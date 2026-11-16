using System.Text.Json.Nodes;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>A run a form, a button or a date started, or why it didn't (<see cref="Code"/>: off, duplicate, busy, paused, quota, premium).</summary>
public sealed record TriggerStart(string? RunId, string? Code = null, string? Message = null)
{
    public bool Started => RunId is not null && Code is null;
}

/// <summary>
/// Starts one run of a flow straight away, for the triggers that name their flow (a form's answer, a button, a date): the
/// same gates as an app event (<see cref="EventProcessor"/>) — the flow is on, the claim is spent once, the workspace's
/// per-minute rate, the loop breaker (more than <see cref="FlowLimits.FlowRunsPerMinute"/> runs of one flow in a minute
/// pauses it) and the plan (its run quota, its premium steps). A refused form or button run isn't logged (the person
/// is told, and can try again); a refused date run is logged as skipped, as an app event's is.
/// </summary>
public sealed class TriggerRuns(IEngineStore store, FlowLimits limits, Action? poke = null)
{
    public async Task<TriggerStart> StartAsync(FlowRow flow, JsonObject trigger, string? claimKey, DateTime now, bool logSkipped = false)
    {
        if (!flow.Enabled) return new TriggerStart(null, "off", "This flow is switched off.");
        if (FlowDefinition.Parse(flow.Definition) is not { } def) return new TriggerStart(null, "off", "This flow can't be read.");
        if (await store.WorkspaceAsync(flow.WorkspaceId) is not { } ws) return new TriggerStart(null, "off", "This flow's workspace is gone.");
        if (await store.RunsSinceAsync(ws.Id, null, now.AddMinutes(-1)) >= limits.RunsPerMinute)
            return new TriggerStart(null, "busy", "This workspace is busy. Try again in a minute.");
        if (claimKey is not null && !await store.ClaimKeyAsync(ws.Id, flow.Id, claimKey)) return new TriggerStart(null, "duplicate", "That was sent already.");
        if (await store.RunsSinceAsync(ws.Id, flow.Id, now.AddMinutes(-1)) >= limits.FlowRunsPerMinute)
        {
            await store.PauseAsync(ws.Id, flow.Id, EventProcessor.LoopReason(limits.FlowRunsPerMinute));
            return new TriggerStart(null, "paused", "This flow was paused: it started too many runs in a minute.");
        }
        var skipped = await store.AdmitAsync(ws, def, now, ws.MonthlyRunQuota ?? limits.MonthlyRuns);
        if (skipped is not null)
        {
            var code = skipped.StartsWith("Skipped:", StringComparison.Ordinal) ? "premium" : "quota";
            if (!logSkipped) return new TriggerStart(null, code, skipped);
            var logged = EventProcessor.NewRun(flow, def, ws, trigger, now, null, null, test: false, skipped);
            await store.InsertRunAsync(logged);
            return new TriggerStart(logged.Id, code, skipped);
        }
        var run = EventProcessor.NewRun(flow, def, ws, trigger, now, null, null, test: false);
        await store.InsertRunAsync(run);
        poke?.Invoke();
        return new TriggerStart(run.Id);
    }
}
