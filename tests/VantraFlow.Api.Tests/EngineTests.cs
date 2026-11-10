using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Records each call and answers from a script: the step id → what it returns (or a list, one per try).</summary>
internal sealed class FakeActions : IStepActions
{
    public readonly List<StepCall> Calls = [];
    public readonly Dictionary<string, Queue<StepOutcome>> Script = new();

    public FakeActions On(string stepId, params StepOutcome[] outcomes)
    {
        Script[stepId] = new Queue<StepOutcome>(outcomes);
        return this;
    }

    public Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct)
    {
        Calls.Add(call);
        if (Script.TryGetValue(call.Step.Id, out var q) && q.Count > 0) return Task.FromResult(q.Count > 1 ? q.Dequeue() : q.Peek());
        return Task.FromResult(StepOutcome.Done(new JsonObject { ["id"] = "out_" + call.Step.Id, ["echo"] = call.Input.DeepClone() }));
    }
}

internal static class Build
{
    public static StepDef Email(string id, string to = "{{trigger.record.fields.Email}}", string subject = "Hi {{trigger.record.fields.Name}}") => new()
    {
        Id = id, Type = StepTypes.Action, App = "email", Action = "send",
        Config = new JsonObject { ["to"] = to, ["subject"] = subject, ["body"] = "Hello" },
    };

    public static StepDef Http(string id, string url = "https://example.com/hook") => new()
    {
        Id = id, Type = StepTypes.Action, App = "http", Action = "request", Config = new JsonObject { ["url"] = url, ["method"] = "POST" },
    };

    public static StepDef Delay(string id, double amount, string unit = "hours") => new()
    {
        Id = id, Type = StepTypes.Delay, Config = new JsonObject { ["amount"] = amount, ["unit"] = unit },
    };

    public static StepDef Branch(string id, string left, string op, string? right, List<StepDef> then, List<StepDef> otherwise) => new()
    {
        Id = id, Type = StepTypes.Branch, Then = then, Else = otherwise,
        Config = new JsonObject
        {
            ["condition"] = new JsonObject
            {
                ["match"] = "all",
                ["rules"] = new JsonArray(new JsonObject { ["left"] = left, ["op"] = op, ["right"] = right }),
            },
        },
    };

    public static FlowDefinition Flow(params StepDef[] steps) => new()
    {
        Trigger = new TriggerDef { App = "orbit", Event = "record.created", Config = new JsonObject { ["tableId"] = "tbl_leads" } },
        Steps = steps.ToList(),
    };

    public static JsonObject Trigger(string stage = "Won") => new()
    {
        ["event"] = "record.created",
        ["table"] = new JsonObject { ["id"] = "tbl_leads", ["name"] = "Leads" },
        ["record"] = new JsonObject
        {
            ["id"] = "rec_1",
            ["fields"] = new JsonObject { ["Name"] = "Ada", ["Email"] = "ada@example.com", ["Stage"] = stage, ["Deal value"] = 1200 },
        },
        ["changes"] = new JsonArray(),
    };

    public static RunState Run(FlowDefinition def, JsonObject? trigger = null, bool test = false) => new()
    {
        Id = "frn_test", WorkspaceId = Guid.NewGuid(), FlowId = "flw_test", Definition = def,
        Data = RunState.NewData(trigger ?? Trigger(), "frn_test", DateTime.UtcNow, "Acme", "UTC", test),
        Position = Positions.Start(def), Test = test, NotBefore = DateTime.UtcNow,
    };
}

public sealed class RunExecutorTests
{
    private static readonly FlowLimits Limits = new();

    [Fact]
    public async Task Runs_every_step_and_feeds_outputs_to_later_tokens()
    {
        var second = Build.Http("s2");
        second.Config["body"] = "{{steps.s1.id}} for {{trigger.record.fields.Deal value}}";
        var def = Build.Flow(Build.Email("s1"), second);
        var actions = new FakeActions();
        var run = Build.Run(def);

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["s1", "s2"], logs.Select(l => l.StepId));
        Assert.All(logs, l => Assert.Equal("succeeded", l.Status));
        Assert.Equal("ada@example.com", actions.Calls[0].Input["to"]!.GetValue<string>());
        Assert.Equal("Hi Ada", actions.Calls[0].Input["subject"]!.GetValue<string>());
        Assert.Equal("out_s1 for 1200", actions.Calls[1].Input["body"]!.GetValue<string>());
        Assert.NotNull(run.Steps["s2"]);
        Assert.Equal(2, run.StepCount);
    }

    [Theory]
    [InlineData("Won", "s_then")]
    [InlineData("Lost", "s_else")]
    public async Task A_branch_takes_one_side_then_carries_on(string stage, string expected)
    {
        var def = Build.Flow(
            Build.Branch("b1", "{{trigger.record.fields.Stage}}", "equals", "won", [Build.Http("s_then")], [Build.Http("s_else")]),
            Build.Http("s_after"));
        var actions = new FakeActions();
        var run = Build.Run(def, Build.Trigger(stage));

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["b1", expected, "s_after"], logs.Select(l => l.StepId));
        Assert.Equal(stage == "Won", run.Steps["b1"]!["result"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_delay_waits_then_resumes_where_it_stopped()
    {
        var def = Build.Flow(Build.Http("s1"), Build.Delay("d1", 2), Build.Http("s2"));
        var actions = new FakeActions();
        var now = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var run = Build.Run(def);

        var first = await new RunExecutor(actions, Limits, () => now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status);
        Assert.Equal(now.AddHours(2), run.NotBefore);
        Assert.Equal(["s1", "d1"], first.Select(l => l.StepId));
        Assert.Equal("waiting", first[1].Status);

        // Too early: still waiting, nothing new in the log.
        var early = await new RunExecutor(actions, Limits, () => now.AddHours(1)).ExecuteAsync(run, CancellationToken.None);
        Assert.Empty(early);
        Assert.Equal(RunStatuses.Waiting, run.Status);

        // Due: the delay closes and the rest runs.
        var later = await new RunExecutor(actions, Limits, () => now.AddHours(2).AddSeconds(1)).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["d1", "s2"], later.Select(l => l.StepId));
        Assert.Single(actions.Calls, c => c.Step.Id == "s2");
    }

    [Fact]
    public async Task A_retryable_failure_backs_off_then_succeeds()
    {
        var step = Build.Http("s1");
        step.Retry = new RetryDef { Max = 3, BackoffSeconds = 30 };
        var def = Build.Flow(step);
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 503", retryable: true), StepOutcome.Fail("HTTP 503", retryable: true),
            StepOutcome.Done(new JsonObject { ["status"] = 200 }));
        var now = new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);
        var run = Build.Run(def);
        var exec = new RunExecutor(actions, Limits, () => now);

        var a = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Equal(now.AddSeconds(30), run.NotBefore);
        Assert.Equal("retrying", a.Single().Status);

        var b = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(now.AddSeconds(60), run.NotBefore); // doubled
        Assert.Equal(1, b.Single().Attempt);

        var c = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal("succeeded", c.Single().Status);
        Assert.Equal(2, c.Single().Attempt);
    }

    [Fact]
    public async Task A_refusal_fails_the_run_at_once_with_the_steps_words()
    {
        var def = Build.Flow(Build.Email("s1"), Build.Http("s2"));
        var actions = new FakeActions().On("s1", StepOutcome.Fail("“nobody” isn't an email address."));
        var run = Build.Run(def);

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("Send email: “nobody” isn't an email address.", run.Error);
        Assert.Equal("failed", logs.Single().Status);
        Assert.DoesNotContain(actions.Calls, c => c.Step.Id == "s2");
    }

    [Fact]
    public async Task Retries_run_out_and_the_run_fails()
    {
        var step = Build.Http("s1");
        step.Retry = new RetryDef { Max = 1, BackoffSeconds = 5 };
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 500", retryable: true));
        var run = Build.Run(Build.Flow(step));
        var exec = new RunExecutor(actions, Limits);

        await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Queued, run.Status);
        await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Failed, run.Status);
    }

    [Fact]
    public async Task A_test_run_skips_delays_and_never_retries()
    {
        var def = Build.Flow(Build.Delay("d1", 3, "days"), Build.Http("s1"));
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 502", retryable: true));
        var run = Build.Run(def, test: true);

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, CancellationToken.None);

        Assert.Equal(["skipped", "failed"], logs.Select(l => l.Status));
        Assert.Equal(RunStatuses.Failed, run.Status);
    }

    [Fact]
    public void Backoff_doubles_and_is_capped_at_an_hour()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), RunExecutor.Backoff(30, 1));
        Assert.Equal(TimeSpan.FromSeconds(120), RunExecutor.Backoff(30, 3));
        Assert.Equal(TimeSpan.FromHours(1), RunExecutor.Backoff(600, 10));
    }
}

public sealed class PositionsTests
{
    [Fact]
    public void Walks_into_and_out_of_branches()
    {
        var def = Build.Flow(
            Build.Http("a"),
            Build.Branch("b", "x", "is_empty", null, [Build.Http("t1"), Build.Http("t2")], []),
            Build.Http("c"));
        Assert.Equal("1", Positions.After(def, "0"));
        Assert.Equal("1.t.0", Positions.Enter(def, "1", then: true));
        Assert.Equal("2", Positions.Enter(def, "1", then: false)); // an empty side goes straight past
        Assert.Equal("1.t.1", Positions.After(def, "1.t.0"));
        Assert.Equal("2", Positions.After(def, "1.t.1"));
        Assert.Null(Positions.After(def, "2"));
        Assert.Equal("t2", Positions.StepAt(def, "1.t.1")!.Id);
        Assert.Null(Positions.StepAt(def, "1.t"));
        Assert.Null(Positions.StepAt(def, "9"));
    }
}
