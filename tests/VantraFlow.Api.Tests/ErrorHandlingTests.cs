using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>"On error": retries and their waits, timeouts, going on past a failure, error paths, and "Retry from here".</summary>
public sealed class ErrorHandlingTests
{
    private static readonly FlowLimits Limits = new();
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    private static StepDef Loop(string id, string items, List<StepDef> steps, string? onError = null)
    {
        var config = new JsonObject { ["items"] = items };
        if (onError is not null) config["onError"] = onError;
        return new StepDef { Id = id, Type = StepTypes.Loop, Config = config, Do = steps };
    }

    private static JsonObject Records(int n) => new()
    {
        ["records"] = new JsonArray(Enumerable.Range(1, n).Select(i => (JsonNode)new JsonObject { ["id"] = $"rec_{i}", ["name"] = $"Lead {i}" }).ToArray()),
    };

    private static async Task<List<StepLog>> RunToEnd(RunExecutor exec, RunState run, int goes = 20)
    {
        var all = new List<StepLog>();
        for (var i = 0; i < goes && !RunStatuses.Finished(run.Status); i++) all.AddRange(await exec.ExecuteAsync(run, default));
        return all;
    }

    // ── Retries ──

    [Fact]
    public async Task With_no_settings_a_step_retries_twice_doubling_from_30_seconds_as_before()
    {
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 503", retryable: true));
        var run = Build.Run(Build.Flow(Build.Http("s1")));
        var exec = new RunExecutor(actions, Limits, () => Now);

        await exec.ExecuteAsync(run, default);
        Assert.Equal(Now.AddSeconds(30), run.NotBefore);
        await exec.ExecuteAsync(run, default);
        Assert.Equal(Now.AddSeconds(60), run.NotBefore);
        var last = await exec.ExecuteAsync(run, default);

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal(3, actions.Calls.Count);
        Assert.Equal(2, last.Single().Attempt);
    }

    [Fact]
    public async Task A_fixed_wait_stays_the_same_and_exponential_doubles()
    {
        foreach (var (backoff, second) in new[] { (ErrorRules.Fixed, 60), (ErrorRules.Exponential, 120) })
        {
            var step = Build.Http("s1");
            step.Retry = new RetryDef { Max = 3, BackoffSeconds = 60, Backoff = backoff };
            var run = Build.Run(Build.Flow(step));
            var exec = new RunExecutor(new FakeActions().On("s1", StepOutcome.Fail("HTTP 500", retryable: true)), Limits, () => Now);
            await exec.ExecuteAsync(run, default);
            Assert.Equal(Now.AddSeconds(60), run.NotBefore);
            await exec.ExecuteAsync(run, default);
            Assert.Equal(Now.AddSeconds(second), run.NotBefore);
        }
        Assert.Equal(TimeSpan.FromHours(1), ErrorRules.WaitBefore(new ErrorRules.RetryPolicy(5, 3600, true, false), 5));
    }

    [Fact]
    public async Task Retries_off_fail_at_once_and_only_transient_errors_retry_unless_all_are_chosen()
    {
        var off = Build.Http("s1");
        off.Retry = new RetryDef { Max = 0, BackoffSeconds = 30 };
        var a = new FakeActions().On("s1", StepOutcome.Fail("HTTP 503", retryable: true));
        var run = Build.Run(Build.Flow(off));
        await new RunExecutor(a, Limits).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Single(a.Calls);

        // A refusal (400) isn't retried by default…
        var refused = new FakeActions().On("s1", StepOutcome.Fail("HTTP 400"));
        var run2 = Build.Run(Build.Flow(Build.Http("s1")));
        await new RunExecutor(refused, Limits).ExecuteAsync(run2, default);
        Assert.Equal(RunStatuses.Failed, run2.Status);
        Assert.Single(refused.Calls);

        // …but is with "all errors".
        var all = Build.Http("s1");
        all.Retry = new RetryDef { Max = 1, BackoffSeconds = 5, On = ErrorRules.All };
        var tried = new FakeActions().On("s1", StepOutcome.Fail("HTTP 400"), StepOutcome.Done(new JsonObject()));
        var run3 = Build.Run(Build.Flow(all));
        var logs = await RunToEnd(new RunExecutor(tried, Limits), run3);
        Assert.Equal(RunStatuses.Succeeded, run3.Status);
        Assert.Equal(["retrying", "succeeded"], logs.Select(l => l.Status));
        Assert.Equal([0, 1], logs.Select(l => l.Attempt));
    }

    [Fact]
    public async Task A_test_run_never_retries()
    {
        var a = new FakeActions().On("s1", StepOutcome.Fail("HTTP 503", retryable: true));
        var run = Build.Run(Build.Flow(Build.Http("s1")), test: true);
        await new RunExecutor(a, Limits).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Single(a.Calls);
    }

    // ── Timeouts ──

    [Fact]
    public void A_steps_timeout_is_held_to_the_engines_caps()
    {
        var s = Build.Http("s1");
        Assert.Equal(30, ErrorRules.TimeoutOf(s, Limits));
        s.TimeoutSeconds = 10;
        Assert.Equal(10, ErrorRules.TimeoutOf(s, Limits));
        s.TimeoutSeconds = 1000;
        Assert.Equal(FlowLimits.MaxStepTimeoutSeconds, ErrorRules.TimeoutOf(s, Limits));
        s.TimeoutSeconds = 1;
        Assert.Equal(FlowLimits.MinStepTimeoutSeconds, ErrorRules.TimeoutOf(s, Limits));
    }

    private sealed class SlowActions : IStepActions
    {
        public readonly List<StepCall> Calls = [];
        public async Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct)
        {
            Calls.Add(call);
            await Task.Delay(Timeout.Infinite, ct);
            return StepOutcome.Done(null);
        }
    }

    [Fact]
    public async Task A_try_that_takes_too_long_times_out_with_the_steps_own_timeout_and_is_retried()
    {
        var step = Build.Http("s1");
        step.TimeoutSeconds = 5;
        var slow = new SlowActions();
        var run = Build.Run(Build.Flow(step));
        var started = DateTime.UtcNow;

        var logs = await new RunExecutor(slow, Limits).ExecuteAsync(run, default);

        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 4.5, 20);
        Assert.Equal(5, slow.Calls.Single().TimeoutSeconds);
        Assert.Equal("retrying", logs.Single().Status);
        Assert.StartsWith("No answer within 5 seconds.", logs.Single().Error);
    }

    // ── If it still fails ──

    [Fact]
    public async Task Continue_goes_on_with_the_next_step_and_hands_on_the_errors_tokens()
    {
        var bad = Build.Http("s1");
        bad.OnError = ErrorRules.Continue;
        var next = Build.Email("s2", subject: "{{steps.s1.error.step}} failed after {{steps.s1.error.attempts}}: {{steps.s1.error.message}}");
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 400 Bad request"));
        var run = Build.Run(Build.Flow(bad, next));

        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, default);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["failed", "succeeded"], logs.Select(l => l.Status));
        Assert.Equal("HTTP request failed after 1: HTTP 400 Bad request", actions.Calls[1].Input["subject"]!.GetValue<string>());
        Assert.True(run.Steps["s1"]!["failed"]!.GetValue<bool>());
        Assert.Contains("\"onError\":\"continue\"", logs[0].Output);
    }

    [Fact]
    public async Task An_error_path_runs_with_the_errors_tokens_then_the_run_stops_as_failed()
    {
        var bad = Build.Http("s1");
        bad.OnError = ErrorRules.Path;
        bad.Retry = new RetryDef { Max = 1, BackoffSeconds = 5 };
        bad.ErrorPath = [Build.Email("e1", "ops@example.com", "{{steps.s1.error.step}}: {{steps.s1.error.message}} ({{steps.s1.error.attempts}} tries)")];
        var actions = new FakeActions().On("s1", StepOutcome.Fail("HTTP 503", retryable: true));
        var run = Build.Run(Build.Flow(bad, Build.Http("after")));

        var logs = await RunToEnd(new RunExecutor(actions, Limits), run);

        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("HTTP request: HTTP 503", run.Error);
        Assert.Equal(["s1", "s1", "e1"], logs.Select(l => l.StepId));
        Assert.Equal(["retrying", "failed", "succeeded"], logs.Select(l => l.Status));
        Assert.Contains("\"onError\":\"path\"", logs[1].Output);
        Assert.Equal("HTTP request: HTTP 503 (2 tries)", actions.Calls.Single(c => c.Step.Id == "e1").Input["subject"]!.GetValue<string>());
        Assert.DoesNotContain(actions.Calls, c => c.Step.Id == "after");
        Assert.Null(run.Steps["s1"]!["handling"]);
    }

    [Fact]
    public async Task An_error_path_waits_durably_on_a_delay_and_still_stops_the_run_after()
    {
        var bad = Build.Http("s1");
        bad.OnError = ErrorRules.Path;
        bad.Retry = new RetryDef { Max = 0 };
        bad.ErrorPath = [Build.Delay("wait", 1), Build.Email("e1", "ops@example.com")];
        var actions = new FakeActions().On("s1", StepOutcome.Fail("Refused."));
        var run = Build.Run(Build.Flow(bad));

        await new RunExecutor(actions, Limits, () => Now).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Waiting, run.Status);
        Assert.Equal("0.x.0", run.Position);
        // A later go, picked up from the database: the state round-trips through JSON.
        var reloaded = new RunState
        {
            Id = run.Id, WorkspaceId = run.WorkspaceId, FlowId = run.FlowId, Definition = FlowDefinition.Parse(run.Definition.ToJson())!,
            Data = (JsonObject)JsonNode.Parse(run.Data.ToJsonString())!, Position = run.Position, Attempt = run.Attempt, NotBefore = run.NotBefore,
        };
        await new RunExecutor(actions, Limits, () => Now.AddHours(2)).ExecuteAsync(reloaded, default);
        Assert.Equal(RunStatuses.Failed, reloaded.Status);
        Assert.Single(actions.Calls, c => c.Step.Id == "e1");
        Assert.Equal("HTTP request: Refused.", reloaded.Error);
    }

    [Fact]
    public async Task Inside_a_loop_that_goes_on_an_error_path_runs_for_the_item_and_the_next_item_starts()
    {
        var call = Build.Http("call");
        call.OnError = ErrorRules.Path;
        call.Retry = new RetryDef { Max = 0 };
        call.ErrorPath = [Build.Email("note", "ops@example.com", "Item {{loop.number}}: {{steps.call.error.message}}")];
        var def = Build.Flow(Build.Http("find"), Loop("each", "{{steps.find.records}}", [call], onError: "continue"));
        var actions = new FakeActions().On("find", StepOutcome.Done(Records(3))).On("call", StepOutcome.Done(new JsonObject()), StepOutcome.Fail("Refused."),
            StepOutcome.Done(new JsonObject()));
        var run = Build.Run(def);

        await RunToEnd(new RunExecutor(actions, Limits), run);

        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(3, actions.Calls.Count(c => c.Step.Id == "call"));
        Assert.Equal("Item 2: Refused.", actions.Calls.Single(c => c.Step.Id == "note").Input["subject"]!.GetValue<string>());
        Assert.Equal(["succeeded", "failed", "succeeded"], run.Steps["each"]!["results"]!.AsArray().Select(r => r!["status"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_step_in_the_error_path_that_fails_stops_the_run_with_its_own_error()
    {
        var bad = Build.Http("s1");
        bad.OnError = ErrorRules.Path;
        bad.Retry = new RetryDef { Max = 0 };
        bad.ErrorPath = [Build.Email("e1")];
        var actions = new FakeActions().On("s1", StepOutcome.Fail("Refused.")).On("e1", StepOutcome.Fail("No address."));
        var run = Build.Run(Build.Flow(bad));
        await RunToEnd(new RunExecutor(actions, Limits), run);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("Send email: No address.", run.Error);
    }

    // ── Definitions ──

    [Fact]
    public void Existing_definitions_need_no_migration_and_round_trip_unchanged()
    {
        const string old = """{"trigger":{"app":"orbit","event":"record.created","config":{"tableId":"tbl_leads"}},"steps":[{"id":"s1","type":"action","app":"http","action":"request","config":{"url":"https://example.com","method":"POST"},"retry":{"max":3,"backoffSeconds":60}}],"options":{"runOnFlowChanges":false}}""";
        var def = FlowDefinition.Parse(old)!;
        var json = def.ToJson();
        Assert.DoesNotContain("onError", json);
        Assert.DoesNotContain("errorPath", json);
        Assert.DoesNotContain("timeoutSeconds", json);
        Assert.DoesNotContain("backoff\"", json);
        Assert.Empty(DefinitionRules.Validate(def, Limits));
        Assert.Equal(ErrorRules.Stop, ErrorRules.ModeOf(def.Steps[0]));
        Assert.Equal(new ErrorRules.RetryPolicy(3, 60, true, false), ErrorRules.RetryOf(def.Steps[0]));
    }

    [Fact]
    public void Validation_checks_the_settings_and_the_error_path()
    {
        var bad = Build.Http("s1");
        bad.Retry = new RetryDef { Max = 9, BackoffSeconds = 30, Backoff = "sometimes", On = "some" };
        bad.TimeoutSeconds = 500;
        bad.OnError = "panic";
        var errors = DefinitionRules.Validate(Build.Flow(bad), Limits).Select(e => e.Message).ToList();
        Assert.Contains("Retries: up to 5, waiting 5 seconds to an hour.", errors);
        Assert.Contains("Choose a fixed or a growing wait between retries.", errors);
        Assert.Contains("Choose which errors are retried.", errors);
        Assert.Contains("A step's timeout can be 5 to 120 seconds.", errors);
        Assert.Contains("Choose what happens if it still fails.", errors);

        var empty = Build.Http("s1");
        empty.OnError = ErrorRules.Path;
        Assert.Contains(DefinitionRules.Validate(Build.Flow(empty), Limits), e => e.Message == "Add a step to its error path.");

        // The error path sees the failed step's error; the steps after it don't need it; ids stay unique.
        var good = Build.Http("s1");
        good.OnError = ErrorRules.Path;
        good.ErrorPath = [Build.Email("e1", "ops@example.com", "{{steps.s1.error.message}}")];
        Assert.Empty(DefinitionRules.Validate(Build.Flow(good, Build.Email("s2", subject: "{{steps.e1.id}}")), Limits));
        var dup = Build.Http("s1");
        dup.OnError = ErrorRules.Path;
        dup.ErrorPath = [Build.Email("s2")];
        Assert.Contains(DefinitionRules.Validate(Build.Flow(dup, Build.Email("s2")), Limits), e => e.Message == "Two steps share this id.");

        // No error path inside an error path, and none on a logic step.
        var inner = Build.Http("e1");
        inner.OnError = ErrorRules.Path;
        inner.ErrorPath = [Build.Email("e2")];
        var outer = Build.Http("s1");
        outer.OnError = ErrorRules.Path;
        outer.ErrorPath = [inner];
        Assert.Contains(DefinitionRules.Validate(Build.Flow(outer), Limits), e => e.StepId == "e1" && e.Message.Contains("error path of its own"));
        var delay = Build.Delay("d1", 1);
        delay.OnError = ErrorRules.Continue;
        Assert.Contains(DefinitionRules.Validate(Build.Flow(delay), Limits), e => e.StepId == "d1");

        // Error path steps count towards the step cap.
        Assert.Equal(2, Build.Flow(good).AllSteps().Count());
    }

    [Fact]
    public void Positions_go_into_an_error_path_and_back_to_its_step()
    {
        var s = Build.Http("s1");
        s.ErrorPath = [Build.Email("e1"), Build.Email("e2")];
        var def = Build.Flow(Build.Http("s0"), s);
        Assert.Equal("1.x.0", Positions.EnterErrorPath(def, "1"));
        Assert.Equal("1.x.1", Positions.After(def, "1.x.0"));
        Assert.Equal("1", Positions.After(def, "1.x.1"));
        Assert.Equal("e2", Positions.StepAt(def, "1.x.1")!.Id);
        Assert.Equal("1.x.1", Positions.PathOf(def, "e2"));
        Assert.Equal("1", Positions.ErrorOwnerOf("1.x.1"));
        Assert.Null(Positions.ErrorOwnerOf("1.d.0"));
    }

    [Fact]
    public async Task A_go_stops_starting_steps_once_its_time_is_up_and_carries_on_next_go()
    {
        var run = Build.Run(Build.Flow(Build.Http("s1"), Build.Http("s2"), Build.Http("s3")));
        var actions = new FakeActions();
        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(run, default, slice: TimeSpan.Zero);
        Assert.Single(logs);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Equal("1", run.Position);
    }

    // ── Retry from here ──

    [Fact]
    public async Task Retry_from_here_starts_at_the_failed_step_reusing_earlier_outputs()
    {
        var def = Build.Flow(Build.Http("s1"), Build.Email("s2", subject: "Re {{steps.s1.id}}"), Build.Http("s3"));
        def.Steps[1].Retry = new RetryDef { Max = 0 };
        var first = new FakeActions().On("s1", StepOutcome.Done(new JsonObject { ["id"] = "made_1" })).On("s2", StepOutcome.Fail("Mail is down."));
        var run = Build.Run(def);
        await new RunExecutor(first, Limits).ExecuteAsync(run, default);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Equal("s2", RetryRules.StepOf(def, run.Data));

        var start = RetryRules.Resume(def, run.Data, "frn_retry", run.Id, Now, false)!;
        Assert.Equal("1", start.Position);
        Assert.Equal("s2", start.StepId);
        Assert.Null(start.Data["failure"]);
        Assert.Equal("made_1", start.Data["steps"]!["s1"]!["id"]!.GetValue<string>());
        Assert.Equal("frn_test", start.Data["run"]!["retryOf"]!.GetValue<string>());
        Assert.Equal("s2", RetryRules.RetriedFrom(start.Data));

        var again = new FakeActions();
        var retry = new RunState
        {
            Id = "frn_retry", WorkspaceId = run.WorkspaceId, FlowId = run.FlowId, Definition = def, Data = start.Data, Position = start.Position, NotBefore = Now,
        };
        var logs = await new RunExecutor(again, Limits).ExecuteAsync(retry, default);
        Assert.Equal(RunStatuses.Succeeded, retry.Status);
        Assert.Equal(["s2", "s3"], logs.Select(l => l.StepId));
        Assert.DoesNotContain(again.Calls, c => c.Step.Id == "s1");
        Assert.Equal("Re made_1", again.Calls[0].Input["subject"]!.GetValue<string>());
    }

    [Fact]
    public async Task Retry_from_here_inside_a_loop_picks_up_at_the_failed_item()
    {
        var call = Build.Http("call");
        call.Retry = new RetryDef { Max = 0 };
        var def = Build.Flow(Build.Http("find"), Loop("each", "{{steps.find.records}}", [call]));
        var actions = new FakeActions().On("find", StepOutcome.Done(Records(4))).On("call", StepOutcome.Done(new JsonObject()), StepOutcome.Fail("Refused."));
        var run = Build.Run(def);
        await RunToEnd(new RunExecutor(actions, Limits), run);
        Assert.Equal(RunStatuses.Failed, run.Status);

        var start = RetryRules.Resume(def, run.Data, "frn_retry", run.Id, Now, false)!;
        Assert.Equal("1.d.0", start.Position);
        Assert.Equal(2, start.Data["loop"]!["number"]!.GetValue<int>());

        var again = new FakeActions();
        var retry = new RunState { Id = "frn_retry", WorkspaceId = run.WorkspaceId, FlowId = run.FlowId, Definition = def, Data = start.Data, Position = start.Position, NotBefore = Now };
        await RunToEnd(new RunExecutor(again, Limits), retry);
        Assert.Equal(RunStatuses.Succeeded, retry.Status);
        Assert.Equal(3, again.Calls.Count(c => c.Step.Id == "call")); // items 2, 3 and 4
        Assert.DoesNotContain(again.Calls, c => c.Step.Id == "find");
        Assert.Equal(4, retry.Steps["each"]!["results"]!.AsArray().Count);
    }

    [Fact]
    public async Task Retry_from_a_step_in_an_error_path_starts_at_the_step_that_owns_it_and_a_removed_step_cant()
    {
        var bad = Build.Http("s1");
        bad.OnError = ErrorRules.Path;
        bad.Retry = new RetryDef { Max = 0 };
        bad.ErrorPath = [Build.Email("e1")];
        var def = Build.Flow(Build.Http("s0"), bad);
        var actions = new FakeActions().On("s1", StepOutcome.Fail("Refused.")).On("e1", StepOutcome.Fail("No address."));
        var run = Build.Run(def);
        await RunToEnd(new RunExecutor(actions, Limits), run);
        Assert.Equal("s1", RetryRules.StepOf(def, run.Data));
        var start = RetryRules.Resume(def, run.Data, "frn_retry", run.Id, Now, false)!;
        Assert.Equal("1", start.Position);
        Assert.Null(start.Data["steps"]!["s1"]);
        Assert.Null(start.Data["steps"]!["e1"]);
        Assert.NotNull(start.Data["steps"]!["s0"]);

        var fixedFlow = Build.Flow(Build.Http("s0"));
        Assert.Null(RetryRules.Resume(fixedFlow, run.Data, "frn_retry", run.Id, Now, false));
        Assert.Null(RetryRules.StepOf(fixedFlow, run.Data));
    }
}

/// <summary>Auto-pause and the daily failure digest, against an in-memory store.</summary>
public sealed class ReliabilityTests
{
    private sealed class Store : IReliabilityStore
    {
        public readonly List<DigestWorkspace> Workspaces = [];
        public readonly List<DigestRun> Failed = [];
        public readonly Dictionary<Guid, DateTime?> SentOn = new();
        public AutoPauseFlow? Flow;
        public readonly List<string> Statuses = [];
        public readonly List<string> Paused = [];

        public Task<IReadOnlyList<DigestWorkspace>> DigestCandidatesAsync(DateTime latestLocalDay) =>
            Task.FromResult<IReadOnlyList<DigestWorkspace>>(Workspaces.Select(w => w with { DigestSentOn = SentOn.GetValueOrDefault(w.Id) })
                .Where(w => w.DigestSentOn is null || w.DigestSentOn < latestLocalDay).ToList());

        public Task<bool> ClaimDigestAsync(Guid workspaceId, DateTime localDay)
        {
            if (SentOn.GetValueOrDefault(workspaceId) is { } d && d >= localDay) return Task.FromResult(false);
            SentOn[workspaceId] = localDay;
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<DigestRun>> FailedRunsAsync(Guid workspaceId, DateTime since, int take) =>
            Task.FromResult<IReadOnlyList<DigestRun>>(Failed.Where(r => r.FinishedAt >= since).Take(take).ToList());

        public Task<IReadOnlyList<MailContact>> RecipientsAsync(Guid workspaceId, bool admins) =>
            Task.FromResult<IReadOnlyList<MailContact>>(admins ? [new("owner@example.com", "Olive"), new("admin@example.com", "Adam")] : [new("owner@example.com", "Olive")]);

        public Task<bool> CountEmailAsync(Guid workspaceId, DateTime now, int cap) => Task.FromResult(true);
        public Task<AutoPauseFlow?> AutoPauseOfAsync(Guid workspaceId, string flowId) => Task.FromResult(Flow);
        public Task<IReadOnlyList<string>> LastStatusesAsync(Guid workspaceId, string flowId, DateTime since, int take) =>
            Task.FromResult<IReadOnlyList<string>>(Statuses.Take(take).ToList());

        public Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason)
        {
            if (Flow is not { Enabled: true }) return Task.FromResult(false);
            Flow = Flow with { Enabled = false };
            Paused.Add(reason);
            return Task.FromResult(true);
        }
    }

    private sealed class Mail : IReliabilityMail
    {
        public readonly List<(string To, string Subject, string Html)> Sent = [];
        public bool CanSend() => true;
        public Task<bool> SendAsync(string toEmail, string toName, string subject, string html) { Sent.Add((toEmail, subject, html)); return Task.FromResult(true); }
    }

    private static Reliability Make(Store store, Mail mail) => new(store, mail, new FlowLimits(), "https://flow.example.com", NullLogger.Instance);

    [Fact]
    public void The_digest_is_due_from_eight_in_the_workspaces_time_zone_once_a_day()
    {
        var utc7 = new DateTime(2026, 11, 12, 7, 30, 0, DateTimeKind.Utc);
        Assert.Null(ReliabilityRules.DueDay("UTC", utc7, null));
        Assert.Equal(new DateTime(2026, 11, 12), ReliabilityRules.DueDay("UTC", utc7.AddHours(1), null));
        Assert.Null(ReliabilityRules.DueDay("UTC", utc7.AddHours(1), new DateTime(2026, 11, 12)));
        Assert.Equal(new DateTime(2026, 11, 12), ReliabilityRules.DueDay("UTC", utc7.AddHours(1), new DateTime(2026, 11, 11)));
        // 07:30 UTC is 16:30 in Tokyo, and 23:30 the day before in Los Angeles.
        Assert.Equal(new DateTime(2026, 11, 12), ReliabilityRules.DueDay("Asia/Tokyo", utc7, null));
        Assert.Equal(new DateTime(2026, 11, 11), ReliabilityRules.DueDay("America/Los_Angeles", utc7, null));
        Assert.True(ReliabilityRules.LatestLocalDay(utc7) >= new DateTime(2026, 11, 12));
    }

    [Fact]
    public async Task The_digest_goes_once_a_day_to_owners_and_admins_even_when_asked_twice()
    {
        var ws = Guid.NewGuid();
        var store = new Store();
        store.Workspaces.Add(new DigestWorkspace(ws, "Northwind", "Europe/London", null));
        var nine = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        store.Failed.AddRange([
            new("frn_1", "flw_a", "Won deals", "Create project: Vantra Projects is unavailable.", nine.AddHours(-1)),
            new("frn_2", "flw_a", "Won deals", "Create project: Vantra Projects is unavailable.", nine.AddHours(-3)),
            new("frn_3", "flw_b", "Welcome", "Send email: “x” isn't an email address.", nine.AddHours(-5)),
            new("frn_old", "flw_c", "Old", "Too old.", nine.AddHours(-30)),
        ]);
        var mail = new Mail();
        var digest = Make(store, mail);

        Assert.Equal(1, await digest.SendDigestsAsync(nine, default));
        Assert.Equal(0, await digest.SendDigestsAsync(nine.AddMinutes(5), default));
        Assert.Equal(0, await Make(store, mail).SendDigestsAsync(nine.AddHours(6), default)); // another instance, the same day
        Assert.Equal(2, mail.Sent.Count);
        Assert.Equal(["owner@example.com", "admin@example.com"], mail.Sent.Select(m => m.To));
        Assert.Equal("2 flows in Northwind failed in the last day", mail.Sent[0].Subject);
        Assert.Contains("Vantra Projects is unavailable.", mail.Sent[0].Html);
        Assert.Contains("× 2", mail.Sent[0].Html);
        Assert.Contains("runs?flow=flw_a&amp;status=failed", mail.Sent[0].Html);
        Assert.DoesNotContain("Too old", mail.Sent[0].Html);

        // The next day it goes again, with that day's failures.
        store.Failed.Add(new("frn_4", "flw_b", "Welcome", "Send email: timed out.", nine.AddDays(1).AddHours(-2)));
        Assert.Equal(1, await digest.SendDigestsAsync(nine.AddDays(1), default));
        Assert.Equal("Welcome failed once in the last day", mail.Sent[^1].Subject);
    }

    [Fact]
    public async Task No_failures_no_digest()
    {
        var store = new Store();
        store.Workspaces.Add(new DigestWorkspace(Guid.NewGuid(), "Quiet", "UTC", null));
        var mail = new Mail();
        Assert.Equal(0, await Make(store, mail).SendDigestsAsync(new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc), default));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void The_digest_lists_flows_with_most_failures_first_and_their_top_errors()
    {
        var at = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        var flows = ReliabilityRules.Summarise([
            new("r1", "a", "Alpha", "x", at), new("r2", "b", "Beta", "y", at), new("r3", "b", "Beta", "y", at.AddMinutes(1)), new("r4", "b", "Beta", "z", at),
        ]);
        Assert.Equal(["b", "a"], flows.Select(f => f.FlowId));
        Assert.Equal(3, flows[0].Failures);
        Assert.Equal(("y", 2), flows[0].TopErrors[0]);
        Assert.Equal("r3", flows[0].LatestRunId);
    }

    [Fact]
    public async Task Auto_pause_switches_a_flow_off_after_n_failures_in_a_row_and_emails_the_owners_once()
    {
        var store = new Store { Flow = new AutoPauseFlow("flw_a", "Won deals", "Northwind", true, 3, DateTime.UtcNow.AddDays(-1)) };
        var mail = new Mail();
        var r = Make(store, mail);

        store.Statuses.AddRange(["failed", "failed", "succeeded"]);
        Assert.False(await r.AfterFailureAsync(Guid.NewGuid(), "flw_a", "Boom", DateTime.UtcNow));

        store.Statuses.Insert(0, "failed");
        Assert.True(await r.AfterFailureAsync(Guid.NewGuid(), "flw_a", "Create project: Vantra Projects is unavailable.", DateTime.UtcNow));
        Assert.Equal(["Paused after 3 failures in a row."], store.Paused);
        Assert.Single(mail.Sent);
        Assert.Equal("owner@example.com", mail.Sent[0].To);
        Assert.Equal("Won deals was paused after 3 failures", mail.Sent[0].Subject);

        // Already off: nothing more.
        Assert.False(await r.AfterFailureAsync(Guid.NewGuid(), "flw_a", "Boom", DateTime.UtcNow));
        Assert.Single(mail.Sent);
    }

    [Fact]
    public async Task Auto_pause_is_off_by_default()
    {
        var store = new Store { Flow = new AutoPauseFlow("flw_a", "Won deals", "Northwind", true, null, DateTime.UtcNow) };
        store.Statuses.AddRange(Enumerable.Repeat("failed", 30));
        Assert.False(await Make(store, new Mail()).AfterFailureAsync(Guid.NewGuid(), "flw_a", null, DateTime.UtcNow));
        Assert.Empty(store.Paused);
        Assert.Null(ReliabilityRules.PauseAfter(1));
        Assert.Null(ReliabilityRules.PauseAfter(99));
        Assert.Equal(5, ReliabilityRules.PauseAfter(5));
    }
}
