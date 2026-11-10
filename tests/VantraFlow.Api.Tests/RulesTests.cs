using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

public sealed class DefinitionRulesTests
{
    private static readonly FlowLimits Limits = new();

    [Fact]
    public void A_complete_flow_has_no_errors() =>
        Assert.Empty(DefinitionRules.Validate(Build.Flow(Build.Email("s1"), Build.Delay("d1", 1), Build.Http("s2")), Limits));

    [Fact]
    public void Says_what_is_missing()
    {
        var def = new FlowDefinition { Trigger = new TriggerDef { App = "orbit", Event = "record.created" } };
        var errors = DefinitionRules.Validate(def, Limits).Select(e => e.Message).ToList();
        Assert.Contains("The trigger needs a table.", errors);
        Assert.Contains("Add a step after the trigger.", errors);
    }

    [Fact]
    public void A_token_must_come_from_an_earlier_step()
    {
        var first = Build.Http("s1");
        first.Config["body"] = "{{steps.s2.id}}";
        var errors = DefinitionRules.Validate(Build.Flow(first, Build.Http("s2")), Limits);
        Assert.Contains(errors, e => e.StepId == "s1" && e.Message.Contains("isn't from an earlier step"));
    }

    [Fact]
    public void After_a_branch_either_sides_outputs_may_be_used_but_not_across_sides()
    {
        var crossing = Build.Http("e1");
        crossing.Config["body"] = "{{steps.t1.id}}";
        var after = Build.Http("z");
        after.Config["body"] = "{{steps.t1.id}} {{steps.e1.id}}";
        var def = Build.Flow(Build.Branch("b", "{{trigger.record.fields.Stage}}", "equals", "Won", [Build.Http("t1")], [crossing]), after);
        var errors = DefinitionRules.Validate(def, Limits);
        Assert.Contains(errors, e => e.StepId == "e1");
        Assert.DoesNotContain(errors, e => e.StepId == "z");
    }

    [Fact]
    public void Holds_flows_to_the_step_limit_ids_and_delay_bounds()
    {
        var many = Enumerable.Range(0, 26).Select(i => Build.Http("s" + i)).ToArray();
        Assert.Contains(DefinitionRules.Validate(Build.Flow(many), Limits), e => e.Message.Contains("at most 25 steps"));
        Assert.Contains(DefinitionRules.Validate(Build.Flow(Build.Http("Bad-Id")), Limits), e => e.Message.Contains("id"));
        Assert.Contains(DefinitionRules.Validate(Build.Flow(Build.Delay("d", 31, "days")), Limits), e => e.Message.Contains("30 days"));
        Assert.Contains(DefinitionRules.Validate(Build.Flow(Build.Http("a"), Build.Http("a")), Limits), e => e.Message.Contains("share"));
    }

    [Fact]
    public void Branches_nest_two_deep_at_most()
    {
        var deep = Build.Branch("b1", "x", "is_empty", null,
            [Build.Branch("b2", "x", "is_empty", null, [Build.Branch("b3", "x", "is_empty", null, [Build.Http("s")], [])], [])], []);
        Assert.Contains(DefinitionRules.Validate(Build.Flow(deep), Limits), e => e.StepId == "b3");
    }
}

public sealed class TokensTests
{
    private static readonly JsonObject Data = RunState.NewData(Build.Trigger(), "frn_1", DateTime.UtcNow, "Acme", "UTC", false);

    [Fact]
    public void Fills_in_paths_with_spaces_and_any_case()
    {
        Assert.Equal("Ada wins 1200", Tokens.Render("{{trigger.record.fields.name}} wins {{ trigger.record.fields.Deal value }}", Data));
        Assert.Equal("Acme", Tokens.Render("{{workspace.name}}", Data));
        Assert.Equal("", Tokens.Render("{{trigger.record.fields.Missing}}", Data));
    }

    [Fact]
    public void A_whole_token_keeps_the_values_type()
    {
        var rendered = (JsonObject)Tokens.RenderNode(new JsonObject { ["value"] = "{{trigger.record.fields.Deal value}}", ["text"] = "x {{trigger.record.fields.Deal value}}" }, Data)!;
        Assert.Equal(1200, rendered["value"]!.GetValue<int>());
        Assert.Equal("x 1200", rendered["text"]!.GetValue<string>());
    }

    [Fact]
    public void Escapes_for_html_when_asked()
    {
        var data = new JsonObject { ["trigger"] = new JsonObject { ["x"] = "<b>hi</b>" } };
        Assert.Equal("&lt;b&gt;hi&lt;/b&gt;", Tokens.Render("{{trigger.x}}", data, html: true));
    }

    [Fact]
    public void Lists_of_people_read_as_names() =>
        Assert.Equal("Ada, Grace", Tokens.Text(new JsonArray(new JsonObject { ["id"] = "1", ["name"] = "Ada" }, new JsonObject { ["id"] = "2", ["name"] = "Grace" })));

    [Fact]
    public void References_lists_every_token() =>
        Assert.Equal(["a.b", "steps.s1.id"], Tokens.References(new JsonObject { ["x"] = "{{a.b}}", ["y"] = new JsonArray("{{ steps.s1.id }}") }));
}

public sealed class ConditionsTests
{
    [Theory]
    [InlineData("Won", "equals", "won", true)]
    [InlineData("1200", "greater_than", "1000", true)]
    [InlineData("900", "greater_than", "1000", false)]
    [InlineData("2026-11-10", "less_than", "2026-12-01", true)]
    [InlineData("hello@example.com", "contains", "@example", true)]
    [InlineData("", "is_empty", null, true)]
    [InlineData("x", "is_not_empty", null, true)]
    [InlineData("true", "is_true", null, true)]
    [InlineData("Won", "not_equals", "Lost", true)]
    public void Tests_each_operator(string left, string op, string? right, bool expected) =>
        Assert.Equal(expected, Conditions.Test(left, op, right));

    [Fact]
    public void Any_and_all()
    {
        var data = new JsonObject { ["trigger"] = Build.Trigger() };
        JsonObject Cond(string match) => new()
        {
            ["match"] = match,
            ["rules"] = new JsonArray(
                new JsonObject { ["left"] = "{{trigger.record.fields.Stage}}", ["op"] = "equals", ["right"] = "Won" },
                new JsonObject { ["left"] = "{{trigger.record.fields.Name}}", ["op"] = "equals", ["right"] = "Grace" }),
        };
        Assert.False(Conditions.Evaluate(Conditions.Parse(Cond("all"))!, data));
        Assert.True(Conditions.Evaluate(Conditions.Parse(Cond("any"))!, data));
    }
}

public sealed class ScheduleRulesTests
{
    private static TriggerDef T(string ev, JsonObject config) => new() { App = "schedule", Event = ev, Config = config };

    [Fact]
    public void Daily_fires_at_the_local_time_in_the_workspaces_zone()
    {
        var t = T("daily", new JsonObject { ["time"] = "09:00" });
        // 09:00 in London during summer time is 08:00 UTC.
        var summer = new DateTime(2026, 7, 1, 8, 0, 30, DateTimeKind.Utc);
        Assert.Equal([new DateTime(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc)], ScheduleRules.DueSlots(t, "Europe/London", summer, TimeSpan.FromMinutes(3)));
        Assert.Empty(ScheduleRules.DueSlots(t, "UTC", summer, TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Weekly_fires_only_on_its_days()
    {
        var t = T("weekly", new JsonObject { ["time"] = "17:30", ["days"] = new JsonArray("fri") });
        Assert.Single(ScheduleRules.DueSlots(t, "UTC", new DateTime(2026, 11, 13, 17, 31, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(3))); // a Friday
        Assert.Empty(ScheduleRules.DueSlots(t, "UTC", new DateTime(2026, 11, 12, 17, 31, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(3)));
    }

    [Fact]
    public void Intervals_land_on_round_minutes_and_hours()
    {
        var every15 = T("interval", new JsonObject { ["every"] = 15, ["unit"] = "minutes" });
        Assert.Equal(new DateTime(2026, 11, 10, 9, 15, 0, DateTimeKind.Utc), ScheduleRules.Next(every15, "UTC", new DateTime(2026, 11, 10, 9, 2, 0, DateTimeKind.Utc)));
        var every2h = T("interval", new JsonObject { ["every"] = 2, ["unit"] = "hours" });
        Assert.Equal(new DateTime(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc), ScheduleRules.Next(every2h, "UTC", new DateTime(2026, 11, 10, 9, 2, 0, DateTimeKind.Utc)));
        Assert.Contains(ScheduleRules.Validate(T("interval", new JsonObject { ["every"] = 1, ["unit"] = "minutes" })), e => e.Message.Contains("5 to 720"));
    }
}
