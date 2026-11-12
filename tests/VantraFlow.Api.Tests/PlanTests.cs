using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Stripe;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Billing;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

public sealed class PlanRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0, DateTimeKind.Utc);
    private static readonly PlanCatalog Catalog = new();

    private static PlanRow Row(string tier = Tiers.Free, string? status = null, string? sub = null, DateTime? trialEnds = null) =>
        new(Guid.NewGuid(), tier, status, sub is null ? null : "month", trialEnds, null, false, null, sub, null, null, Now.AddDays(-30), Now.AddDays(-30));

    private static StepDef Slack(string id) => new() { Id = id, Type = StepTypes.Action, App = "slack", Action = "message.send", Config = new JsonObject() };
    private static StepDef Paths(string id, params StepDef[] inside) => new()
    {
        Id = id, Type = StepTypes.Paths, Paths = [new PathDef { Name = "Big", Steps = inside.ToList() }], Config = new JsonObject(),
    };

    [Fact]
    public void Before_db008_nothing_is_enforced_beyond_the_servers_quota()
    {
        var plan = PlanRules.Resolve(null, Catalog, Now, 5000);
        Assert.False(plan.Enforced);
        Assert.Equal(5000, plan.Limits.Runs);
        Assert.Null(PlanRules.CanEnable(plan, Build.Flow(Build.Http("s1")), 99, Catalog));
    }

    [Theory]
    [InlineData("active")]
    [InlineData("trialing")]
    [InlineData("past_due")]
    public void A_live_subscription_sets_the_tier(string status)
    {
        var plan = PlanRules.Resolve(Row(Tiers.Starter, status, "sub_1", Now.AddDays(-1)), Catalog, Now, 5000);
        Assert.Equal((Tiers.Starter, PlanSources.Subscription), (plan.Tier, plan.Source));
        Assert.Equal(2000, plan.Limits.Runs);
        Assert.Null(plan.Limits.ActiveFlows);
    }

    [Fact]
    public void An_ended_subscription_falls_back_to_the_trial_left_then_free()
    {
        Assert.Equal(PlanSources.Trial, PlanRules.Resolve(Row(Tiers.Pro, "canceled", "sub_1", Now.AddDays(2)), Catalog, Now, 0).Source);
        var free = PlanRules.Resolve(Row(Tiers.Pro, "unpaid", "sub_1", Now.AddDays(-2)), Catalog, Now, 0);
        Assert.Equal((Tiers.Free, PlanSources.Free, 100, (int?)3), (free.Tier, free.Source, free.Limits.Runs, free.Limits.ActiveFlows));
    }

    [Fact]
    public void A_trial_not_started_is_pro_and_a_running_one_counts_its_days()
    {
        var pending = PlanRules.Resolve(Row(trialEnds: null), Catalog, Now, 0);
        Assert.Equal((Tiers.Pro, PlanSources.TrialPending), (pending.Tier, pending.Source));
        var trial = PlanRules.Resolve(Row(trialEnds: Now.AddDays(8).AddHours(3)), Catalog, Now, 0);
        Assert.Equal((Tiers.Pro, PlanSources.Trial, 9), (trial.Tier, trial.Source, trial.TrialDaysLeft));
        Assert.Equal(1, PlanRules.Resolve(Row(trialEnds: Now.AddHours(3)), Catalog, Now, 0).TrialDaysLeft);
    }

    [Fact]
    public void New_workspaces_start_the_trial_only_once_billing_is_live()
    {
        Assert.Null(PlanRules.NewTrialEnd(false, Now, Catalog));
        Assert.Equal(Now.AddDays(14), PlanRules.NewTrialEnd(true, Now, Catalog));
    }

    [Fact]
    public void Premium_uses_cover_outside_apps_http_webhooks_sub_flows_and_paths_anywhere_in_the_flow()
    {
        Assert.Empty(PlanRules.PremiumUses(Build.Flow(Build.Email("s1"), Build.Delay("s2", 1)), Catalog));
        var def = Build.Flow(Build.Branch("b1", "x", "eq", "y", [Slack("s2")], [Build.Email("s3")]), Paths("p1", Build.Http("s4")));
        var uses = PlanRules.PremiumUses(def, Catalog);
        Assert.Equal(["s2", "p1", "s4"], uses.Select(u => u.StepId));
        Assert.Equal("Slack, Paths and HTTP requests", PlanRules.Names(uses));

        var hook = new FlowDefinition { Trigger = new TriggerDef { App = "webhook", Event = "catch" }, Steps = [Build.Email("s1")] };
        Assert.Equal("Webhooks", Assert.Single(PlanRules.PremiumUses(hook, Catalog)).Label);
        var sub = Build.Flow(new StepDef { Id = "s1", Type = StepTypes.Action, App = "flow", Action = "run", Config = new JsonObject() });
        Assert.Equal("Sub-flows", Assert.Single(PlanRules.PremiumUses(sub, Catalog)).Label);
    }

    [Fact]
    public void Free_refuses_a_fourth_active_flow_and_premium_steps_naming_the_tier_that_has_them()
    {
        var free = PlanRules.Resolve(Row(trialEnds: Now.AddDays(-1)), Catalog, Now, 0);
        Assert.Null(PlanRules.CanEnable(free, Build.Flow(Build.Email("s1")), 2, Catalog));
        var flows = PlanRules.CanEnable(free, Build.Flow(Build.Email("s1")), 3, Catalog);
        Assert.Equal((PlanCodes.Flows, Tiers.Starter), (flows!.Code, flows.UpgradeTo));
        Assert.Contains("3 active flows", flows.Message);
        var premium = PlanRules.CanEnable(free, Build.Flow(Build.Http("s1")), 0, Catalog);
        Assert.Equal((PlanCodes.Premium, Tiers.Pro), (premium!.Code, premium.UpgradeTo));
        Assert.Equal("HTTP requests is on Pro and up. Upgrade to switch this flow on.", premium.Message);
    }

    [Fact]
    public void Starter_has_no_flow_limit_but_no_premium_and_pro_has_both()
    {
        var starter = PlanRules.Resolve(Row(Tiers.Starter, "active", "sub_1"), Catalog, Now, 0);
        Assert.Null(PlanRules.CanEnable(starter, Build.Flow(Build.Email("s1")), 500, Catalog));
        Assert.Equal(PlanCodes.Premium, PlanRules.CanEnable(starter, Build.Flow(Slack("s1")), 0, Catalog)!.Code);
        var pro = PlanRules.Resolve(Row(Tiers.Pro, "active", "sub_1"), Catalog, Now, 0);
        Assert.Null(PlanRules.CanEnable(pro, Build.Flow(Slack("s1"), Paths("p1")), 500, Catalog));
    }

    [Fact]
    public void A_downgrade_switches_off_premium_flows_first_then_the_least_recently_run_and_deletes_nothing()
    {
        var free = PlanRules.Resolve(Row(trialEnds: Now.AddDays(-1)), Catalog, Now, 0);
        ActiveFlowRow F(string id, FlowDefinition def, int ranHoursAgo) => new(id, id, def.ToJson(), Now.AddHours(-ranHoursAgo), Now.AddDays(-5));
        var on = new List<ActiveFlowRow>
        {
            F("slack", Build.Flow(Slack("s1")), 1),
            F("a", Build.Flow(Build.Email("s1")), 2),
            F("b", Build.Flow(Build.Email("s1")), 50),
            F("c", Build.Flow(Build.Email("s1")), 3),
            F("d", Build.Flow(Build.Email("s1")), 4),
        };
        var pause = PlanRules.FlowsToPause(on, free, Catalog).Select(f => f.Id).ToList();
        Assert.Equal(["slack", "b"], pause);
        Assert.StartsWith("Switched off: Slack needs Pro", PlanRules.PauseReason(on[0], free, Catalog));
        Assert.Equal("Switched off: Free allows 3 active flows.", PlanRules.PauseReason(on[2], free, Catalog));

        var pro = PlanRules.Resolve(Row(Tiers.Pro, "active", "sub_1"), Catalog, Now, 0);
        Assert.Empty(PlanRules.FlowsToPause(on, pro, Catalog));
    }

    [Theory]
    [InlineData(79, 100, null)]
    [InlineData(80, 100, PlanNotices.Usage80)]
    [InlineData(81, 100, null)]
    [InlineData(100, 100, PlanNotices.Usage100)]
    [InlineData(1600, 2000, PlanNotices.Usage80)]
    [InlineData(1, 1, PlanNotices.Usage100)]
    public void Usage_notices_fire_on_the_run_that_crosses_80_and_100_percent(int after, int quota, string? expected) =>
        Assert.Equal(expected, PlanRules.UsageNotice(after, quota));

    [Fact]
    public void A_trial_carries_into_checkout_only_with_two_days_left_and_runs_reset_on_the_first()
    {
        Assert.Null(PlanRules.CarriedTrialEnd(Now.AddHours(30), Now));
        Assert.Equal(Now.AddDays(5), PlanRules.CarriedTrialEnd(Now.AddDays(5), Now));
        Assert.Null(PlanRules.CarriedTrialEnd(null, Now));
        Assert.Equal(new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc), PlanRules.ResetsAt(Now));
    }

    [Theory]
    [InlineData("pro_month", "pro", "month")]
    [InlineData("business_year", "business", "year")]
    [InlineData("free_month", null, null)]
    [InlineData("pro_week", null, null)]
    [InlineData("client_month", null, null)]
    [InlineData("", null, null)]
    public void Price_keys_name_a_paid_tier_and_an_interval(string key, string? tier, string? interval)
    {
        var parsed = PlanRules.ParsePriceKey(key);
        Assert.Equal(tier, parsed?.Tier);
        Assert.Equal(interval, parsed?.Interval);
    }

    [Fact]
    public void The_numbers_come_from_config()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Billing:TrialDays"] = "30",
            ["Billing:Tiers:free:Runs"] = "250",
            ["Billing:Tiers:free:ActiveFlows"] = "0",
            ["Billing:Tiers:starter:ActiveFlows"] = "10",
            ["Billing:PremiumApps"] = "http",
        }).Build();
        var c = PlanCatalog.From(config);
        Assert.Equal(30, c.TrialDays);
        Assert.Equal((250, (int?)null), (c.For(Tiers.Free).Runs, c.For(Tiers.Free).ActiveFlows));
        Assert.Equal(10, c.For(Tiers.Starter).ActiveFlows);
        Assert.Equal(10_000, c.For(Tiers.Pro).Runs);
        Assert.True(c.IsPremium("http", "request", false));
        Assert.False(c.IsPremium("slack", "message.send", false));
        Assert.Equal(Tiers.Free, c.For("nonsense").Tier);
    }
}

public sealed class PlanEngineTests
{
    /// <summary>A store whose plan skips every run, as the SQL one does for premium steps on Free.</summary>
    private sealed class PlanStore(string? refusal) : IEngineStore
    {
        public readonly FakeStore Inner = new();
        public Task<IReadOnlyList<FlowRow>> CandidatesAsync(FlowEventRow ev) => Inner.CandidatesAsync(ev);
        public Task<WorkspaceRow?> WorkspaceAsync(Guid workspaceId) => Inner.WorkspaceAsync(workspaceId);
        public Task<bool> ClaimKeyAsync(Guid workspaceId, string flowId, string key) => Inner.ClaimKeyAsync(workspaceId, flowId, key);
        public Task<bool> CountRunAsync(Guid workspaceId, DateTime now, int quota) => Inner.CountRunAsync(workspaceId, now, quota);
        public Task<int> RunsSinceAsync(Guid workspaceId, string? flowId, DateTime since) => Inner.RunsSinceAsync(workspaceId, flowId, since);
        public Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason) => Inner.PauseAsync(workspaceId, flowId, reason);
        public Task InsertRunAsync(RunRow run) => Inner.InsertRunAsync(run);
        public Task<string?> AdmitAsync(WorkspaceRow ws, FlowDefinition def, DateTime now, int quota) => Task.FromResult(refusal);
    }

    private static FlowRow Flow(FlowDefinition def) =>
        new("flw_1", Guid.NewGuid(), "Post to Slack", null, true, def.ToJson(), 1, def.Trigger.App, def.Trigger.Event, "tbl_leads", null, null, null, null,
            "u", DateTime.UtcNow, "u", DateTime.UtcNow, null, null);

    private static FlowEventRow Event() =>
        new(1, "fev_1", Guid.NewGuid(), "orbit", "record.created", "tbl_leads", null, Build.Trigger().ToJsonString(), false, false, null, DateTime.UtcNow, 1);

    [Fact]
    public async Task The_plans_refusal_is_logged_on_a_skipped_run()
    {
        var store = new PlanStore("Skipped: Slack is on Pro and up.");
        store.Inner.Flows.Add(Flow(Build.Flow(Build.Email("s1"))));
        await new EventProcessor(store, new FlowLimits()).ProcessAsync(Event());
        var run = Assert.Single(store.Inner.Runs);
        Assert.Equal(RunStatuses.Skipped, run.Status);
        Assert.Equal("Skipped: Slack is on Pro and up.", run.Error);
    }

    [Fact]
    public async Task An_admitted_run_is_queued()
    {
        var store = new PlanStore(null);
        store.Inner.Flows.Add(Flow(Build.Flow(Build.Email("s1"))));
        await new EventProcessor(store, new FlowLimits()).ProcessAsync(Event());
        Assert.Equal(RunStatuses.Queued, Assert.Single(store.Inner.Runs).Status);
    }
}

public sealed class StripeBillingTests
{
    private static StripeBilling Stripe(params (string Key, string Id)[] prices) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(
            prices.Select(p => new KeyValuePair<string, string?>($"Stripe:Prices:{p.Key}", p.Id)).Append(new("Stripe:SecretKey", "sk_test_x"))).Build());

    private static Subscription Sub(string priceId, Dictionary<string, string>? meta = null) => new()
    {
        Id = "sub_1", CustomerId = "cus_1", Status = "active", Metadata = meta ?? [], Currency = "gbp",
        Items = new StripeList<SubscriptionItem> { Data = [new SubscriptionItem { Price = new Price { Id = priceId } }] },
    };

    [Fact]
    public void Prices_map_to_tiers_and_a_price_under_two_keys_sells_neither()
    {
        var s = Stripe(("pro_month", "price_pro_m"), ("pro_year", "price_pro_y"), ("starter_month", "price_dup"), ("business_month", "price_dup"));
        Assert.Equal(("pro", "year"), s.ForPrice("price_pro_y"));
        Assert.Null(s.PriceIdFor("starter_month"));
        Assert.Null(s.PriceIdFor("business_month"));
        Assert.Equal(["pro_month", "pro_year"], s.AvailableKeys);
        Assert.True(s.Enabled);
    }

    [Fact]
    public void Only_flows_subscriptions_are_read_and_others_on_a_shared_account_are_ignored()
    {
        var s = Stripe(("starter_month", "price_starter"));
        var ws = Guid.NewGuid();
        var mine = s.Read(Sub("price_starter", StripeBilling.Stamp(ws)));
        Assert.Equal((ws, "starter", "month", "active"), (mine!.WorkspaceId, mine.Tier, mine.Interval, mine.Status));
        Assert.NotNull(s.Read(Sub("price_starter")));                    // on Flow's price, unstamped
        Assert.Null(s.Read(Sub("price_projects_pro", new() { ["workspaceId"] = ws.ToString() }))); // Projects'
    }

    [Fact]
    public void Without_a_secret_key_billing_is_off() =>
        Assert.False(new StripeBilling(new ConfigurationBuilder().Build()).Enabled);
}

public sealed class PlanMailTests
{
    [Fact]
    public void The_emails_escape_names_and_link_to_plan_and_billing()
    {
        var ws = Guid.NewGuid();
        var (subject, html) = PlanMail.Usage(PlanNotices.Usage100, "<Acme>", 100, 100, "Free", new DateTime(2026, 12, 1), "https://flow.example.com", ws);
        Assert.Equal("<Acme> has used its runs for the month", subject);
        Assert.Contains("&lt;Acme&gt;", html);
        Assert.DoesNotContain("<Acme>", html);
        Assert.Contains($"https://flow.example.com/settings?tab=billing&amp;ws={ws:D}", html);
        Assert.Contains("1 December", html);
        var paused = PlanMail.Paused("Acme", [("Post to Slack", "Switched off: Slack needs Pro.")], "Free", "https://flow.example.com", ws);
        Assert.Equal("Post to Slack was switched off", paused.Subject);
    }
}

public sealed class PlansMigrationTests
{
    private static string Sql => System.IO.File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "008_plans.sql"));

    [Fact]
    public void The_plans_migration_parses_is_azure_ready_and_makes_the_probes_index_last()
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
        Assert.DoesNotMatch(new Regex(@"\bCREATE\s+(UNIQUE\s+)?(INDEX|TABLE)\b", RegexOptions.IgnoreCase), outside);
        Assert.DoesNotMatch(new Regex(@"\bINSERT\s+INTO\b", RegexOptions.IgnoreCase), outside);
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_WorkspacePlans_Customer", last);
        Assert.Contains(last, VantraFlow.Api.Data.FlowSchema.PlansProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.PlansProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }
}
