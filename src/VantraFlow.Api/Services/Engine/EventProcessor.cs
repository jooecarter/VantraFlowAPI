using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Engine;

/// <summary>What the event processor reads and writes (the SQL one wraps the repositories; tests use a fake).</summary>
public interface IEngineStore
{
    /// <summary>The flows an event could start: the one it names, or the enabled flows listening to its app and scope.</summary>
    Task<IReadOnlyList<FlowRow>> CandidatesAsync(FlowEventRow ev);
    Task<WorkspaceRow?> WorkspaceAsync(Guid workspaceId);
    Task<bool> ClaimKeyAsync(Guid workspaceId, string flowId, string key);
    Task<bool> CountRunAsync(Guid workspaceId, DateTime now, int quota);
    Task<int> RunsSinceAsync(Guid workspaceId, string? flowId, DateTime since);
    Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason);
    Task InsertRunAsync(RunRow run);
}

/// <summary>Done, or put back until a time (the workspace's per-minute rate).</summary>
public sealed record EventOutcome(bool Done, DateTime? DeferUntil = null, int Started = 0);

/// <summary>
/// One event in, its runs out: each flow it starts (<see cref="TriggerMatcher"/>) gets a run, once (the
/// <c>e:{event}</c> claim), within the workspace's monthly quota and per-minute rate, with the loop breaker
/// watching each flow (more than <see cref="FlowLimits.FlowRunsPerMinute"/> runs in a minute pauses it). A run
/// starts from a snapshot of the flow's definition, so editing a flow never changes a run under way.
/// </summary>
public sealed class EventProcessor(IEngineStore store, FlowLimits limits, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    public static string LoopReason(int perMinute) =>
        $"Paused: it started more than {perMinute} runs in a minute, which looks like a loop.";

    public async Task<EventOutcome> ProcessAsync(FlowEventRow ev)
    {
        var flows = await store.CandidatesAsync(ev);
        if (flows.Count == 0) return new EventOutcome(true);
        var ws = await store.WorkspaceAsync(ev.WorkspaceId);
        if (ws is null) return new EventOutcome(true);
        var t = now();
        if (ev.FlowId is null && await store.RunsSinceAsync(ws.Id, null, t.AddMinutes(-1)) >= limits.RunsPerMinute)
            return new EventOutcome(false, t.AddSeconds(30));

        JsonNode? payload;
        try { payload = JsonNode.Parse(ev.Payload); }
        catch (JsonException) { return new EventOutcome(true); }

        var started = 0;
        foreach (var flow in flows)
        {
            var def = FlowDefinition.Parse(flow.Definition);
            if (def is null) continue;
            if (TriggerMatcher.Match(def, flow.Id, ev, payload) is not { } data) continue;
            if (!await store.ClaimKeyAsync(ws.Id, flow.Id, "e:" + ev.Id)) continue;
            if (ev.ReplayOf is null && await store.RunsSinceAsync(ws.Id, flow.Id, t.AddMinutes(-1)) >= limits.FlowRunsPerMinute)
            {
                await store.PauseAsync(ws.Id, flow.Id, LoopReason(limits.FlowRunsPerMinute));
                continue;
            }
            var quota = ws.MonthlyRunQuota ?? limits.MonthlyRuns;
            var allowed = await store.CountRunAsync(ws.Id, t, quota);
            await store.InsertRunAsync(NewRun(flow, def, ws, data, t, ev.Id, ev.ReplayOf, test: false,
                allowed ? null : $"This workspace has used its {quota:N0} runs for the month."));
            started++;
        }
        return new EventOutcome(true, null, started);
    }

    /// <summary>A new run of <paramref name="flow"/> from <paramref name="trigger"/>: queued, or skipped with why.</summary>
    public static RunRow NewRun(FlowRow flow, FlowDefinition def, WorkspaceRow ws, JsonNode trigger, DateTime now, string? eventId, string? replayOf, bool test, string? skipped = null)
    {
        var id = Connectors.FlowSecrets.NewRowId("frn_");
        var data = RunState.NewData(trigger, id, now, ws.Name, ws.TimeZone, test);
        return new RunRow(0, id, ws.Id, flow.Id, flow.Name, flow.Version, def.ToJson(), trigger.ToJsonString(), data.ToJsonString(),
            Positions.Start(def), 0, skipped is null ? RunStatuses.Queued : RunStatuses.Skipped, test, replayOf, eventId, 0, skipped, now,
            skipped is null ? null : now, skipped is null ? null : 0, now);
    }
}

/// <summary>The SQL side of <see cref="IEngineStore"/>.</summary>
public sealed class SqlEngineStore(FlowsRepository flows, WorkspacesRepository workspaces, EngineRepository engine) : IEngineStore
{
    public async Task<IReadOnlyList<FlowRow>> CandidatesAsync(FlowEventRow ev)
    {
        if (ev.FlowId is not null)
            return await flows.GetAsync(ev.WorkspaceId, ev.FlowId) is { } f && (f.Enabled || ev.ReplayOf is not null || ev.Source == EventSources.Manual) ? [f] : [];
        return await flows.ListeningAsync(ev.WorkspaceId, ev.Source, ev.Scope);
    }

    public Task<WorkspaceRow?> WorkspaceAsync(Guid workspaceId) => workspaces.GetAsync(workspaceId);
    public Task<bool> ClaimKeyAsync(Guid workspaceId, string flowId, string key) => engine.ClaimKeyAsync(workspaceId, flowId, key);
    public Task<bool> CountRunAsync(Guid workspaceId, DateTime now, int quota) => engine.CountRunAsync(workspaceId, now, quota);
    public Task<int> RunsSinceAsync(Guid workspaceId, string? flowId, DateTime since) => engine.RunsSinceAsync(workspaceId, flowId, since);
    public Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason) => flows.PauseAsync(workspaceId, flowId, reason);
    public Task InsertRunAsync(RunRow run) => engine.InsertRunAsync(run);
}
