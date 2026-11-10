using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>"Repeat for each": a list from an earlier step, the inner steps once per item with its tokens, the cap, and each item's result.</summary>
public sealed class LoopTests
{
    private static StepDef Loop(string id, string items, List<StepDef> steps, int? max = null, string? onError = null)
    {
        var config = new JsonObject { ["items"] = items };
        if (max is not null) config["max"] = max;
        if (onError is not null) config["onError"] = onError;
        return new StepDef { Id = id, Type = StepTypes.Loop, Config = config, Do = steps };
    }

    private static JsonObject Records(int n) => new()
    {
        ["count"] = n,
        ["records"] = new JsonArray(Enumerable.Range(1, n).Select(i => (JsonNode)new JsonObject
        {
            ["id"] = $"rec_{i}", ["fields"] = new JsonObject { ["Name"] = $"Lead {i}", ["Email"] = $"lead{i}@example.com" },
        }).ToArray()),
    };

    private static FlowDefinition Def(params StepDef[] steps) => Build.Flow(steps);

    [Fact]
    public async Task Each_item_runs_the_inner_steps_with_its_own_tokens()
    {
        var def = Def(
            Build.Http("find"),
            Loop("each", "{{steps.find.records}}", [Build.Email("mail", "{{loop.item.fields.Email}}", "Hi {{loop.item.fields.Name}} ({{loop.number}} of {{loop.count}})")]),
            Build.Http("after", "https://example.com/{{steps.each.succeeded}}"));
        var actions = new FakeActions().On("find", StepOutcome.Done(Records(3)));
        var run = Build.Run(def);
        var logs = await new RunExecutor(actions, new FlowLimits()).ExecuteAsync(run, default);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        var mails = actions.Calls.Where(c => c.Step.Id == "mail").ToList();
        Assert.Equal(["lead1@example.com", "lead2@example.com", "lead3@example.com"], mails.Select(c => c.Input["to"]!.GetValue<string>()));
        Assert.Equal("Hi Lead 2 (2 of 3)", mails[1].Input["subject"]!.GetValue<string>());
        Assert.Equal("https://example.com/3", actions.Calls.Last().Input["url"]!.GetValue<string>());
        Assert.Equal(["Send email · item 1", "Send email · item 2", "Send email · item 3"], logs.Where(l => l.StepId == "mail").Select(l => l.Name));
        var loop = run.Steps["each"]!;
        Assert.Equal(3, loop["count"]!.GetValue<int>());
        Assert.Equal("Lead 1", loop["results"]![0]!["label"]!.GetValue<string>());
        Assert.Null(run.Data["loop"]);
    }

    [Fact]
    public async Task The_list_is_capped_and_a_test_goes_through_the_first_three()
    {
        var def = Def(Build.Http("find"), Loop("each", "{{steps.find}}", [Build.Http("call")], max: 5));
        var actions = new FakeActions().On("find", StepOutcome.Done(Records(8)));
        var run = Build.Run(def);
        var logs = await new RunExecutor(actions, new FlowLimits()).ExecuteAsync(run, default, maxSteps: 200);
        Assert.Equal(5, actions.Calls.Count(c => c.Step.Id == "call"));
        Assert.Equal("Only the first 5 of 8 items.", logs.Single(l => l.StepId == "each").Error);

        var test = new FakeActions().On("find", StepOutcome.Done(Records(8)));
        var testRun = Build.Run(def, test: true);
        await new RunExecutor(test, new FlowLimits()).ExecuteAsync(testRun, default, maxSteps: 200);
        Assert.Equal(LoopRules.TestItems, test.Calls.Count(c => c.Step.Id == "call"));
        Assert.Equal(RunStatuses.Succeeded, testRun.Status);
    }

    [Fact]
    public async Task A_failed_item_stops_the_run_or_is_skipped()
    {
        StepOutcome Ok() => StepOutcome.Done(new JsonObject());
        var stop = new FakeActions().On("find", StepOutcome.Done(Records(3))).On("call", Ok(), StepOutcome.Fail("Refused."), Ok());
        var run = Build.Run(Def(Build.Http("find"), Loop("each", "{{steps.find.records}}", [Build.Http("call")])));
        var logs = await new RunExecutor(stop, new FlowLimits()).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("HTTP request · item 2: Refused.", run.Error);
        Assert.Equal(2, stop.Calls.Count(c => c.Step.Id == "call"));
        Assert.Equal("failed", logs.Last().Status);
        Assert.Equal("each", logs.Last().StepId);

        var go = new FakeActions().On("find", StepOutcome.Done(Records(3))).On("call", Ok(), StepOutcome.Fail("Refused."), Ok());
        var run2 = Build.Run(Def(Build.Http("find"), Loop("each", "{{steps.find.records}}", [Build.Http("call")], onError: "continue")));
        var logs2 = await new RunExecutor(go, new FlowLimits()).ExecuteAsync(run2, default);
        Assert.Equal(RunStatuses.Succeeded, run2.Status);
        Assert.Equal(3, go.Calls.Count(c => c.Step.Id == "call"));
        var results = run2.Steps["each"]!["results"]!.AsArray();
        Assert.Equal(["succeeded", "failed", "succeeded"], results.Select(r => r!["status"]!.GetValue<string>()));
        Assert.Equal("1 of 3 items failed.", logs2.Single(l => l.StepId == "each").Error);
    }

    [Fact]
    public async Task A_loop_resumes_across_goes_and_retries()
    {
        var def = Def(Build.Http("find"), Loop("each", "{{steps.find.records}}", [Build.Http("call")]));
        var actions = new FakeActions().On("find", StepOutcome.Done(Records(4)));
        var run = Build.Run(def);
        var exec = new RunExecutor(actions, new FlowLimits());
        await exec.ExecuteAsync(run, default, maxSteps: 3);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Equal("1", run.Position);   // the first item done: back at the loop
        for (var i = 0; i < 10 && !RunStatuses.Finished(run.Status); i++) await exec.ExecuteAsync(run, default, maxSteps: 3);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(4, actions.Calls.Count(c => c.Step.Id == "call"));
    }

    [Fact]
    public async Task An_empty_list_skips_the_steps_and_a_non_list_fails()
    {
        var empty = new FakeActions().On("find", StepOutcome.Done(Records(0)));
        var run = Build.Run(Def(Build.Http("find"), Loop("each", "{{steps.find.records}}", [Build.Http("call")]), Build.Http("after")));
        await new RunExecutor(empty, new FlowLimits()).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.DoesNotContain(empty.Calls, c => c.Step.Id == "call");
        Assert.Contains(empty.Calls, c => c.Step.Id == "after");

        var bad = Build.Run(Def(Loop("each", "{{trigger.record.fields.Name}}", [Build.Http("call")])));
        await new RunExecutor(new FakeActions(), new FlowLimits()).ExecuteAsync(bad, default);
        Assert.Equal(RunStatuses.Failed, bad.Status);
    }

    [Fact]
    public void Loops_are_checked()
    {
        var limits = new FlowLimits();
        Assert.Empty(DefinitionRules.Validate(Def(Build.Http("find"), Loop("each", "{{steps.find.records}}", [Build.Email("mail", "{{loop.item.fields.Email}}")])), limits));
        Assert.Contains(DefinitionRules.Validate(Def(Loop("each", "", [Build.Http("a")])), limits), e => e.Message == "Choose the list to go through.");
        Assert.Contains(DefinitionRules.Validate(Def(Loop("each", "{{trigger.x}}", [])), limits), e => e.Message == "Add a step to repeat.");
        Assert.Contains(DefinitionRules.Validate(Def(Loop("each", "{{trigger.x}}", [Build.Http("a")], max: 500)), limits), e => e.StepId == "each");
        Assert.Contains(DefinitionRules.Validate(Def(Loop("each", "{{trigger.x}}", [Loop("inner", "{{loop.item.list}}", [Build.Http("a")])])), limits),
            e => e.Message == "A Repeat for each can't be inside another.");
        Assert.Contains(DefinitionRules.Validate(Def(Build.Email("mail", "{{loop.item.email}}")), limits), e => e.StepId == "mail");
        // The inner steps count towards the flow's steps.
        Assert.Equal(3, Def(Build.Http("find"), Loop("each", "{{steps.find}}", [Build.Http("a")])).AllSteps().Count());
    }

    [Fact]
    public void Positions_go_into_a_loop_and_back_round()
    {
        var def = Def(Build.Http("a"), Loop("each", "{{trigger.x}}", [Build.Http("b"), Build.Http("c")]), Build.Http("d"));
        Assert.Equal("1.d.0", Positions.EnterLoop(def, "1"));
        Assert.Equal("1.d.1", Positions.After(def, "1.d.0"));
        Assert.Equal("1", Positions.After(def, "1.d.1"));   // back to the loop for the next item
        Assert.Equal("2", Positions.After(def, "1"));
        Assert.Equal("1", Positions.LoopOf("1.d.1"));
        Assert.Null(Positions.LoopOf("1"));
        Assert.Equal("c", Positions.StepAt(def, "1.d.1")!.Id);
    }
}

/// <summary>The failure alert's email: the count since the last one, the newest failures, a link to the run.</summary>
public sealed class FailureAlertTests
{
    [Fact]
    public void The_email_names_the_flow_counts_the_failures_and_links_the_newest_run()
    {
        var ws = Guid.Parse("8c1f6a52-6b8e-4d1e-9a5b-2f4c7d9e1a01");
        var runs = new List<VantraFlow.Api.Data.AlertFailure>
        {
            new("frn_2", new DateTime(2026, 11, 10, 14, 5, 0, DateTimeKind.Utc), "Create task: <b>Refused</b>."),
            new("frn_1", new DateTime(2026, 11, 10, 13, 40, 0, DateTimeKind.Utc), null),
        };
        var (subject, html) = FailureAlerts.Compose("New lead → task", "Acme", runs, 7, "https://flow.example.com", ws);
        Assert.Equal("New lead → task failed 7 times", subject);
        Assert.Contains("https://flow.example.com/runs/frn_2?ws=8c1f6a52-6b8e-4d1e-9a5b-2f4c7d9e1a01", html);
        Assert.Contains("&lt;b&gt;Refused&lt;/b&gt;", html);   // errors are escaped
        Assert.Contains("And 5 more.", html);
        Assert.Equal("Lead failed", FailureAlerts.Compose("Lead", "Acme", runs[..1], 1, "https://flow.example.com", ws).Subject);
    }
}
