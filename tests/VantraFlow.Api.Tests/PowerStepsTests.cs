using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

internal static class Power
{
    public static StepDef Formatter(string id, JsonObject config) => new() { Id = id, Type = StepTypes.Formatter, Config = config };

    public static StepDef Filter(string id, string left, string op, string? right) => new()
    {
        Id = id, Type = StepTypes.Filter,
        Config = new JsonObject { ["condition"] = new JsonObject { ["match"] = "all", ["rules"] = new JsonArray(Rule(left, op, right)) } },
    };

    public static JsonObject Rule(string left, string op, string? right) => new() { ["left"] = left, ["op"] = op, ["right"] = right };

    public static PathDef Path(string name, string left, string op, string? right, params StepDef[] steps) => new()
    {
        Name = name, Condition = new JsonObject { ["match"] = "all", ["rules"] = new JsonArray(Rule(left, op, right)) }, Steps = steps.ToList(),
    };

    public static StepDef Paths(string id, bool otherwise, List<StepDef>? otherwiseSteps, params PathDef[] paths) => new()
    {
        Id = id, Type = StepTypes.Paths, Paths = paths.ToList(), Else = otherwiseSteps ?? [], Config = new JsonObject { ["otherwise"] = otherwise },
    };

    public static StepDef Set(string id, params (string Name, string Op, JsonNode? Value)[] a) => new()
    {
        Id = id, Type = StepTypes.Variable,
        Config = new JsonObject
        {
            ["assignments"] = new JsonArray(a.Select(x => (JsonNode)new JsonObject { ["name"] = x.Name, ["op"] = x.Op, ["value"] = x.Value?.DeepClone() }).ToArray()),
        },
    };

    public static StepDef Loop(string id, string items, string onError, params StepDef[] steps) => new()
    {
        Id = id, Type = StepTypes.Loop, Config = new JsonObject { ["items"] = items, ["onError"] = onError }, Do = steps.ToList(),
    };

    public static JsonObject Fmt(string kind, string op, JsonNode? input, params (string, JsonNode?)[] more)
    {
        var o = new JsonObject { ["kind"] = kind, ["op"] = op, ["input"] = input };
        foreach (var (k, v) in more) o[k] = v;
        return o;
    }

    public static JsonNode? Run(JsonObject config, string? tz = "UTC")
    {
        var (output, error) = VantraFlow.Api.Services.Engine.Formatter.Run(config, tz);
        Assert.True(error is null, error);
        return output!["output"];
    }

    public static string Error(JsonObject config)
    {
        var (output, error) = VantraFlow.Api.Services.Engine.Formatter.Run(config, "UTC");
        Assert.Null(output);
        return error!;
    }
}

public sealed class FormatterTests
{
    [Theory]
    [InlineData("trim", "  Ada   Lovelace ", null, "Ada Lovelace")]
    [InlineData("case", "ada lovelace", "title", "Ada Lovelace")]
    [InlineData("case", "Ada", "upper", "ADA")]
    [InlineData("case", "hELLO there", "sentence", "Hello there")]
    public void Text_trims_and_changes_case(string op, string input, string? mode, string expected)
    {
        Assert.Equal(expected, Power.Run(Power.Fmt("text", op, input, ("case", mode)))!.GetValue<string>());
    }

    [Fact]
    public void Text_replaces_every_match_and_splits_into_a_list()
    {
        Assert.Equal("a-b-c", Power.Run(Power.Fmt("text", "replace", "a b c", ("find", " "), ("with", "-")))!.GetValue<string>());
        var (out1, _) = Formatter.Run(Power.Fmt("text", "split", "red, green ,blue,", ("separator", ",")), "UTC");
        Assert.Equal(["red", "green", "blue"], out1!["output"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.Equal("red", out1["first"]!.GetValue<string>());
        Assert.Equal("blue", out1["last"]!.GetValue<string>());
        Assert.Equal(3, out1["count"]!.GetValue<int>());
    }

    [Fact]
    public void Text_extracts_with_presets_and_patterns()
    {
        Assert.Equal("ada@example.com", Power.Run(Power.Fmt("text", "extract", "Write to Ada <ada@example.com> today", ("what", "email")))!.GetValue<string>());
        Assert.Equal("1,250.50", Power.Run(Power.Fmt("text", "extract", "Total: 1,250.50 GBP", ("what", "number")))!.GetValue<string>());
        // The first group, when the pattern has one.
        Assert.Equal("4471", Power.Run(Power.Fmt("text", "extract", "Order #4471 shipped", ("what", "custom"), ("pattern", @"#(\d+)")))!.GetValue<string>());
        var (none, _) = Formatter.Run(Power.Fmt("text", "extract", "nothing here", ("what", "email")), "UTC");
        Assert.Equal("", none!["output"]!.GetValue<string>());
        Assert.False(none["found"]!.GetValue<bool>());
    }

    [Fact]
    public void A_runaway_pattern_times_out_instead_of_hanging()
    {
        var evil = Power.Fmt("text", "extract", new string('a', 5000) + "!", ("what", "custom"), ("pattern", "^(a+)+$"));
        var started = DateTime.UtcNow;
        Assert.Equal("The pattern took too long on this text.", Power.Error(evil));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5));
        Assert.Equal("The pattern isn't a regular expression Vantra Flow can run.", Formatter.Check(Power.Fmt("text", "extract", "x", ("what", "custom"), ("pattern", "(unclosed"))));
    }

    [Theory]
    [InlineData(1234.5, "comma", 2, "1,234.50")]
    [InlineData(1234.5, "period", 2, "1.234,50")]
    [InlineData(1234567.891, "space", 1, "1 234 567,9")]
    [InlineData(-1234.5, "none", 0, "-1235")]
    [InlineData(999.995, "comma", 2, "1,000.00")]
    public void Numbers_format_with_separators(double n, string sep, int decimals, string expected)
    {
        Assert.Equal(expected, Formatter.FormatNumber(n, decimals, sep));
    }

    [Fact]
    public void Numbers_format_from_text_round_and_do_maths()
    {
        Assert.Equal("£1,250.00", Power.Run(Power.Fmt("number", "format", "1250", ("decimals", 2), ("prefix", "£")))!.GetValue<string>());
        Assert.Equal(3.14, Power.Run(Power.Fmt("number", "round", 3.14159, ("decimals", 2)))!.GetValue<double>());
        Assert.Equal(4, Power.Run(Power.Fmt("number", "round", "3.01", ("mode", "up")))!.GetValue<long>());
        Assert.Equal(3, Power.Run(Power.Fmt("number", "round", "3.99", ("mode", "down")))!.GetValue<long>());
        Assert.Equal(1500, Power.Run(Power.Fmt("number", "math", "$1,200", ("operator", "add"), ("value", "300")))!.GetValue<long>());
        Assert.Equal(0.25, Power.Run(Power.Fmt("number", "math", 1, ("operator", "divide"), ("value", 4)))!.GetValue<double>());
        Assert.Equal("Can't divide by zero.", Power.Error(Power.Fmt("number", "math", 1, ("operator", "divide"), ("value", 0))));
        Assert.Equal("“soon” isn't a number.", Power.Error(Power.Fmt("number", "round", "soon")));
    }

    [Fact]
    public void Dates_parse_format_and_move()
    {
        // Day first, read in London (BST in June), out in UTC.
        Assert.Equal("2026-06-11T08:30:00Z", Power.Run(Power.Fmt("date", "parse", "11/06/2026 09:30", ("from", "dmy")), "Europe/London")!.GetValue<string>());
        Assert.Equal("2026-11-06T00:00:00Z", Power.Run(Power.Fmt("date", "parse", "11/06/2026", ("from", "mdy")))!.GetValue<string>());
        Assert.Equal("2026-11-11T09:00:00Z", Power.Run(Power.Fmt("date", "parse", "1794387600"))!.GetValue<string>());
        Assert.Equal("Wednesday 11 November 2026, 9:05 AM",
            Power.Run(Power.Fmt("date", "format", "2026-11-11T09:05:00Z", ("pattern", "dddd d MMMM yyyy, h:mm tt")))!.GetValue<string>());
        Assert.Equal("at 10:05", Power.Run(Power.Fmt("date", "format", "2026-06-11T09:05:00Z", ("pattern", "'at' HH:mm")), "Europe/London")!.GetValue<string>());
        Assert.Equal("2026-12-11T09:00:00Z", Power.Run(Power.Fmt("date", "add", "2026-11-11T09:00:00Z", ("amount", 1), ("unit", "months")))!.GetValue<string>());
        Assert.Equal("2026-11-10T07:00:00Z", Power.Run(Power.Fmt("date", "add", "2026-11-11T09:00:00Z", ("operator", "subtract"), ("amount", "26"), ("unit", "hours")))!.GetValue<string>());
        // Across the clock change, a day later is still 09:00 local.
        Assert.Equal("2026-10-25T09:00:00Z", Power.Run(Power.Fmt("date", "add", "2026-10-24T08:00:00Z", ("amount", 1), ("unit", "days")), "Europe/London")!.GetValue<string>());
        Assert.Equal("2026-11-11T04:00:00-05:00", Power.Run(Power.Fmt("date", "timezone", "2026-11-11T09:00:00Z", ("zone", "America/New_York")))!.GetValue<string>());
        Assert.Equal("“someday” isn't a date Vantra Flow can read.", Power.Error(Power.Fmt("date", "format", "someday", ("pattern", "yyyy"))));
        Assert.Equal("“Mars/Base” isn't a time zone.", Formatter.Check(Power.Fmt("date", "timezone", "x", ("zone", "Mars/Base"))));
    }

    [Fact]
    public void A_lookup_table_matches_without_case_and_falls_back()
    {
        var rows = new JsonArray(new JsonObject { ["key"] = "UK", ["value"] = "Europe/London" }, new JsonObject { ["key"] = "us", ["value"] = "America/New_York" });
        Assert.Equal("America/New_York", Power.Run(Power.Fmt("lookup", "lookup", "US", ("rows", rows.DeepClone()), ("fallback", "UTC")))!.GetValue<string>());
        Assert.Equal("UTC", Power.Run(Power.Fmt("lookup", "lookup", "FR", ("rows", rows.DeepClone()), ("fallback", "UTC")))!.GetValue<string>());
    }

    [Fact]
    public void Check_names_what_is_missing()
    {
        Assert.Equal("Choose what to format.", Formatter.Check(new JsonObject()));
        Assert.Equal("Choose the value to format.", Formatter.Check(Power.Fmt("text", "trim", "")));
        Assert.Equal("Fill in what to replace.", Formatter.Check(Power.Fmt("text", "replace", "x")));
        Assert.Equal("Fill in the number to use.", Formatter.Check(Power.Fmt("number", "math", "1")));
        Assert.Equal("Add a row to the table.", Formatter.Check(Power.Fmt("lookup", "lookup", "x")));
        Assert.Null(Formatter.Check(Power.Fmt("date", "add", "{{trigger.when}}", ("amount", "{{trigger.days}}"), ("zone", "{{trigger.zone}}"))));
    }
}

public sealed class PowerStepsEngineTests
{
    private static readonly FlowLimits Limits = new();

    [Fact]
    public async Task A_formatter_hands_its_output_to_later_steps()
    {
        var fmt = Power.Formatter("f1", Power.Fmt("text", "case", "{{trigger.record.fields.Name}}", ("case", "upper")));
        var send = Build.Http("s2");
        send.Config["body"] = "{{steps.f1.output}}";
        var actions = new FakeActions();
        var run = Build.Run(Build.Flow(fmt, send));

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("ADA", actions.Calls.Single().Input["body"]!.GetValue<string>());
        Assert.Equal(["f1", "s2"], logs.Select(l => l.StepId));
        Assert.Equal(1, run.StepCount); // a formatter isn't an app call
    }

    [Fact]
    public async Task A_formatter_that_cant_work_fails_the_run_with_why()
    {
        var run = Build.Run(Build.Flow(Power.Formatter("f1", Power.Fmt("number", "round", "{{trigger.record.fields.Name}}")), Build.Http("s2")));
        var actions = new FakeActions();

        await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("Format: “Ada” isn't a number.", run.Error);
        Assert.Empty(actions.Calls);
    }

    [Theory]
    [InlineData("Won", true)]
    [InlineData("Lost", false)]
    public async Task A_filter_stops_the_run_quietly_when_it_does_not_pass(string stage, bool passes)
    {
        var def = Build.Flow(Power.Filter("f1", "{{trigger.record.fields.Stage}}", "equals", "won"), Build.Http("s2"));
        var actions = new FakeActions();
        var run = Build.Run(def, Build.Trigger(stage));

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Null(run.Error);
        Assert.Equal(passes, actions.Calls.Count == 1);
        Assert.Equal(passes, run.Steps["f1"]!["passed"]!.GetValue<bool>());
        Assert.Equal(passes ? null : "It didn't pass, so the run stopped here.", logs[0].Error);
    }

    [Fact]
    public async Task Filter_groups_join_with_and_or()
    {
        // (Stage is Won and value > 1000) or (Stage is Lost): Won with 1200 passes.
        var filter = new StepDef
        {
            Id = "f1", Type = StepTypes.Filter,
            Config = new JsonObject
            {
                ["condition"] = new JsonObject
                {
                    ["match"] = "any",
                    ["groups"] = new JsonArray(
                        new JsonObject { ["match"] = "all", ["rules"] = new JsonArray(Power.Rule("{{trigger.record.fields.Stage}}", "equals", "Won"), Power.Rule("{{trigger.record.fields.Deal value}}", "greater_than", "1000")) },
                        new JsonObject { ["match"] = "all", ["rules"] = new JsonArray(Power.Rule("{{trigger.record.fields.Stage}}", "equals", "Lost")) }),
                },
            },
        };
        foreach (var (stage, expected) in new[] { ("Won", true), ("Lost", true), ("New", false) })
        {
            var run = Build.Run(Build.Flow(filter, Build.Http("s2")), Build.Trigger(stage));
            await new RunExecutor(new FakeActions(), Limits).ExecuteAsync(run, CancellationToken.None);
            Assert.Equal(expected, run.Steps["f1"]!["passed"]!.GetValue<bool>());
        }
        var c = Conditions.Parse(filter.Config["condition"])!;
        Assert.Equal(3, c.Count);
    }

    [Fact]
    public async Task Inside_a_loop_a_filter_skips_just_that_item()
    {
        var trigger = Build.Trigger();
        trigger["items"] = new JsonArray(new JsonObject { ["n"] = 1 }, new JsonObject { ["n"] = 5 }, new JsonObject { ["n"] = 9 });
        var def = Build.Flow(Power.Loop("l1", "{{trigger.items}}", "stop", Power.Filter("f1", "{{loop.item.n}}", "greater_than", "3"), Build.Http("s2")));
        var actions = new FakeActions();
        var run = Build.Run(def, trigger);

        await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None, maxSteps: 100);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(2, actions.Calls.Count);
        var output = run.Steps["l1"]!;
        Assert.Equal(1, output["skipped"]!.GetValue<int>());
        Assert.Equal(2, output["succeeded"]!.GetValue<int>());
        Assert.Equal("skipped", output["results"]![0]!["status"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Won", "a", "Big win")]
    [InlineData("Lost", "b", "Lost it")]
    [InlineData("New", "c", "Otherwise")]
    public async Task Paths_take_the_first_that_holds_else_otherwise(string stage, string expected, string pathName)
    {
        var def = Build.Flow(
            Power.Paths("p1", true, [Build.Http("c")],
                Power.Path("Big win", "{{trigger.record.fields.Stage}}", "equals", "Won", Build.Http("a")),
                Power.Path("Lost it", "{{trigger.record.fields.Stage}}", "equals", "Lost", Build.Http("b")),
                Power.Path("Also won", "{{trigger.record.fields.Stage}}", "equals", "Won", Build.Http("never"))),
            Build.Http("after"));
        var actions = new FakeActions();
        var run = Build.Run(def, Build.Trigger(stage));

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["p1", expected, "after"], logs.Select(l => l.StepId));
        Assert.Equal(pathName, run.Steps["p1"]!["path"]!.GetValue<string>());
    }

    [Fact]
    public async Task With_no_match_and_no_otherwise_paths_are_passed_by()
    {
        var def = Build.Flow(Power.Paths("p1", false, [Build.Http("c")], Power.Path("A", "{{trigger.record.fields.Stage}}", "equals", "Lost", Build.Http("a"))), Build.Http("after"));
        var run = Build.Run(def);
        var logs = await new RunExecutor(new FakeActions(), Limits).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(["p1", "after"], logs.Select(l => l.StepId));
        Assert.Equal("No path matched, so the run went on past them.", logs[0].Error);
    }

    [Fact]
    public async Task Variables_set_add_and_append_across_loop_items()
    {
        var trigger = Build.Trigger();
        trigger["items"] = new JsonArray(new JsonObject { ["name"] = "a", ["n"] = 2 }, new JsonObject { ["name"] = "b", ["n"] = 3.5 });
        var def = Build.Flow(
            Power.Set("v0", ("total", "set", 0), ("label", "set", "Order {{trigger.record.id}}")),
            Power.Loop("l1", "{{trigger.items}}", "stop", Power.Set("v1", ("total", "add", "{{loop.item.n}}"), ("names", "append", "{{loop.item.name}}"))),
            Build.Http("s2"));
        def.Steps[2].Config["body"] = "{{vars.label}}: {{vars.total}} from {{vars.names}}";
        var actions = new FakeActions();
        var run = Build.Run(def, trigger);

        await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None, maxSteps: 100);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("Order rec_1: 5.5 from a, b", actions.Calls.Single().Input["body"]!.GetValue<string>());
        Assert.DoesNotContain(DefinitionRules.Validate(def, Limits), e => e.Message.Contains("vars"));
    }

    [Fact]
    public async Task Adding_text_to_a_number_variable_fails_the_step()
    {
        var run = Build.Run(Build.Flow(Power.Set("v1", ("total", "add", "{{trigger.record.fields.Name}}"))));
        await new RunExecutor(new FakeActions(), Limits).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("Set variable: total: “Ada” isn't a number to add.", run.Error);
    }

    [Fact]
    public async Task A_called_runs_depth_reaches_the_run_another_flow_step()
    {
        var call = new StepDef { Id = "s1", Type = StepTypes.Action, App = "flow", Action = "run", Config = new JsonObject { ["flowId"] = "flw_child" } };
        var def = new FlowDefinition { Trigger = new TriggerDef { App = "flow", Event = "called" }, Steps = [call] };
        var trigger = new JsonObject { ["input"] = new JsonObject(), ["caller"] = new JsonObject { ["depth"] = 2 } };
        var actions = new FakeActions();
        var run = Build.Run(def, trigger);

        await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(2, actions.Calls.Single().Depth);
        Assert.Equal(0, SubFlowRules.DepthOf(Build.Run(Build.Flow()).Data));
        Assert.Empty(DefinitionRules.Validate(def, Limits));
    }
}

public sealed class PowerStepsRulesTests
{
    private static readonly FlowLimits Limits = new();

    [Fact]
    public void Positions_walk_into_and_out_of_paths()
    {
        var def = Build.Flow(
            Power.Paths("p", true, [Build.Http("o1")], Power.Path("A", "x", "is_empty", null, Build.Http("a1"), Build.Http("a2")), Power.Path("B", "x", "is_empty", null)),
            Build.Http("after"));
        Assert.Equal("0.p0.0", Positions.EnterPath(def, "0", 0));
        Assert.Equal("1", Positions.EnterPath(def, "0", 1)); // an empty path goes straight past
        Assert.Equal("0.e.0", Positions.EnterPath(def, "0", -1));
        Assert.Equal("1", Positions.EnterPath(def, "0", null));
        Assert.Equal("0.p0.1", Positions.After(def, "0.p0.0"));
        Assert.Equal("1", Positions.After(def, "0.p0.1"));
        Assert.Equal("a2", Positions.StepAt(def, "0.p0.1")!.Id);
        Assert.Null(Positions.StepAt(def, "0.p7.0"));
        Assert.Equal(["p", "a1", "a2", "o1", "after"], def.AllSteps().Select(s => s.Id));
    }

    [Fact]
    public void Validation_covers_each_new_step()
    {
        var def = Build.Flow(
            new StepDef { Id = "f1", Type = StepTypes.Filter, Config = new JsonObject() },
            Power.Formatter("f2", new JsonObject { ["kind"] = "text", ["op"] = "trim" }),
            new StepDef { Id = "v1", Type = StepTypes.Variable, Config = new JsonObject { ["assignments"] = new JsonArray(new JsonObject { ["name"] = "1bad" }) } },
            new StepDef { Id = "p1", Type = StepTypes.Paths, Paths = [new PathDef { Name = "Big" }], Config = new JsonObject() },
            Power.Paths("p2", false, null, Enumerable.Range(0, 6).Select(i => Power.Path($"P{i}", "x", "is_empty", null)).ToArray()));
        var errors = DefinitionRules.Validate(def, Limits).Select(e => $"{e.StepId}: {e.Message}").ToList();
        Assert.Contains("f1: Add a condition.", errors);
        Assert.Contains("f2: Choose the value to format.", errors);
        Assert.Contains("v1: “1bad”: a name is letters, numbers and _, starting with a letter.", errors);
        Assert.Contains("p1: Big needs a condition.", errors);
        Assert.Contains("p2: A step can have at most 5 paths.", errors);
    }

    [Fact]
    public void A_path_cant_read_a_sibling_paths_steps_but_later_steps_can()
    {
        var b = Build.Http("b1");
        b.Config["body"] = "{{steps.a1.id}}";
        var after = Build.Http("after");
        after.Config["body"] = "{{steps.a1.id}} {{steps.b1.id}}";
        var def = Build.Flow(Power.Paths("p", false, null, Power.Path("A", "x", "is_empty", null, Build.Http("a1")), Power.Path("B", "x", "is_empty", null, b)), after);
        var errors = DefinitionRules.Validate(def, Limits);
        Assert.Equal("b1", Assert.Single(errors).StepId);
    }

    [Fact]
    public void Path_conditions_can_only_read_earlier_steps()
    {
        var def = Build.Flow(Power.Paths("p", false, null, Power.Path("A", "{{steps.later.id}}", "is_empty", null)), Build.Http("later"));
        Assert.Contains(DefinitionRules.Validate(def, Limits), e => e.StepId == "p" && e.Message.Contains("isn't from an earlier step"));
    }

    [Fact]
    public void A_variable_token_needs_a_step_that_sets_it()
    {
        var s = Build.Http("s1");
        s.Config["body"] = "{{vars.missing}}";
        Assert.Contains(DefinitionRules.Validate(Build.Flow(s), Limits), e => e.Message == "{{vars.missing}} isn't set by a Set variable step.");
    }

    [Fact]
    public void Paths_count_toward_depth_and_the_step_cap()
    {
        PathDef Nest(int depth) => depth == 0
            ? Power.Path("leaf", "x", "is_empty", null, Build.Http("leaf"))
            : Power.Path($"d{depth}", "x", "is_empty", null, Power.Paths($"p{depth}", false, null, Nest(depth - 1)));
        var def = Build.Flow(Power.Paths("p9", false, null, Nest(2)));
        Assert.Contains(DefinitionRules.Validate(def, Limits), e => e.Message.StartsWith("Paths can be nested"));
        var many = Build.Flow(Power.Paths("p", false, null, Power.Path("A", "x", "is_empty", null, Enumerable.Range(0, 30).Select(i => Build.Http($"h{i}")).ToArray())));
        Assert.Contains(DefinitionRules.Validate(many, Limits), e => e.Message == "A flow can have at most 25 steps.");
    }

    [Fact]
    public void An_if_else_flow_saved_before_paths_reads_and_runs_as_it_did()
    {
        const string json = """
        {"trigger":{"app":"orbit","event":"record.created","config":{"tableId":"t"}},
         "steps":[{"id":"b1","type":"branch","config":{"condition":{"match":"all","rules":[{"left":"{{trigger.x}}","op":"is_empty"}]}},
                   "then":[{"id":"s1","type":"action","app":"http","action":"request","config":{"url":"https://example.com"}}],"else":[]}],
         "options":{"runOnFlowChanges":false}}
        """;
        var def = FlowDefinition.Parse(json)!;
        Assert.Null(def.Steps[0].Paths);
        Assert.Empty(DefinitionRules.Validate(def, Limits));
        Assert.Equal("0.t.0", Positions.Enter(def, "0", then: true));
    }

    [Fact]
    public void The_versions_migration_is_one_azure_ready_batch()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "003_flow_versions.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\bCREATE\s+(TABLE|INDEX)\b", RegexOptions.IgnoreCase), outside);
        Assert.Contains("flow.FlowVersions", VantraFlow.Api.Data.FlowSchema.VersionsProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.VersionsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }
}
