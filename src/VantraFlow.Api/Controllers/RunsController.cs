using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The run log.
/// <code>
/// GET  api/w/{ws}/runs?flowId=&amp;status=&amp;from=&amp;to=&amp;q=&amp;before=&amp;limit=
///                                                       → newest first; q: a run id or text in the trigger data
/// GET  api/w/{ws}/runs/{id}                             → the run, its trigger data, and each step's input, output, error and duration
/// POST api/w/{ws}/runs/{id}/replay                      → a new run of the flow as it is now, from the same trigger data
/// POST api/w/{ws}/runs/{id}/cancel                      → a queued or waiting run stops
/// </code>
/// </summary>
public sealed class RunsController(WorkspacesRepository workspaces, EngineRepository engine, FlowsRepository flows, FlowSignal signal, Services.Billing.Plans plans,
    FlowSchema schema, StepsRepository steps)
    : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/runs")]
    public async Task<ActionResult<IReadOnlyList<RunSummaryDto>>> List(Guid workspaceId, string? flowId, string? status, DateTime? before,
        DateTime? from = null, DateTime? to = null, string? q = null, int limit = 50)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var term = q?.Trim() is { Length: > 0 } t ? (t.Length > 200 ? t[..200] : t) : null;
        var rows = await engine.RunsAsync(workspaceId, string.IsNullOrWhiteSpace(flowId) ? null : flowId, string.IsNullOrWhiteSpace(status) ? null : status,
            Math.Clamp(limit, 1, 200), before?.ToUniversalTime(), from?.ToUniversalTime(), to?.ToUniversalTime(), term);
        return Ok(rows.Select(Summary).ToList());
    }

    [HttpGet("api/w/{workspaceId:guid}/runs/{id}")]
    public async Task<IActionResult> Get(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return await DetailAsync(engine, workspaceId, id) is { } d ? Ok(d) : NotFound(new { error = "That run isn't in the log any more." });
    }

    [HttpPost("api/w/{workspaceId:guid}/runs/{id}/replay")]
    public async Task<IActionResult> Replay(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await engine.RunAsync(workspaceId, id) is not { } run) return NotFound(new { error = "That run isn't in the log any more." });
        if (await flows.GetAsync(workspaceId, run.FlowId) is not { } flow) return NotFound(new { error = "Its flow has been deleted." });
        if (FlowDefinition.Parse(flow.Definition) is not { } def) return BadRequest(new { error = "The flow couldn't be read." });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        // The plan: the month's runs, and premium steps (402, with the tier that has them).
        if (await plans.AdmitRunAsync(ws, def, now) is { } refused)
            return refused.Code == Services.Billing.PlanCodes.Premium
                ? StatusCode(402, new { error = refused.Message, code = refused.Code, upgradeTo = refused.UpgradeTo })
                : StatusCode(429, new { error = refused.Message, code = "quota", upgradeTo = refused.UpgradeTo });
        JsonNode trigger;
        try { trigger = JsonNode.Parse(run.TriggerData) ?? new JsonObject(); } catch (JsonException) { trigger = new JsonObject(); }
        var replay = EventProcessor.NewRun(flow, def, ws, trigger, now, null, run.Id, test: run.Test);
        await engine.InsertRunAsync(replay);
        signal.Poke();
        return StatusCode(202, Summary(replay));
    }

    [HttpPost("api/w/{workspaceId:guid}/runs/{id}/cancel")]
    public async Task<IActionResult> Cancel(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await engine.CancelRunAsync(workspaceId, id, DateTime.UtcNow)) return Conflict(new { error = "Only a queued or waiting run can be cancelled." });
        // An approval it was waiting on stops waiting too (its links then say the run was cancelled).
        if (await schema.StepsReadyAsync()) await steps.CancelApprovalsAsync(id, DateTime.UtcNow);
        return NoContent();
    }

    public static async Task<RunDetailDto?> DetailAsync(EngineRepository engine, Guid workspaceId, string id)
    {
        if (await engine.RunAsync(workspaceId, id) is not { } run) return null;
        var steps = await engine.StepsAsync(run.Id);
        return new RunDetailDto(Summary(run), Parse(run.TriggerData),
            steps.Select(s => new RunStepDto(s.Seq, s.StepId, s.Type, s.App, s.StepAction, s.Name, s.Status, s.Attempt, Parse(s.Input), Parse(s.Output), s.Error,
                Iso(s.StartedAt), s.DurationMs)).ToList(), Parse(run.Definition));
    }

    public static RunSummaryDto Summary(RunRow r) =>
        new(r.Id, r.FlowId, r.FlowName, r.Status, r.Test, r.ReplayOf, Iso(r.StartedAt), Iso(r.FinishedAt), r.DurationMs, r.Error, r.StepCount,
            r.Status is RunStatuses.Waiting or RunStatuses.Queued && r.NotBefore > DateTime.UtcNow ? Iso(r.NotBefore) : null,
            r.Test ? null : r.FlowVersion, // a test runs the builder's unsaved copy
            WaitingFor(r));

    /// <summary>What a waiting run waits on: the step it's at (an approval, a wait until, a delay), or a retry of an action.</summary>
    public static string? WaitingFor(RunRow r)
    {
        if (r.Status != RunStatuses.Waiting && !(r.Status == RunStatuses.Queued && r.Attempt > 0)) return null;
        if (r.Attempt > 0 && r.Status == RunStatuses.Queued) return "retry";
        return FlowDefinition.Parse(r.Definition) is { } def && Positions.StepAt(def, r.Position) is { } step
            ? step.Type is StepTypes.Approval or StepTypes.Wait or StepTypes.Delay ? step.Type : null
            : null;
    }

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return JsonValue.Create(json); }
    }
}
