using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>One record a date flow will run for next, for the builder's "Coming up".</summary>
public sealed record ComingUpDto(string RecordId, string Title, string FireAt);

/// <summary>
/// The date triggers' scan (db/014), from the worker's minute tick: each enabled date flow's cursor is claimed under a lease,
/// its app asked for the records whose date can fall in the window since the last scan (Orbit's REST API v1 with range
/// filters; Projects' due tasks), and each one due fires once — the ledger's key (flow, record, date value, offset) spent,
/// then a run through <see cref="TriggerRuns"/> (the plan, the loop breaker, a paused flow skipped). After downtime it reaches
/// back a day at most; a new or re-saved flow starts from then. An app that can't be reached is tried again later, backing off.
/// </summary>
public sealed class DateTriggers(
    TriggersRepository repo, FlowsRepository flows, WorkspacesRepository workspaces, Connections connections, TriggerRuns runs, ILogger<DateTriggers> log)
{
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(4);

    /// <summary>A source's records, read for a range of days, and how to read each.</summary>
    private sealed record Source(List<JsonObject> Rows, Func<JsonObject, string> Id, Func<JsonObject, JsonNode?> Value, Func<JsonObject, bool> Done,
        Func<JsonObject, DateRules.DateValue, DateTime, JsonObject> Data, Func<JsonObject, string> Title);

    public async Task RunDueAsync(string me, DateTime now, CancellationToken ct)
    {
        if (!await repo.ReadyAsync()) return;
        foreach (var cursor in await repo.ClaimCursorsAsync(me, now, 20, Lease))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var until = await ScanAsync(cursor, DateTime.UtcNow, ct);
                await repo.FinishCursorAsync(cursor.FlowId, until, DateTime.UtcNow.Add(DateRules.Every), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
            {
                var wait = TimeSpan.FromMinutes(Math.Min(60, 3 * Math.Pow(2, Math.Min(cursor.Failures, 5))));
                log.LogInformation("Date scan for flow {Flow} couldn't reach its app; trying again in {Minutes} minutes", cursor.FlowId, (int)wait.TotalMinutes);
                await repo.FinishCursorAsync(cursor.FlowId, null, DateTime.UtcNow.Add(wait), e.Message);
            }
            catch (Exception e)
            {
                log.LogWarning(e, "Date scan for flow {Flow} failed", cursor.FlowId);
                await repo.FinishCursorAsync(cursor.FlowId, null, DateTime.UtcNow.AddMinutes(10), "The scan failed.");
            }
        }
    }

    /// <summary>One flow's scan: what came due in (start, now] fires. Returns how far it got.</summary>
    private async Task<DateTime?> ScanAsync(DateCursorRow cursor, DateTime now, CancellationToken ct)
    {
        if (await flows.GetAsync(cursor.WorkspaceId, cursor.FlowId) is not { Enabled: true } flow) return now;
        if (FlowDefinition.Parse(flow.Definition) is not { } def || !DateRules.IsDateTrigger(def.Trigger)) return now;
        var ws = await workspaces.GetAsync(flow.WorkspaceId);
        var spec = DateRules.Read(def.Trigger, ws?.TimeZone ?? "UTC");
        var from = DateRules.WindowStart(now, cursor.ScannedUntil, new[] { flow.UpdatedAt, cursor.StartedAt }.Max());
        if (from >= now) return now;
        var overdue = def.Trigger.Event == DateRules.Overdue;
        var source = await SourceAsync(flow.WorkspaceId, def.Trigger, spec, DateRules.ValueRange(spec, from, now), ct);
        if (source is null) return now;
        var due = DateRules.Due(spec, overdue, source.Rows, source.Id, source.Value, source.Done, source.Data, from, now);
        foreach (var d in due)
        {
            if (!await repo.ClaimFiringAsync(flow.WorkspaceId, flow.Id, d.RecordId, d.DateValue, spec.OffsetKey, d.FireAt)) continue;
            var started = await runs.StartAsync(flow, d.Data, null, now, logSkipped: true);
            if (started.RunId is { } runId) await repo.FiringRunAsync(flow.Id, d.RecordId, d.DateValue, spec.OffsetKey, runId);
            if (started.Code is "paused" or "off") break;
        }
        return now;
    }

    /// <summary>The next few records a date flow will run for, from now (the builder's "Coming up").</summary>
    public async Task<IReadOnlyList<ComingUpDto>> ComingUpAsync(Guid workspaceId, FlowDefinition def, string timeZone, int take, DateTime now, CancellationToken ct)
    {
        if (!DateRules.IsDateTrigger(def.Trigger)) return [];
        var spec = DateRules.Read(def.Trigger, timeZone);
        if (def.Trigger.App == "orbit" && (spec.Field.Length == 0 || Tokens.Text(def.Trigger.Config["tableId"]).Length == 0)) return [];
        var to = now.Add(DateRules.Ahead);
        var source = await SourceAsync(workspaceId, def.Trigger, spec, DateRules.ValueRange(spec, now, to), ct);
        if (source is null) return [];
        var due = DateRules.Due(spec, def.Trigger.Event == DateRules.Overdue, source.Rows, source.Id, source.Value, source.Done, source.Data, now, to);
        var byId = source.Rows.GroupBy(source.Id).ToDictionary(g => g.Key, g => g.First());
        return due.Take(take).Select(d => new ComingUpDto(d.RecordId, byId.TryGetValue(d.RecordId, out var r) ? source.Title(r) : d.RecordId, Formatter.IsoUtc(d.FireAt))).ToList();
    }

    private async Task<Source?> SourceAsync(Guid workspaceId, TriggerDef t, DateSpec spec, (DateOnly From, DateOnly To) range, CancellationToken ct)
    {
        if (t.App == "orbit")
        {
            var tableId = Tokens.Text(t.Config["tableId"]).Trim();
            if (tableId.Length == 0 || spec.Field.Length == 0) return null;
            return await connections.CallAsync(workspaceId, "orbit", async (api, key) =>
            {
                var orbit = (OrbitApi)api;
                var table = await orbit.TableAsync(key, tableId, ct);
                var fields = (table["fields"] as JsonArray ?? []).OfType<JsonObject>().ToList();
                var field = fields.FirstOrDefault(f => Tokens.Text(f["id"]) == spec.Field) ?? fields.FirstOrDefault(f => Tokens.Text(f["name"]) == spec.Field);
                if (field is null) throw new AppRefusedException(400, "The date field isn't in the table any more.", "no_field");
                var name = Tokens.Text(field["name"]);
                var primary = fields.FirstOrDefault(f => Tokens.Text(f["id"]) == Tokens.Text(table["primaryFieldId"]));
                var primaryName = primary is null ? null : Tokens.Text(primary["name"]);
                var rows = await orbit.DateRangeAsync(key, tableId, Tokens.Text(field["id"]), range.From, range.To, ct);
                var tableNode = new JsonObject { ["id"] = tableId, ["name"] = table["name"]?.DeepClone() };
                return new Source(rows, r => Tokens.Text(r["id"]), r => r["fields"]?[name], _ => false,
                    (r, v, at) => new JsonObject
                    {
                        ["event"] = DateRules.Relative,
                        ["table"] = tableNode.DeepClone(),
                        ["record"] = r.DeepClone(),
                        ["date"] = new JsonObject { ["field"] = name, ["value"] = v.Key },
                        ["scheduledFor"] = Formatter.IsoUtc(at),
                    },
                    r => primaryName is not null && Tokens.Text(r["fields"]?[primaryName]) is { Length: > 0 } title ? title : Tokens.Text(r["id"]));
            });
        }
        if (t.App != "projects") return null;
        return await connections.CallAsync(workspaceId, "projects", async (api, key) =>
        {
            var rows = await ((ProjectsApi)api).TasksDueAsync(key, range.From, range.To, ct);
            return new Source(rows, r => Tokens.Text(r["id"]), r => r["dueDate"], r => r["done"] is JsonValue dv && dv.TryGetValue<bool>(out var done) && done,
                (r, v, at) => new JsonObject
                {
                    ["event"] = t.Event,
                    ["task"] = r.DeepClone(),
                    ["project"] = new JsonObject { ["id"] = r["projectId"]?.DeepClone(), ["name"] = r["projectName"]?.DeepClone() },
                    ["dueDate"] = v.Key,
                    ["link"] = r["url"]?.DeepClone(),
                    ["scheduledFor"] = Formatter.IsoUtc(at),
                },
                r => Tokens.Text(r["name"]) is { Length: > 0 } n ? n : Tokens.Text(r["id"]));
        });
    }
}
