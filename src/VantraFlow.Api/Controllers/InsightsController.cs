using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Insights;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Insights: how the workspace's flows are doing.
/// <code>
/// GET api/w/{ws}/insights?days=7|30|90&amp;flowId=   → runs per day, success rate, durations, busiest and failing flows,
///                                                    failing steps, time saved, this month's runs against the limit
/// PUT api/w/{ws}/insights/time-saved              { minutesPerStep, flows: [{ flowId, minutesPerRun }] } (owners and admins; db/006)
/// </code>
/// Any member reads; owners and admins set the time-saved figures. Before db/006 has run, Insights reads only the runs
/// still kept (<c>Flow:KeepRunsDays</c>) and the figures can't be changed (503 <c>not_ready</c>).
/// </summary>
public sealed class InsightsController(WorkspacesRepository workspaces, InsightsRepository insights, FlowSchema schema, IRunLimits limits, IConfiguration config)
    : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/insights")]
    public async Task<IActionResult> Get(Guid workspaceId, int? days, string? flowId, CancellationToken ct)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        var range = InsightsReport.RangeOf(days);
        var first = InsightsReport.Since(now, range);
        var flow = string.IsNullOrWhiteSpace(flowId) ? null : flowId.Trim();
        if (flow is { Length: > 40 }) return BadRequest(new { error = "That isn't a flow." });
        var kept = await schema.InsightsReadyAsync();

        // The period before is read too, for the change in runs and success rate.
        var rows = await insights.DaysAsync(workspaceId, first.AddDays(-range), kept);
        var durations = await insights.DurationsAsync(workspaceId, first);
        var flows = await insights.FlowsAsync(workspaceId, kept);
        var (steps, errors) = await insights.FailingStepsAsync(workspaceId, first, flow);
        var perStep = kept ? await insights.MinutesPerStepAsync(workspaceId) : null;
        var used = await insights.RunsThisMonthAsync(workspaceId, now);
        var quota = await limits.MonthlyRunsAsync(ws, ct);
        var keptDays = int.TryParse(config["Flow:KeepRunsDays"], out var d) ? Math.Clamp(d, 7, 365) : 30;

        Response.Headers.CacheControl = "private, no-store";
        return Ok(InsightsReport.Build(now, range, flow, rows, durations, flows, steps, errors, perStep, kept && FlowRoles.CanManage(role),
            used, quota, keptDays, kept));
    }

    [HttpPut("api/w/{workspaceId:guid}/insights/time-saved")]
    public async Task<IActionResult> SetTimeSaved(Guid workspaceId, [FromBody] TimeSavedBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await schema.InsightsReadyAsync())
            return StatusCode(503, new { error = "Time saved settings arrive with the next server update.", code = "not_ready" });
        var known = (await insights.FlowsAsync(workspaceId, true)).Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        var given = (body.Flows ?? []).Take(500).ToList();
        if (given.Any(f => !known.Contains(f.FlowId ?? ""))) return NotFound(new { error = "One of those flows isn't in this workspace.", code = "no_flow" });
        await insights.SetTimeSavedAsync(workspaceId, InsightsReport.CleanMinutes(body.MinutesPerStep, InsightsReport.MaxMinutesPerStep),
            given.Select(f => (f.FlowId, InsightsReport.CleanMinutes(f.MinutesPerRun, InsightsReport.MaxMinutesPerRun))).ToList());
        return NoContent();
    }
}
