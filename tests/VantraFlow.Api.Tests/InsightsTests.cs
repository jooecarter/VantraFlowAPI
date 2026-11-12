using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Insights;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Insights: runs per day, rates, durations, the flows and steps that stand out, time saved and usage (db/006).</summary>
public sealed class InsightsTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 15, 30, 0, DateTimeKind.Utc);

    private static InsightDayRow Day(int daysAgo, string flow, int ok, int bad, int other = 0, int steps = 0, string? name = null) =>
        new(Now.Date.AddDays(-daysAgo), flow, name ?? flow, ok, bad, other, steps);

    private static InsightsDto Build(IReadOnlyList<InsightDayRow> rows, int days = 7, string? flowId = null, decimal? perStep = null,
        IReadOnlyList<InsightFlowRow>? flows = null, IReadOnlyList<InsightStepRow>? steps = null, IReadOnlyList<InsightErrorRow>? errors = null,
        IReadOnlyList<InsightDurationRow>? durations = null) =>
        InsightsReport.Build(Now, days, flowId, rows, durations ?? [], flows ?? [new("a", "Alpha", true, "orbit", null), new("b", "Beta", false, "schedule", null)],
            steps ?? [], errors ?? [], perStep, editable: true, runsThisMonth: 812, quota: 5000, keptDays: 30, dailyKept: true);

    [Fact]
    public void Every_day_of_the_range_is_there_oldest_first_and_quiet_days_are_zero()
    {
        var r = Build([Day(0, "a", 3, 1), Day(6, "a", 2, 0), Day(7, "a", 9, 9), Day(2, "b", 4, 0, 1)]);
        Assert.Equal(7, r.Daily.Count);
        Assert.Equal("2026-11-06", r.Daily[0].Day);
        Assert.Equal("2026-11-12", r.Daily[^1].Day);
        Assert.Equal([2, 0, 0, 0, 4, 0, 3], r.Daily.Select(d => d.Succeeded));
        Assert.Equal([0, 0, 0, 0, 0, 0, 1], r.Daily.Select(d => d.Failed));
        Assert.Equal(1, r.Daily[4].Other);
        Assert.Equal("2026-11-06", r.From);
        Assert.Equal("2026-11-12", r.To);
    }

    [Fact]
    public void Success_rate_counts_finished_runs_and_compares_with_the_period_before()
    {
        var r = Build([Day(0, "a", 8, 2), Day(1, "b", 0, 0, 5), Day(8, "a", 1, 1), Day(14, "a", 50, 0)]);
        Assert.Equal(15, r.Totals.Runs);
        Assert.Equal(0.8, r.Totals.SuccessRate);
        // The 7 days before: day 8 only (day 14 is outside both).
        Assert.Equal(2, r.Previous.Runs);
        Assert.Equal(0.5, r.Previous.SuccessRate);
        Assert.Null(Build([]).Totals.SuccessRate);
    }

    [Fact]
    public void Durations_are_the_workspaces_or_the_chosen_flows()
    {
        IReadOnlyList<InsightDurationRow> d = [new(null, 1200.4, 9100.6, 40), new("a", 800, 2000, 30), new("b", 4000, 12000, 10)];
        var all = Build([Day(0, "a", 1, 0)], durations: d);
        Assert.Equal(1200, all.Totals.MedianMs);
        Assert.Equal(9101, all.Totals.P95Ms);
        var one = Build([Day(0, "a", 1, 0)], flowId: "b", durations: d);
        Assert.Equal(4000, one.Totals.MedianMs);
        Assert.Equal(800, all.Flows.Single(f => f.Id == "a").MedianMs);
        Assert.Null(Build([]).Totals.MedianMs);
    }

    [Fact]
    public void Flows_include_quiet_ones_and_deleted_ones_that_ran()
    {
        var r = Build([Day(0, "a", 6, 2), Day(1, "gone", 1, 3, name: "Old flow")]);
        Assert.Equal(["a", "b", "gone"], r.Flows.Select(f => f.Id));
        var a = r.Flows[0];
        Assert.Equal((8, 6, 2, 0.25), (a.Runs, a.Succeeded, a.Failed, a.FailureRate!.Value));
        Assert.True(a.Exists);
        Assert.Equal(0, r.Flows[1].Runs);
        Assert.Null(r.Flows[1].FailureRate);
        var gone = r.Flows[2];
        Assert.False(gone.Exists);
        Assert.Equal("Old flow", gone.Name);
    }

    [Fact]
    public void A_flow_filter_narrows_the_totals_but_keeps_every_flow_listed()
    {
        var r = Build([Day(0, "a", 6, 2), Day(0, "b", 10, 0)], flowId: "b");
        Assert.Equal(10, r.Totals.Runs);
        Assert.Equal([0, 0, 0, 0, 0, 0, 10], r.Daily.Select(d => d.Succeeded));
        Assert.Equal(2, r.Flows.Count);
        Assert.Equal("b", r.FlowId);
    }

    [Fact]
    public void Time_saved_is_steps_times_minutes_unless_a_flow_has_its_own_per_run()
    {
        var rows = new[] { Day(0, "a", 4, 1, steps: 12), Day(1, "b", 3, 0, steps: 9) };
        var byDefault = Build(rows);
        Assert.Equal(21 * 2, byDefault.TimeSaved.Minutes);
        Assert.Equal(21, byDefault.TimeSaved.Steps);
        Assert.Equal(2m, byDefault.TimeSaved.MinutesPerStep);
        Assert.False(byDefault.TimeSaved.Custom);

        var custom = Build(rows, perStep: 0.5m, flows: [new("a", "Alpha", true, "orbit", 15m), new("b", "Beta", true, "orbit", null)]);
        // a: 4 succeeded runs × 15 minutes; b: 9 steps × 0.5 minutes.
        Assert.Equal(60 + 4.5, custom.TimeSaved.Minutes);
        Assert.True(custom.TimeSaved.Custom);
        Assert.Equal(60, custom.Flows.Single(f => f.Id == "a").MinutesSaved);
    }

    [Fact]
    public void Failing_steps_come_most_failed_first_with_their_commonest_errors()
    {
        var steps = new InsightStepRow[]
        {
            new("a", "s1", "Create project", "action", "projects", "project.create", 3, Now.AddHours(-2), "frn_1", "Vantra Projects didn't answer."),
            new("a", "s2", "Tell delivery", "action", "email", "send", 7, Now.AddHours(-5), "frn_2", "The address isn't valid."),
            new("b", "s1", "Post", "action", "slack", "message.send", 1, Now.AddHours(-1), "frn_3", "channel_not_found"),
        };
        var errors = new InsightErrorRow[]
        {
            new("a", "s2", "The address isn't valid.", 5), new("a", "s2", "The daily email limit was reached.", 2), new("a", "s1", "x", 1),
        };
        var r = Build([], steps: steps, errors: errors);
        Assert.Equal(["s2", "s1", "s1"], r.FailingSteps.Select(s => s.StepId));
        Assert.Equal("Alpha", r.FailingSteps[0].FlowName);
        Assert.Equal(["The address isn't valid.", "The daily email limit was reached."], r.FailingSteps[0].Errors.Select(e => e.Message));
        // No grouped errors: the latest one stands in.
        Assert.Equal("channel_not_found", r.FailingSteps[2].Errors.Single().Message);
        Assert.Single(Build([], flowId: "b", steps: steps).FailingSteps);
    }

    [Fact]
    public void Usage_is_this_months_runs_against_the_limit()
    {
        var r = Build([]);
        Assert.Equal(new InsightsUsageDto("2026-11", 812, 5000, "2026-12-01"), r.Usage);
    }

    [Theory]
    [InlineData(null, 30)]
    [InlineData(7, 7)]
    [InlineData(90, 90)]
    [InlineData(45, 30)]
    public void Only_7_30_and_90_days(int? asked, int range) => Assert.Equal(range, InsightsReport.RangeOf(asked));

    [Fact]
    public void Minutes_are_held_to_a_range() =>
        Assert.Equal((0m, 240m, 1.25m, (decimal?)null),
            (InsightsReport.CleanMinutes(-3, 240)!.Value, InsightsReport.CleanMinutes(999, 240)!.Value, InsightsReport.CleanMinutes(1.2549m, 240)!.Value,
                InsightsReport.CleanMinutes(null, 240)));

    // ── db/006 ──

    private static string Sql => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "006_ai_insights.sql"));

    [Fact]
    public void The_migration_parses_is_azure_ready_and_makes_the_probes_index_last()
    {
        var sql = Sql;
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(ALTER|CREATE)\s+(TABLE|INDEX|UNIQUE)\b", RegexOptions.IgnoreCase), outside);
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_RunSteps_Failed", last);
        Assert.Contains("IX_RunSteps_Failed", FlowSchema.InsightsProbeSql);
        Assert.DoesNotContain("definition", FlowSchema.InsightsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_queries_parse()
    {
        foreach (var q in new[]
                 {
                     InsightsRepository.DaysSql, InsightsRepository.DaysKeptSql, InsightsRepository.DurationsSql, InsightsRepository.FailingStepsSql,
                     EngineRepository.PruneRunsSql, EngineRepository.PruneRunsKeepingDaysSql, AiRepository.CountSql,
                 })
        {
            new TSql170Parser(true).Parse(new StringReader(q), out var errors);
            Assert.Empty(errors);
        }
        // Only the prune that keeps daily totals touches them, and it does so in the transaction that deletes.
        Assert.DoesNotContain("RunDays", EngineRepository.PruneRunsSql);
        Assert.Contains("OUTPUT DELETED.", EngineRepository.PruneRunsKeepingDaysSql);
        Assert.Contains("BEGIN TRANSACTION", EngineRepository.PruneRunsKeepingDaysSql);
        Assert.Contains("Test = 0", EngineRepository.PruneRunsKeepingDaysSql);
    }
}
