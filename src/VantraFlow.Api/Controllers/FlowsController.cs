using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Flows: the list, the builder, switching on and off, and testing with sample data.
/// <code>
/// GET    api/w/{ws}/flows                         → summaries with last run, 7-day counts and runs per day
/// POST   api/w/{ws}/flows                         { name, description, definition, templateId } → 201
/// GET    api/w/{ws}/flows/{id}
/// PUT    api/w/{ws}/flows/{id}                    { name, description, definition } → { flow, errors }
/// POST   api/w/{ws}/flows/{id}/enable             { enabled } → the flow, or 422 { errors }, or 402 { code, upgradeTo } (the plan)
/// DELETE api/w/{ws}/flows/{id}
/// POST   api/w/{ws}/flows/{id}/hook-secret        { signed } → { secret } once (a webhook trigger's HMAC secret)
/// POST   api/w/{ws}/flows/{id}/sample             { definition } → { trigger, real } — data to test with
/// GET    api/w/{ws}/flows/{id}/hook-sample        → a webhook trigger's newest payload (db/007; 404 no_sample until one arrives)
/// POST   api/w/{ws}/flows/{id}/test               { definition, trigger? } → the test run, every step for real
/// POST   api/w/{ws}/flows/{id}/test-step          { definition, stepId, data } → that step's log line
/// PUT    api/w/{ws}/flows/{id}/alerts             { on: true | false | null } → the flow's failure alert (db/002; 503 before)
/// GET    api/w/{ws}/flows/{id}/versions           → its saved versions, newest first (db/003; 503 before)
/// GET    api/w/{ws}/flows/{id}/versions/{v}       → one, with its definition
/// POST   api/w/{ws}/flows/{id}/versions/{v}/restore → saved again as the newest version → { flow, errors }
/// </code>
/// Every save (and the first, on create) keeps a version once db/003 has run.
/// </summary>
public sealed class FlowsController(
    WorkspacesRepository workspaces, FlowsRepository flows, EngineRepository engine, Connections connections, IStepActions actions,
    FlowSecrets secrets, IConfiguration config, AlertsRepository alerts, FlowSchema schema, VersionsRepository versions,
    Services.OAuth.OAuthActions oauthActions, Services.Billing.Plans plans,
    StepsRepository steps, FlowSwitch flowSwitch)
    : FlowControllerBase(workspaces)
{
    /// <summary>
    /// 402 with what the plan doesn't cover (<c>plan_flows</c>, <c>plan_premium</c>) and the cheapest tier that does
    /// (<c>upgradeTo</c>), so the studio can offer the upgrade.
    /// </summary>
    private ObjectResult PlanRefused(Services.Billing.PlanBlock block, string? message = null) =>
        StatusCode(402, new { error = message ?? block.Message, code = block.Code, upgradeTo = block.UpgradeTo });

    private FlowLimits Limits => FlowLimits.From(config);

    [HttpGet("api/w/{workspaceId:guid}/flows")]
    public async Task<ActionResult<IReadOnlyList<FlowSummaryDto>>> List(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var now = DateTime.UtcNow;
        var stats = (await flows.StatsAsync(workspaceId, now.AddDays(-7))).ToDictionary(s => s.FlowId);
        var days = FlowDays.Spread(await flows.DailyAsync(workspaceId, FlowDays.Since(now)), now);
        return Ok((await flows.ListAsync(workspaceId)).Select(f =>
        {
            var d = days.TryGetValue(f.Id, out var x) ? x : (Runs: new int[FlowDays.Count], Failures: new int[FlowDays.Count]);
            return Summary(f, stats.GetValueOrDefault(f.Id)) with { Daily7d = d.Runs, DailyFailures7d = d.Failures };
        }).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/flows")]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] SaveFlowBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var def = Definition(body.Definition) ?? new FlowDefinition();
        var name = Name(body.Name) ?? "Untitled flow";
        var now = DateTime.UtcNow;
        var row = Shape(new FlowRow(FlowSecrets.NewRowId("flw_"), workspaceId, name, Desc(body.Description), false, def.ToJson(), 1, "", "", null, null, null,
            body.TemplateId is { Length: <= 40 } t ? t : null, null, Sub, now, Sub, now, null, null), def);
        await flows.InsertAsync(row);
        var saved = (await flows.GetAsync(workspaceId, row.Id))!;
        await KeepVersionAsync(saved, null);
        return StatusCode(201, new { flow = await DtoAsync(saved), errors = DefinitionRules.Validate(def, Limits) });
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Get(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return await flows.GetAsync(workspaceId, id) is { } f ? Ok(await DtoAsync(f)) : NoFlow();
    }

    [HttpPut("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Save(Guid workspaceId, string id, [FromBody] SaveFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        return await SaveAsync(f, Name(body.Name) ?? f.Name, body.Description is null ? f.Description : Desc(body.Description), def, null, ct);
    }

    /// <summary>
    /// Saves a definition (an edit, or a restored version): refused while the flow is on and has problems; the apps'
    /// webhooks kept in step with its trigger; a version kept. → <c>{ flow, errors }</c>.
    /// </summary>
    private async Task<IActionResult> SaveAsync(FlowRow f, string name, string? description, FlowDefinition def, string? note, CancellationToken ct)
    {
        var (workspaceId, id) = (f.WorkspaceId, f.Id);
        var errors = DefinitionRules.Validate(def, Limits);
        if (f.Enabled && errors.Count > 0)
            return UnprocessableEntity(new { error = "Fix these before saving, or switch the flow off first.", errors });
        // A flow that's on can't take on premium steps its plan doesn't include (switched off, it can).
        if (f.Enabled && await Workspaces.GetAsync(workspaceId) is { } wsRow && await plans.CanSaveOnAsync(wsRow, def, DateTime.UtcNow) is { } block)
            return PlanRefused(block, block.Message.Replace(" Upgrade to switch this flow on.", " Switch the flow off to save this, or upgrade."));
        var updated = Shape(f with { Name = name, Description = description, UpdatedBy = Sub, UpdatedAt = DateTime.UtcNow }, def);
        await flows.UpdateAsync(updated);
        var saved = (await flows.GetAsync(workspaceId, id))!;
        await KeepVersionAsync(saved, note);
        if (f.Enabled)
        {
            await JoinSlackAsync(workspaceId, def, ct);
            // The trigger may now listen elsewhere: keep the apps' webhooks in step.
            try { await SyncAsync(workspaceId, f.TriggerApp, updated.TriggerApp, ct); }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
            {
                await flows.SetEnabledAsync(workspaceId, id, false, Sub);
                return StatusCode(502, new { error = $"Saved, but switched off: {e.Message}", code = "subscribe_failed" });
            }
        }
        return Ok(new { flow = await DtoAsync((await flows.GetAsync(workspaceId, id))!), errors });
    }

    /// <summary>A copy of the flow as just saved, once db/003 has run (before, nothing is kept and nothing fails).</summary>
    private async Task KeepVersionAsync(FlowRow saved, string? note)
    {
        if (!await schema.VersionsReadyAsync()) return;
        var def = FlowDefinition.Parse(saved.Definition) ?? new FlowDefinition();
        await versions.InsertAsync(new FlowVersionRow(saved.WorkspaceId, saved.Id, saved.Version, saved.Name, saved.Definition, def.AllSteps().Count(), note,
            Sub, DisplayName, DateTime.UtcNow));
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{id}/versions")]
    public async Task<IActionResult> Versions(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.VersionsReadyAsync()) return VersionsNotReady();
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        var list = (await versions.ListAsync(workspaceId, id))
            .Select(v => new FlowVersionDto(v.Version, v.Name, Iso(v.SavedAt), v.SavedByName, v.StepCount, v.Note, v.Version == f.Version)).ToList();
        // A flow last saved before db/003 ran: the version it is now, though no copy of it was kept.
        if (!list.Any(v => v.Current))
            list.Insert(0, new FlowVersionDto(f.Version, f.Name, Iso(f.UpdatedAt), null, (FlowDefinition.Parse(f.Definition) ?? new FlowDefinition()).AllSteps().Count(), null, true));
        return Ok(list);
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{id}/versions/{version:int}")]
    public async Task<IActionResult> Version(Guid workspaceId, string id, int version)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.VersionsReadyAsync()) return VersionsNotReady();
        if (await versions.GetAsync(workspaceId, id, version) is not { } v) return NoVersion();
        var def = FlowDefinition.Parse(v.Definition) ?? new FlowDefinition();
        return Ok(new FlowVersionDetailDto(v.Version, v.Name, Iso(v.SavedAt), v.SavedByName, v.Note, JsonSerializer.SerializeToNode(def, FlowDefinition.Json)!));
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/versions/{version:int}/restore")]
    public async Task<IActionResult> Restore(Guid workspaceId, string id, int version, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.VersionsReadyAsync()) return VersionsNotReady();
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        if (await versions.GetAsync(workspaceId, id, version) is not { } v || FlowDefinition.Parse(v.Definition) is not { } def) return NoVersion();
        return await SaveAsync(f, v.Name, f.Description, def, $"Restored version {version}", ct);
    }

    private ObjectResult VersionsNotReady() => StatusCode(503, new { error = "Versions arrive with the next server update.", code = "not_ready" });
    private NotFoundObjectResult NoVersion() => NotFound(new { error = "That version isn't kept any more.", code = "no_version" });

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/enable")]
    public async Task<IActionResult> Enable(Guid workspaceId, string id, [FromBody] ToggleFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        // The checks before it goes on (its definition, its apps, the plan) and the apps' webhooks: FlowSwitch, shared with the API.
        var outcome = await flowSwitch.SetAsync(f, body.Enabled, Sub, ct);
        if (outcome.Errors is { } errors) return UnprocessableEntity(new { error = "This flow can't run yet.", errors });
        if (outcome.Plan is { } block) return PlanRefused(block);
        if (outcome.SubscribeFailed is { } why) return StatusCode(502, new { error = why, code = "subscribe_failed" });
        return Ok(await DtoAsync(outcome.Flow!));
    }

    [HttpDelete("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Delete(Guid workspaceId, string id, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoContent();
        await flows.DeleteAsync(workspaceId, id);
        if (await schema.VersionsReadyAsync()) await versions.DeleteAsync(workspaceId, id);
        try { await SyncAsync(workspaceId, f.TriggerApp, f.TriggerApp, ct); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { /* tidied on the next change */ }
        return NoContent();
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/hook-secret")]
    public async Task<IActionResult> HookSecret(Guid workspaceId, string id, [FromBody] HookSecretBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { HookId: not null } f) return NotFound(new { error = "This flow has no webhook trigger." });
        string? secret = body.Signed ? FlowSecrets.NewSecret() : null;
        await flows.UpdateAsync(f with { HookSecretCipher = secret is null ? null : secrets.Protect(secret, workspaceId, f.HookId), UpdatedBy = Sub, UpdatedAt = DateTime.UtcNow });
        return Ok(new { secret });
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/sample")]
    public async Task<IActionResult> Sample(Guid workspaceId, string id, [FromBody] TestFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var real = def.Trigger.App == "webhook" ? await HookSampleAsync(workspaceId, id) : await connections.SampleAsync(workspaceId, def.Trigger, ct);
        return Ok(new { trigger = real ?? FlowCatalog.Sample(def.Trigger, DateTime.UtcNow), real = real is not null });
    }

    /// <summary>
    /// A webhook trigger's newest payload (db/007), also one sent while the flow is off: <c>{ trigger, receivedAt }</c>, or
    /// 404 <c>no_sample</c> until one arrives. The builder asks again while it listens.
    /// </summary>
    [HttpGet("api/w/{workspaceId:guid}/flows/{id}/hook-sample")]
    public async Task<IActionResult> HookSample(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.StepsReadyAsync()) return StatusCode(503, new { error = "Webhook samples arrive with the next server update.", code = "not_ready" });
        if (await steps.HookSampleAsync(workspaceId, id) is not { } row) return NotFound(new { error = "Nothing has arrived yet.", code = "no_sample" });
        JsonNode? trigger;
        try { trigger = JsonNode.Parse(row.Payload); } catch (JsonException) { trigger = null; }
        return Ok(new { trigger, receivedAt = Iso(row.ReceivedAt) });
    }

    private async Task<JsonObject?> HookSampleAsync(Guid workspaceId, string flowId)
    {
        if (!await schema.StepsReadyAsync() || await steps.HookSampleAsync(workspaceId, flowId) is not { } row) return null;
        try { return JsonNode.Parse(row.Payload) as JsonObject; } catch (JsonException) { return null; }
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/test")]
    public async Task<IActionResult> Test(Guid workspaceId, string id, [FromBody] TestFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var errors = DefinitionRules.Validate(def, Limits);
        if (errors.Count > 0) return UnprocessableEntity(new { error = "Fix these to test the flow.", errors });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        var trigger = body.Trigger as JsonObject
            ?? (def.Trigger.App == "webhook" ? await HookSampleAsync(workspaceId, id) : await connections.SampleAsync(workspaceId, def.Trigger, ct))
            ?? FlowCatalog.Sample(def.Trigger, now);
        var run = EventProcessor.NewRun(f, def, ws, trigger, now, null, null, test: true);
        await engine.InsertRunAsync(run);
        var state = FlowWorker.State(run)!;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        IReadOnlyList<StepLog> logs;
        // A loop comes round once per item (a test goes through the first few), so a test may take more goes than steps.
        try { logs = await new RunExecutor(actions, Limits).ExecuteAsync(state, budget.Token, maxSteps: Limits.MaxSteps * (LoopRules.TestItems + 1) + 10); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logs = [];
            state.Status = RunStatuses.Failed;
            state.Error = "The test took longer than a minute.";
        }
        await engine.SaveSliceAsync(state, logs, now, DateTime.UtcNow);
        return Ok(await RunsController.DetailAsync(engine, workspaceId, run.Id));
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/test-step")]
    public async Task<IActionResult> TestStep(Guid workspaceId, string id, [FromBody] TestStepBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var step = def.AllSteps().FirstOrDefault(s => s.Id == body.StepId);
        if (step is null) return NotFound(new { error = "That step isn't in this flow." });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var runId = FlowSecrets.NewRowId("tst_");
        var data = body.Data as JsonObject ?? RunState.NewData(FlowCatalog.Sample(def.Trigger, DateTime.UtcNow), runId, DateTime.UtcNow, ws.Name, ws.TimeZone, true);
        // Just this step: a one-step flow over the data the earlier steps' tests left. A step inside a Repeat for each
        // runs for the list's first item; a Repeat for each runs its steps for the first few items.
        var single = new FlowDefinition { Trigger = def.Trigger, Steps = [Clone(step)], Options = def.Options };
        var state = new RunState
        {
            Id = runId, WorkspaceId = workspaceId, FlowId = id, Definition = single, Data = (JsonObject)data.DeepClone(),
            Position = Positions.First, Test = true, NotBefore = DateTime.UtcNow,
        };
        if (state.Data["loop"] is null && LoopAround(def.Steps, step.Id) is { } loop
            && LoopRules.ListOf(Tokens.RenderNode(loop.Config["items"], state.Data)) is { Count: > 0 } items)
            state.Data["loop"] = new JsonObject { ["item"] = items[0]?.DeepClone(), ["index"] = 0, ["number"] = 1, ["count"] = Math.Min(items.Count, LoopRules.MaxOf(loop.Config)) };
        var goes = step.Type == StepTypes.Loop ? ((step.Do?.Count ?? 0) * 3 + 2) * (LoopRules.TestItems + 1) : 1;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        IReadOnlyList<StepLog> logs;
        try { logs = await new RunExecutor(actions, Limits).ExecuteAsync(state, budget.Token, maxSteps: goes); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logs = [];
            state.Status = RunStatuses.Failed;
            state.Error = "The test took longer than a minute.";
        }
        var log = logs.LastOrDefault(l => l.StepId == step.Id) ?? logs.FirstOrDefault();
        return Ok(new
        {
            status = log?.Status ?? state.Status,
            input = Parse(log?.Input),
            output = state.Steps[step.Id]?.DeepClone(),
            error = log?.Error ?? state.Error,
            durationMs = log?.DurationMs ?? 0,
        });
    }

    /// <summary>
    /// "Email me when this flow fails": on (the alert goes to the caller), off, or null (the workspace's default, which
    /// emails the flow's creator). Any member may; turning it on makes it theirs.
    /// </summary>
    [HttpPut("api/w/{workspaceId:guid}/flows/{id}/alerts")]
    public async Task<IActionResult> Alerts(Guid workspaceId, string id, [FromBody] FlowAlertsBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.AlertsReadyAsync())
            return StatusCode(503, new { error = "Failure alerts arrive with the next server update.", code = "not_ready" });
        if (await flows.GetAsync(workspaceId, id) is null) return NoFlow();
        await alerts.SetFlowAsync(workspaceId, id, body.On, Sub);
        return Ok(await AlertsDtoAsync(workspaceId, id));
    }

    // ── Helpers ──

    private async Task<FlowAlertsDto?> AlertsDtoAsync(Guid workspaceId, string flowId)
    {
        if (!await schema.AlertsReadyAsync()) return null;
        if (await alerts.SettingAsync(workspaceId, flowId) is not { } a) return null;
        var to = a.To ?? a.CreatedBy;
        var member = (await Workspaces.MembersAsync(workspaceId)).FirstOrDefault(m => m.VantraId == to);
        return new FlowAlertsDto(a.On ?? a.WorkspaceDefault, a.On, a.WorkspaceDefault, member is null ? null : member.Name ?? member.Email, to == Sub);
    }

    /// <summary>A Slack trigger hears a channel only when the app is in it: join a public one (a private one needs an invite).</summary>
    private async Task JoinSlackAsync(Guid workspaceId, FlowDefinition def, CancellationToken ct)
    {
        if (def.Trigger.App == "slack" && Tokens.Text(def.Trigger.Config["channel"]) is { Length: > 0 } channel && await schema.ConnectorsReadyAsync())
            await oauthActions.JoinSlackChannelAsync(workspaceId, channel, ct);
    }

    /// <summary>Sync the apps whose webhooks a change may affect (the trigger's app before and after).</summary>
    private async Task SyncAsync(Guid workspaceId, string before, string after, CancellationToken ct)
    {
        foreach (var app in new[] { before, after }.Distinct())
            if (app is "orbit" or "projects") await connections.SyncSubscriptionsAsync(workspaceId, app, ct);
    }

    /// <summary>The row's trigger columns from its definition, and a webhook trigger's address made when it first needs one.</summary>
    private static FlowRow Shape(FlowRow f, FlowDefinition def)
    {
        var t = def.Trigger;
        var scope = t.App switch
        {
            "orbit" => Tokens.Text(t.Config["tableId"]) is { Length: > 0 and <= 64 } s ? s : null,
            "slack" => Tokens.Text(t.Config["channel"]) is { Length: > 0 and <= 64 } c ? c : null,
            _ => null,
        };
        var hook = t.App == "webhook" ? f.HookId ?? FlowSecrets.NewId("fhk_") : null;
        return f with
        {
            Definition = def.ToJson(), TriggerApp = t.App.Length > 0 ? t.App : "none", TriggerEvent = t.Event.Length > 0 ? t.Event : "none",
            TriggerScope = scope, HookId = hook, HookSecretCipher = hook is null ? null : f.HookSecretCipher,
        };
    }

    private async Task<FlowDto> DtoAsync(FlowRow f)
    {
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        var tz = (await Workspaces.GetAsync(f.WorkspaceId))?.TimeZone;
        var next = f.Enabled && def.Trigger.App == "schedule" ? ScheduleRules.Next(def.Trigger, tz, DateTime.UtcNow) : null;
        return new FlowDto(f.Id, f.Name, f.Description, f.Enabled, JsonSerializer.SerializeToNode(def, FlowDefinition.Json)!, f.Version,
            f.HookId is null ? null : $"{connections.ApiBase}/api/hooks/{f.HookId}", f.HookSecretCipher is not null, f.PausedReason, f.TemplateId,
            Iso(next), Iso(f.UpdatedAt), Iso(f.LastRunAt), f.LastRunStatus, await AlertsDtoAsync(f.WorkspaceId, f.Id));
    }

    private static FlowSummaryDto Summary(FlowRow f, FlowStatsRow? s)
    {
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        var apps = new List<string> { def.Trigger.App };
        apps.AddRange(def.AllSteps().Where(x => x.Type == StepTypes.Action && x.App is not null).Select(x => x.App!));
        return new FlowSummaryDto(f.Id, f.Name, f.Description, f.Enabled, f.TriggerApp, f.TriggerEvent, apps.Where(a => a.Length > 0).Distinct().ToList(),
            def.AllSteps().Count(), Iso(f.LastRunAt), f.LastRunStatus, s?.Runs ?? 0, s?.Failures ?? 0, Iso(f.UpdatedAt), f.PausedReason, f.TemplateId);
    }

    private static FlowDefinition? Definition(JsonNode? node)
    {
        if (node is not JsonObject) return null;
        try { return node.Deserialize<FlowDefinition>(FlowDefinition.Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The Repeat for each a step is inside, if any.</summary>
    private static StepDef? LoopAround(IEnumerable<StepDef> steps, string stepId, StepDef? loop = null)
    {
        foreach (var s in steps)
        {
            if (s.Id == stepId) return loop;
            foreach (var side in s.Children())
                if (LoopAround(side, stepId, s.Type == StepTypes.Loop && side == s.Do ? s : loop) is { } found) return found;
        }
        return null;
    }

    private static StepDef Clone(StepDef s) => JsonSerializer.Deserialize<StepDef>(JsonSerializer.Serialize(s, FlowDefinition.Json), FlowDefinition.Json)!;

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return JsonValue.Create(json); }
    }

    private static string? Name(string? n) => n?.Trim() is { Length: > 0 } s ? (s.Length > 160 ? s[..160] : s) : null;
    private static string? Desc(string? d) => d?.Trim() is { Length: > 0 } s ? (s.Length > 400 ? s[..400] : s) : null;
    private NotFoundObjectResult NoFlow() => NotFound(new { error = "That flow doesn't exist any more.", code = "no_flow" });
}
