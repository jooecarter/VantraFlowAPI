using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.OAuth;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>An approval gate that keeps requests in memory; a test decides them.</summary>
internal sealed class FakeApprovals : IApprovalGate
{
    public readonly Dictionary<string, ApprovalState> Requests = new();
    public readonly List<ApprovalAsk> Asked = [];
    public readonly List<string> Expired = [];

    public Task<ApprovalState> RequestAsync(ApprovalAsk ask, CancellationToken ct)
    {
        Asked.Add(ask);
        var id = $"apr_{ask.RunId}_{ask.StepId}";
        if (!Requests.TryGetValue(id, out var st))
            Requests[id] = st = new ApprovalState(id, "pending", null, null, null, null, ask.Approvers.Select(a => $"Person {a}").ToList(), ask.ExpiresAt, ask.Approvers.Count);
        return Task.FromResult(st);
    }

    public Task<ApprovalState?> StateAsync(Guid workspaceId, string approvalId, CancellationToken ct) => Task.FromResult(Requests.GetValueOrDefault(approvalId));

    public Task ExpireAsync(string approvalId, DateTime now)
    {
        Expired.Add(approvalId);
        Requests[approvalId] = Requests[approvalId] with { Status = "expired" };
        return Task.CompletedTask;
    }

    public void Decide(string decision, string by = "Priya Shah", string? comment = null)
    {
        var id = Requests.Keys.Single();
        Requests[id] = Requests[id] with { Status = decision, DecidedByName = by, DecidedAt = DateTime.UtcNow, Comment = comment, Via = "email" };
    }
}

internal static class NewBuild
{
    public static StepDef Until(string id, string at, string? zone = null)
    {
        var c = new JsonObject { ["mode"] = "until", ["at"] = at };
        if (zone is not null) c["zone"] = zone;
        return new StepDef { Id = id, Type = StepTypes.Delay, Config = c };
    }

    public static JsonObject Rule(string left, string op, string? right) => new()
    {
        ["match"] = "all", ["rules"] = new JsonArray(new JsonObject { ["left"] = left, ["op"] = op, ["right"] = right }),
    };

    public static StepDef Wait(string id, JsonObject condition, string? refresh = null, string onTimeout = "continue", double days = 1) => new()
    {
        Id = id, Type = StepTypes.Wait,
        Config = new JsonObject
        {
            ["condition"] = condition, ["every"] = 30, ["amount"] = days, ["unit"] = "days", ["onTimeout"] = onTimeout, ["refresh"] = refresh,
        },
    };

    public static StepDef Approval(string id, List<StepDef>? approved = null, List<StepDef>? rejected = null, List<StepDef>? timedOut = null, params string[] approvers) => new()
    {
        Id = id, Type = StepTypes.Approval,
        Config = new JsonObject
        {
            ["title"] = "Refund {{trigger.record.fields.Name}}?", ["details"] = "Deal value {{trigger.record.fields.Deal value}}",
            ["approvers"] = new JsonArray((approvers.Length > 0 ? approvers : ["u_priya"]).Select(a => (JsonNode)a).ToArray()), ["amount"] = 2, ["unit"] = "days",
        },
        Paths =
        [
            new PathDef { Name = "Approved", Steps = approved ?? [] },
            new PathDef { Name = "Rejected", Steps = rejected ?? [] },
            new PathDef { Name = "Timed out", Steps = timedOut ?? [] },
        ],
    };
}

public sealed class DelayUntilTests
{
    private static readonly FlowLimits Limits = new();
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_delay_until_a_time_waits_for_it_in_the_given_zone_then_carries_on()
    {
        var def = Build.Flow(NewBuild.Until("d1", "2026-11-12 09:30", "America/New_York"), Build.Http("s2"));
        var run = Build.Run(def);
        var clock = Now;
        var exec = new RunExecutor(new FakeActions(), Limits, () => clock);

        var first = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status);
        Assert.Equal(new DateTime(2026, 11, 12, 14, 30, 0, DateTimeKind.Utc), run.NotBefore);
        Assert.Equal("waiting", first.Single().Status);

        clock = run.NotBefore;
        var second = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["d1", "s2"], second.Select(l => l.StepId));
    }

    [Fact]
    public async Task A_time_already_past_carries_straight_on()
    {
        var def = Build.Flow(NewBuild.Until("d1", "2026-11-01T08:00:00Z"), Build.Http("s2"));
        var run = Build.Run(def);
        var logs = await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("already passed", logs[0].Error);
    }

    [Fact]
    public async Task A_time_from_a_token_that_isnt_a_date_fails_the_run()
    {
        var def = Build.Flow(NewBuild.Until("d1", "{{trigger.record.fields.Name}}"), Build.Http("s2"));
        var run = Build.Run(def);
        await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("isn't a date", run.Error);
    }

    [Fact]
    public async Task More_than_thirty_days_ahead_is_refused()
    {
        var run = Build.Run(Build.Flow(NewBuild.Until("d1", "2027-03-01 09:00")));
        await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("30 days", run.Error);
    }

    [Fact]
    public void Validation_checks_the_time_and_the_zone()
    {
        Assert.Contains(DefinitionRules.Validate(Build.Flow(NewBuild.Until("d1", "")), Limits), e => e.Message == "Choose when to carry on.");
        Assert.Contains(DefinitionRules.Validate(Build.Flow(NewBuild.Until("d1", "soon")), Limits), e => e.Message.Contains("isn't a date"));
        Assert.Contains(DefinitionRules.Validate(Build.Flow(NewBuild.Until("d1", "2026-12-01 09:00", "Mars/Olympus")), Limits), e => e.Message.Contains("time zone"));
        Assert.Empty(DefinitionRules.Validate(Build.Flow(NewBuild.Until("d1", "{{trigger.record.fields.Due}}", "Europe/London")), Limits));
    }
}

public sealed class WaitUntilTests
{
    private static readonly FlowLimits Limits = new();
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_condition_that_already_holds_carries_on_at_once()
    {
        var def = Build.Flow(NewBuild.Wait("w1", NewBuild.Rule("{{trigger.record.fields.Stage}}", "equals", "Won")), Build.Http("s2"));
        var run = Build.Run(def);
        var logs = await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.True(run.Steps["w1"]!["met"]!.GetValue<bool>());
        Assert.Equal(["w1", "s2"], logs.Select(l => l.StepId));
    }

    [Fact]
    public async Task It_re_runs_the_lookup_each_check_until_the_condition_holds()
    {
        var find = Build.Http("f1");
        var def = Build.Flow(find, NewBuild.Wait("w1", NewBuild.Rule("{{steps.f1.status}}", "equals", "paid"), refresh: "f1"), Build.Http("s3"));
        var actions = new FakeActions().On("f1",
            StepOutcome.Done(new JsonObject { ["status"] = "open" }),
            StepOutcome.Done(new JsonObject { ["status"] = "open" }),
            StepOutcome.Done(new JsonObject { ["status"] = "paid" }));
        var run = Build.Run(def);
        var clock = Now;
        var exec = new RunExecutor(actions, Limits, () => clock);

        var first = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status);
        Assert.Equal(Now.AddMinutes(30), run.NotBefore);
        Assert.Equal(["f1", "w1"], first.Select(l => l.StepId));
        Assert.Equal("waiting", first[1].Status);

        clock = run.NotBefore;
        var second = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["w1", "s3"], second.Select(l => l.StepId));
        Assert.Equal(2, run.Steps["w1"]!["checks"]!.GetValue<int>());
        Assert.Equal(3, actions.Calls.Count(c => c.Step.Id == "f1"));
    }

    [Theory]
    [InlineData("continue", RunStatuses.Succeeded, true)]
    [InlineData("stop", RunStatuses.Succeeded, false)]
    public async Task Past_its_deadline_it_carries_on_or_stops(string onTimeout, string status, bool after)
    {
        var def = Build.Flow(NewBuild.Wait("w1", NewBuild.Rule("{{trigger.record.fields.Stage}}", "equals", "Lost"), onTimeout: onTimeout, days: 1), Build.Http("s2"));
        var run = Build.Run(def);
        var clock = Now;
        var exec = new RunExecutor(new FakeActions(), Limits, () => clock);
        await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status);

        clock = Now.AddDays(1).AddMinutes(1);
        var logs = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(status, run.Status);
        Assert.True(run.Steps["w1"]!["timedOut"]!.GetValue<bool>());
        Assert.Equal(after, logs.Any(l => l.StepId == "s2"));
    }

    [Fact]
    public async Task A_test_checks_once_and_doesnt_wait()
    {
        var def = Build.Flow(NewBuild.Wait("w1", NewBuild.Rule("{{trigger.record.fields.Stage}}", "equals", "Lost")), Build.Http("s2"));
        var run = Build.Run(def, test: true);
        var logs = await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Contains("A test checks once", logs[0].Error);
    }

    [Fact]
    public void Validation_wants_a_condition_and_an_earlier_step_to_run_again()
    {
        var bad = Build.Flow(NewBuild.Wait("w1", new JsonObject(), refresh: "s9"), Build.Http("s9"));
        var errors = DefinitionRules.Validate(bad, Limits);
        Assert.Contains(errors, e => e.Message == "Add a condition to wait for.");
        Assert.Contains(errors, e => e.Message.Contains("must come before"));
        var tooLong = NewBuild.Wait("w1", NewBuild.Rule("a", "equals", "b"), days: 45);
        Assert.Contains(DefinitionRules.Validate(Build.Flow(tooLong), Limits), e => e.Message.Contains("30 days"));
    }
}

public sealed class ApprovalStepTests
{
    private static readonly FlowLimits Limits = new();
    private static readonly DateTime Now = new(2026, 11, 10, 9, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("approved", "s_yes")]
    [InlineData("rejected", "s_no")]
    public async Task It_asks_waits_and_goes_down_the_decisions_path(string decision, string expected)
    {
        var def = Build.Flow(NewBuild.Approval("a1", [Build.Http("s_yes")], [Build.Http("s_no")], [Build.Http("s_late")], "u_priya", "u_tom"), Build.Http("s_after"));
        var gate = new FakeApprovals();
        var run = Build.Run(def);
        var clock = Now;
        var exec = new RunExecutor(new FakeActions(), Limits, () => clock, gate);

        var first = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status);
        Assert.Equal("waiting", first.Single().Status);
        var ask = gate.Asked.Single();
        Assert.Equal("Refund Ada?", ask.Title);
        Assert.Equal("Deal value 1200", ask.Details);
        Assert.Equal(["u_priya", "u_tom"], ask.Approvers);
        Assert.Equal(Now.AddDays(2), ask.ExpiresAt);
        // It looks again at least hourly, in case the decision's wake-up is missed.
        Assert.Equal(Now.Add(RunExecutor.ApprovalRecheck), run.NotBefore);

        clock = Now.AddMinutes(30);
        await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Waiting, run.Status); // nobody has decided yet

        gate.Decide(decision, comment: "Fine by me");
        clock = Now.AddMinutes(40);
        var logs = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["a1", expected, "s_after"], logs.Select(l => l.StepId));
        Assert.Equal(decision, run.Steps["a1"]!["decision"]!.GetValue<string>());
        Assert.Equal("Priya Shah", run.Steps["a1"]!["decidedBy"]!.GetValue<string>());
        Assert.Equal("Fine by me", run.Steps["a1"]!["comment"]!.GetValue<string>());
        Assert.Single(gate.Asked); // asked once
    }

    [Fact]
    public async Task No_decision_in_time_takes_the_timed_out_path()
    {
        var def = Build.Flow(NewBuild.Approval("a1", [Build.Http("s_yes")], [], [Build.Http("s_late")]));
        var gate = new FakeApprovals();
        var run = Build.Run(def);
        var clock = Now;
        var exec = new RunExecutor(new FakeActions(), Limits, () => clock, gate);
        await exec.ExecuteAsync(run, CancellationToken.None);

        clock = Now.AddDays(2).AddMinutes(1);
        var logs = await exec.ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["a1", "s_late"], logs.Select(l => l.StepId));
        Assert.Equal("timed_out", run.Steps["a1"]!["decision"]!.GetValue<string>());
        Assert.Single(gate.Expired);
    }

    [Fact]
    public async Task A_test_takes_approved_without_asking()
    {
        var def = Build.Flow(NewBuild.Approval("a1", [Build.Http("s_yes")], [Build.Http("s_no")]));
        var gate = new FakeApprovals();
        var run = Build.Run(def, test: true);
        var logs = await new RunExecutor(new FakeActions(), Limits, () => Now, gate).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Succeeded, run.Status);
        Assert.Equal(["a1", "s_yes"], logs.Select(l => l.StepId));
        Assert.Empty(gate.Asked);
    }

    [Fact]
    public async Task Without_a_gate_or_approvers_it_fails_with_why()
    {
        var run = Build.Run(Build.Flow(NewBuild.Approval("a1")));
        await new RunExecutor(new FakeActions(), Limits, () => Now).ExecuteAsync(run, CancellationToken.None);
        Assert.Equal(RunStatuses.Failed, run.Status);
        Assert.Contains("aren't available", run.Error);
    }

    [Fact]
    public void Validation_wants_a_title_approvers_and_no_loop_around_it()
    {
        var step = NewBuild.Approval("a1");
        step.Config["approvers"] = new JsonArray();
        step.Config["title"] = " ";
        var errors = DefinitionRules.Validate(Build.Flow(step), Limits);
        Assert.Contains(errors, e => e.Message == "Choose who can approve.");
        Assert.Contains(errors, e => e.Message == "Say what needs approving.");

        var loop = new StepDef { Id = "l1", Type = StepTypes.Loop, Config = new JsonObject { ["items"] = "{{trigger.record.fields.Name}}" }, Do = [NewBuild.Approval("a2")] };
        Assert.Contains(DefinitionRules.Validate(Build.Flow(loop), Limits), e => e.Message.Contains("can't be inside a Repeat for each"));

        // Its paths' steps can use its decision; steps after it too.
        var ok = Build.Flow(NewBuild.Approval("a1", [Build.Http("s_yes")]), Build.Http("s_after"));
        ok.Steps[0].Paths![0].Steps[0].Config["body"] = "{{steps.a1.decision}}";
        ok.Steps[1].Config["body"] = "{{steps.a1.decidedBy}}";
        Assert.Empty(DefinitionRules.Validate(ok, Limits));
    }

    private static FlowSecrets Secrets() => new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 48) }).Build());

    [Fact]
    public void Email_links_are_sealed_expire_and_cant_be_altered()
    {
        var secrets = Secrets();
        var token = ApprovalLinks.Make(secrets, "apr_1", "u_priya", "approved", Now.AddDays(1));
        Assert.Equal(("apr_1", "u_priya", "approved"), ApprovalLinks.Read(secrets, token, Now));
        Assert.Null(ApprovalLinks.Read(secrets, token, Now.AddDays(2)));
        var tampered = token[..^3] + (token[^3] == 'A' ? "B" : "A") + token[^2..];
        Assert.Null(ApprovalLinks.Read(secrets, tampered, Now));
        var other = new FlowSecrets(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('z', 48) }).Build());
        Assert.Null(ApprovalLinks.Read(other, token, Now));
        Assert.Null(ApprovalLinks.Read(secrets, "not-a-token", Now));
    }

    [Fact]
    public void The_email_escapes_what_the_flow_wrote()
    {
        var (subject, html) = ApprovalMail.Compose("Refunds", "Northwind", "Refund <b>Ada</b>", "Line one\nLine <two>", "https://example.com/a?x=1&y=2",
            "https://example.com/r", "https://example.com/inbox", Now);
        Assert.Equal("Approval needed: Refund <b>Ada</b>", subject);
        Assert.Contains("Refund &lt;b&gt;Ada&lt;/b&gt;", html);
        Assert.Contains("Line one<br>Line &lt;two&gt;", html);
        Assert.Contains("x=1&amp;y=2", html);
    }
}

public sealed class NewConnectorTests
{
    [Fact]
    public void Query_parameters_are_added_and_escaped()
    {
        var uri = StepActions.WithQuery(new Uri("https://example.com/api?page=1"), new JsonObject { ["q"] = "a b&c", [" "] = "skip" });
        Assert.Equal("https://example.com/api?page=1&q=a%20b%26c", uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("12.50", "gbp", 1250L)]
    [InlineData("1,200", "usd", 120000L)]
    [InlineData("500", "jpy", 500L)]
    public void Stripe_amounts_go_in_the_smallest_unit(string amount, string currency, long expected) =>
        Assert.Equal(expected, BusinessActions.MinorUnits(amount, currency));

    [Fact]
    public void Stripe_takes_secret_and_restricted_keys_only()
    {
        Assert.Null(StripeProvider.CheckKey("rk_live_51AbCdEfGhIjKlMnOp"));
        Assert.Null(StripeProvider.CheckKey("sk_test_51AbCdEfGhIjKlMnOp"));
        Assert.Contains("publishable", StripeProvider.CheckKey("pk_live_51AbCdEfGhIjKlMnOp"));
        Assert.NotNull(StripeProvider.CheckKey("whatever"));
        Assert.Null(BusinessActions.MinorUnits("-3", "gbp"));
    }

    [Fact]
    public void Stripe_payments_read_as_flat_fields_in_major_units()
    {
        var charge = JsonNode.Parse("""{"id":"ch_1","amount":129900,"currency":"gbp","customer":"cus_9","billing_details":{"email":"ada@example.com","name":"Ada"},"created":1794387600,"livemode":true,"payment_intent":"pi_7"}""");
        var o = BusinessActions.StripePayment(charge);
        Assert.Equal(1299m, o["amount"]!.GetValue<decimal>());
        Assert.Equal("ada@example.com", o["email"]!.GetValue<string>());
        Assert.Equal("https://dashboard.stripe.com/payments/pi_7", o["link"]!.GetValue<string>());
        Assert.Equal("charge.succeeded", BusinessPolls.StripeEventType("payment.new"));
        Assert.Equal("invoice.paid", BusinessPolls.StripeEventType("invoice.paid"));
    }

    [Fact]
    public void Xero_dates_and_hubspot_records_read_cleanly()
    {
        Assert.Equal("2019-11-14T18:10:38Z", BusinessActions.XeroDate(JsonValue.Create("/Date(1573755038314+0000)/")));
        var c = BusinessActions.HubSpotContact(JsonNode.Parse("""{"id":"51","properties":{"email":"ada@example.com","firstname":"Ada","lastname":"Lovelace"}}"""), "123");
        Assert.Equal("Ada Lovelace", c["name"]!.GetValue<string>());
        Assert.Equal("https://app.hubspot.com/contacts/123/record/0-1/51", c["link"]!.GetValue<string>());
        Assert.Equal(BusinessPolls.Short("abc"), BusinessPolls.Short("abc"));
        Assert.Equal(10, BusinessPolls.Short("abc").Length);
    }

    [Fact]
    public void A_webhook_takes_json_or_a_form()
    {
        var form = IncomingController.HookBody("name=Ada+Lovelace&tag=a&tag=b"u8.ToArray(), "application/x-www-form-urlencoded; charset=utf-8") as JsonObject;
        Assert.Equal("Ada Lovelace", form!["name"]!.GetValue<string>());
        Assert.Equal(2, (form["tag"] as JsonArray)!.Count);
        Assert.Equal(7, IncomingController.HookBody("""{"n":7}"""u8.ToArray(), "application/json")!["n"]!.GetValue<int>());
        Assert.Empty((JsonObject)IncomingController.HookBody([], null)!);
        Assert.Null(IncomingController.HookBody("not json"u8.ToArray(), "text/plain"));
    }

    [Fact]
    public void A_webhooks_headers_never_include_credentials()
    {
        var h = new Microsoft.AspNetCore.Http.HeaderDictionary
        {
            ["Authorization"] = "Bearer x", ["X-Flow-Signature"] = "sha256=..", ["Cookie"] = "a=b", ["X-Api-Key"] = "k", ["X-Request-Id"] = "r1",
            ["User-Agent"] = "Example/1.0", ["X-Forwarded-For"] = "10.0.0.1", ["X-Auth-Token"] = "t", ["Accept"] = "*/*",
        };
        var safe = IncomingController.SafeHeaders(h);
        Assert.Equal(["x-request-id", "user-agent"], safe.Select(kv => kv.Key).OrderByDescending(k => k.StartsWith('x')).ToList());
    }

    [Fact]
    public void A_monthly_schedule_fires_on_its_day_or_the_months_last()
    {
        static TriggerDef T(string day) => new() { App = "schedule", Event = "monthly", Config = new JsonObject { ["day"] = day, ["time"] = "09:00" } };
        Assert.True(ScheduleRules.Matches(T("1"), new DateTime(2026, 12, 1, 9, 0, 0)));
        Assert.False(ScheduleRules.Matches(T("1"), new DateTime(2026, 12, 2, 9, 0, 0)));
        Assert.True(ScheduleRules.Matches(T("31"), new DateTime(2026, 6, 30, 9, 0, 0)));
        Assert.True(ScheduleRules.Matches(T("last"), new DateTime(2027, 2, 28, 9, 0, 0)));
        Assert.False(ScheduleRules.Matches(T("last"), new DateTime(2027, 2, 27, 9, 0, 0)));
        Assert.Equal(new DateTime(2026, 12, 1, 9, 0, 0, DateTimeKind.Utc), ScheduleRules.Next(T("1"), "UTC", new DateTime(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc)));
        Assert.NotEmpty(ScheduleRules.Validate(new TriggerDef { App = "schedule", Event = "monthly", Config = new JsonObject { ["time"] = "09:00", ["day"] = "40" } }));
    }

    [Fact]
    public void New_catalog_entries_are_described_and_the_new_apps_are_marked_premium()
    {
        foreach (var e in FlowCatalog.Triggers.Concat(FlowCatalog.Actions).Where(e => e.App is "stripe" or "hubspot" or "xero"))
        {
            Assert.True(e.Premium, $"{e.App} {e.Key}");
            Assert.False(string.IsNullOrWhiteSpace(e.Description), $"{e.App} {e.Key}");
        }
        Assert.Contains(FlowCatalog.Steps, s => s.Type == StepTypes.Approval && s.Premium);
        Assert.True(OAuthProviders.IsProvider("xero"));
        Assert.True(OAuthProviders.NeedsSteps("stripe"));
        Assert.False(OAuthProviders.NeedsSteps("slack"));
    }
}

public sealed class StepsMigrationTests
{
    private static string Sql => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "007_steps_apps.sql"));

    [Fact]
    public void The_migration_is_one_azure_ready_batch_with_its_probe_index_last()
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
        Assert.DoesNotMatch(new Regex(@"\b(CREATE\s+(UNIQUE\s+)?INDEX|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase), outside);
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_Approvals_Inbox", last);
        Assert.Contains("IX_Approvals_Inbox", VantraFlow.Api.Data.FlowSchema.StepsProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.StepsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }
}
