using System.Globalization;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Insights;

// ── What the repository reads ───────────────────────────────────────────────

/// <summary>One flow's runs on one day (UTC): from the runs still kept and the daily totals kept after them (db/006).</summary>
public sealed record InsightDayRow(DateTime Day, string FlowId, string FlowName, int Succeeded, int Failed, int Other, int Steps);

/// <summary>How long a flow's finished runs took (null FlowId: all of them).</summary>
public sealed record InsightDurationRow(string? FlowId, double MedianMs, double P95Ms, int Measured);

/// <summary>A workspace's flow as Insights names it, with its own minutes saved per run (db/006).</summary>
public sealed record InsightFlowRow(string Id, string Name, bool Enabled, string TriggerApp, decimal? MinutesPerRun);

/// <summary>A step that failed in the range: how often, and the latest failure.</summary>
public sealed record InsightStepRow(string FlowId, string StepId, string Name, string Type, string? App, string? StepAction, int Failures, DateTime LastAt,
    string LastRunId, string? LastError);

/// <summary>One of a failing step's errors, and how often it came up.</summary>
public sealed record InsightErrorRow(string FlowId, string StepId, string Error, int Count);

// ── What the studio gets ──────────────────────────────────────────────────

public sealed record InsightsDto(
    int Days, string From, string To, string? FlowId,
    IReadOnlyList<InsightsDayDto> Daily,
    InsightsTotalsDto Totals,
    InsightsTotalsDto Previous,
    IReadOnlyList<InsightsFlowDto> Flows,
    IReadOnlyList<InsightsStepDto> FailingSteps,
    InsightsTimeSavedDto TimeSaved,
    InsightsUsageDto Usage,
    int KeptDays,
    bool DailyKept);

public sealed record InsightsDayDto(string Day, int Succeeded, int Failed, int Other);

public sealed record InsightsTotalsDto(int Runs, int Succeeded, int Failed, int Other, double? SuccessRate, double? MedianMs, double? P95Ms);

public sealed record InsightsFlowDto(string Id, string Name, bool Exists, bool Enabled, string? TriggerApp, int Runs, int Succeeded, int Failed,
    double? FailureRate, double? MedianMs, decimal? MinutesPerRun, double MinutesSaved);

public sealed record InsightsStepDto(string FlowId, string FlowName, string StepId, string Name, string Type, string? App, string? Action, int Failures,
    string LastAt, string LastRunId, IReadOnlyList<InsightsErrorDto> Errors);

public sealed record InsightsErrorDto(string Message, int Count);

/// <summary>
/// Time saved: <see cref="Minutes"/> in all, from <see cref="Steps"/> steps in succeeded runs at
/// <see cref="MinutesPerStep"/> each, or a flow's own minutes per run where it has one. <see cref="Editable"/>: the caller
/// may change the figures (an owner or admin, once db/006 has run).
/// </summary>
public sealed record InsightsTimeSavedDto(double Minutes, int Steps, decimal MinutesPerStep, bool Custom, bool Editable);

/// <summary>This month's runs against the workspace's monthly runs.</summary>
public sealed record InsightsUsageDto(string Month, int Runs, int Quota, string ResetsOn);

/// <summary>Body of <c>PUT …/insights/time-saved</c>: the workspace's minutes per step, and flows' own minutes per run (null: the default).</summary>
public sealed record TimeSavedBody(decimal? MinutesPerStep, IReadOnlyList<FlowMinutesBody>? Flows);

public sealed record FlowMinutesBody(string FlowId, decimal? MinutesPerRun);

/// <summary>
/// Adds up a workspace's runs for Insights: runs per day (succeeded and failed stacked), the success rate against the
/// period before, how long runs take, the busiest flows and those that fail most, the steps that fail most, the
/// time saved and this month's runs against the limit. Pure: the repository reads the rows; tests drive this.
/// </summary>
public static class InsightsReport
{
    public static readonly int[] Ranges = [7, 30, 90];
    public const decimal DefaultMinutesPerStep = 2m;
    public const decimal MaxMinutesPerStep = 240m;
    public const decimal MaxMinutesPerRun = 1440m;

    /// <summary>The range asked for, as one of <see cref="Ranges"/> (30 otherwise).</summary>
    public static int RangeOf(int? days) => days is { } d && Ranges.Contains(d) ? d : 30;

    /// <summary>The first day counted: <paramref name="days"/> days ending today (UTC), at midnight.</summary>
    public static DateTime Since(DateTime nowUtc, int days) => nowUtc.Date.AddDays(-(days - 1));

    /// <summary>A minutes figure that can be kept: 0 to <paramref name="max"/>, to two places (null stays null).</summary>
    public static decimal? CleanMinutes(decimal? m, decimal max) => m is { } v ? Math.Round(Math.Clamp(v, 0m, max), 2) : null;

    public static InsightsDto Build(
        DateTime nowUtc, int days, string? flowId,
        IReadOnlyList<InsightDayRow> rows,
        IReadOnlyList<InsightDurationRow> durations,
        IReadOnlyList<InsightFlowRow> flows,
        IReadOnlyList<InsightStepRow> steps,
        IReadOnlyList<InsightErrorRow> errors,
        decimal? minutesPerStep, bool editable,
        int runsThisMonth, int quota, int keptDays, bool dailyKept)
    {
        var first = Since(nowUtc, days);
        var previousFirst = first.AddDays(-days);
        var perStep = minutesPerStep ?? DefaultMinutesPerStep;
        bool Mine(string id) => flowId is null || id == flowId;

        var current = rows.Where(r => r.Day.Date >= first && r.Day.Date <= nowUtc.Date).ToList();
        var previous = rows.Where(r => r.Day.Date >= previousFirst && r.Day.Date < first && Mine(r.FlowId)).ToList();

        // Runs per day, every day of the range (a quiet day is zero, not missing).
        var byDay = current.Where(r => Mine(r.FlowId)).GroupBy(r => r.Day.Date).ToDictionary(g => g.Key, g => g.ToList());
        var daily = Enumerable.Range(0, days).Select(i =>
        {
            var day = first.AddDays(i);
            var list = byDay.GetValueOrDefault(day) ?? [];
            return new InsightsDayDto(Day(day), list.Sum(r => r.Succeeded), list.Sum(r => r.Failed), list.Sum(r => r.Other));
        }).ToList();

        var overall = durations.FirstOrDefault(d => d.FlowId is null);
        var mineDuration = flowId is null ? overall : durations.FirstOrDefault(d => d.FlowId == flowId);
        var totals = Totals(current.Where(r => Mine(r.FlowId)), mineDuration);
        var before = Totals(previous, null);

        // Every flow in the workspace, and deleted flows that ran in the range.
        var known = flows.ToDictionary(f => f.Id, StringComparer.Ordinal);
        var ran = current.GroupBy(r => r.FlowId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);
        var ids = flows.Select(f => f.Id).Concat(ran.Keys.Where(k => !known.ContainsKey(k))).ToList();
        var flowList = ids.Select(id =>
        {
            var f = known.GetValueOrDefault(id);
            var list = ran.GetValueOrDefault(id) ?? [];
            int ok = list.Sum(r => r.Succeeded), bad = list.Sum(r => r.Failed), other = list.Sum(r => r.Other), stepsRun = list.Sum(r => r.Steps);
            var runs = ok + bad + other;
            var saved = f?.MinutesPerRun is { } perRun ? ok * (double)perRun : stepsRun * (double)perStep;
            var name = f?.Name ?? list.OrderByDescending(r => r.Day).Select(r => r.FlowName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n)) ?? "Deleted flow";
            return new InsightsFlowDto(id, name, f is not null, f?.Enabled ?? false, f?.TriggerApp, runs, ok, bad,
                ok + bad > 0 ? Math.Round((double)bad / (ok + bad), 4) : null, durations.FirstOrDefault(d => d.FlowId == id)?.MedianMs is { } m ? Math.Round(m) : null,
                f?.MinutesPerRun, Math.Round(saved, 1));
        }).ToList();

        var savedFlows = flowList.Where(f => Mine(f.Id)).ToList();
        var stepsRun = current.Where(r => Mine(r.FlowId)).Sum(r => r.Steps);
        var timeSaved = new InsightsTimeSavedDto(Math.Round(savedFlows.Sum(f => f.MinutesSaved), 1), stepsRun, perStep,
            minutesPerStep is not null || flows.Any(f => f.MinutesPerRun is not null), editable);

        var nameOf = flowList.ToDictionary(f => f.Id, f => f.Name, StringComparer.Ordinal);
        var failing = steps.Where(s => Mine(s.FlowId)).OrderByDescending(s => s.Failures).ThenByDescending(s => s.LastAt).Take(8).Select(s =>
            new InsightsStepDto(s.FlowId, nameOf.GetValueOrDefault(s.FlowId) ?? "Deleted flow", s.StepId, s.Name, s.Type, s.App, s.StepAction, s.Failures,
                Iso(s.LastAt), s.LastRunId,
                errors.Where(e => e.FlowId == s.FlowId && e.StepId == s.StepId).OrderByDescending(e => e.Count).Take(3)
                    .Select(e => new InsightsErrorDto(e.Error, e.Count)).DefaultIfEmpty(s.LastError is { Length: > 0 } le ? new InsightsErrorDto(le, s.Failures) : null)
                    .OfType<InsightsErrorDto>().ToList())).ToList();

        var month = UsageMonth(nowUtc);
        var resets = new DateTime(nowUtc.Year, nowUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        return new InsightsDto(days, Day(first), Day(nowUtc.Date), flowId, daily, totals, before, flowList, failing, timeSaved,
            new InsightsUsageDto(month, runsThisMonth, quota, Day(resets)), keptDays, dailyKept);
    }

    private static InsightsTotalsDto Totals(IEnumerable<InsightDayRow> rows, InsightDurationRow? d)
    {
        var list = rows.ToList();
        int ok = list.Sum(r => r.Succeeded), bad = list.Sum(r => r.Failed), other = list.Sum(r => r.Other);
        return new InsightsTotalsDto(ok + bad + other, ok, bad, other, ok + bad > 0 ? Math.Round((double)ok / (ok + bad), 4) : null,
            d is { Measured: > 0 } ? Math.Round(d.MedianMs) : null, d is { Measured: > 0 } ? Math.Round(d.P95Ms) : null);
    }

    public static string UsageMonth(DateTime utc) => utc.ToString("yyyy-MM", CultureInfo.InvariantCulture);
    private static string Day(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
}

/// <summary>
/// The monthly runs a workspace may use: Insights reads it here, and only here. It's the plan's quota (db/008), or the
/// workspace's own where it has one set by hand (<see cref="Billing.PlanRules.RunQuota"/>); before plans are in force,
/// the server's default (<c>Flow:MonthlyRuns</c>).
/// </summary>
public interface IRunLimits
{
    Task<int> MonthlyRunsAsync(WorkspaceRow workspace, CancellationToken ct = default);
}

public sealed class PlanRunLimits(Billing.Plans plans) : IRunLimits
{
    public async Task<int> MonthlyRunsAsync(WorkspaceRow workspace, CancellationToken ct = default) =>
        Billing.PlanRules.RunQuota(workspace, (await plans.ForAsync(workspace, DateTime.UtcNow)).Plan);
}
