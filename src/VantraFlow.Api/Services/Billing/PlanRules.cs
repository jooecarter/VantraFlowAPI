using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Billing;

/// <summary>Where a workspace's plan comes from (<see cref="PlanRules.Resolve"/>).</summary>
public static class PlanSources
{
    /// <summary>db/008 hasn't run: plans aren't in force, so nothing is limited beyond the server's run quota.</summary>
    public const string Setup = "setup";
    public const string Subscription = "subscription";
    public const string Trial = "trial";
    /// <summary>On Pro until billing goes live (Stripe set up), when the trial's days start.</summary>
    public const string TrialPending = "trial_pending";
    public const string Free = "free";
}

/// <summary>The plan in force now: its tier and limits, and why.</summary>
public sealed record EffectivePlan(string Tier, string Source, TierLimits Limits, DateTime? TrialEndsAt, int? TrialDaysLeft)
{
    public bool Enforced => Source != PlanSources.Setup;
}

/// <summary>A premium app or step a definition uses: where (the trigger: <c>StepId</c> null) and what to call it.</summary>
public sealed record PremiumUse(string? StepId, string Label);

/// <summary>Why a flow can't be switched on under the plan, and the cheapest tier that would allow it.</summary>
public sealed record PlanBlock(string Code, string Message, string UpgradeTo);

public static class PlanCodes
{
    /// <summary>The plan's active-flow limit is reached.</summary>
    public const string Flows = "plan_flows";
    /// <summary>The flow uses premium apps or steps the plan doesn't include.</summary>
    public const string Premium = "plan_premium";
    /// <summary>The month's runs are used.</summary>
    public const string Runs = "plan_runs";
}

/// <summary>The plan's rules, without a database: what's in force, what a flow needs, what a downgrade switches off.</summary>
public static class PlanRules
{
    /// <summary>A subscription Stripe still bills (past due is still a subscription, settled by paying).</summary>
    public static bool IsLive(string? status) => status is "active" or "trialing" or "past_due";

    /// <summary>
    /// The plan in force at <paramref name="now"/>: a live subscription's tier; else Flow's Pro trial while it lasts
    /// (and, until billing goes live, a trial that hasn't started); else Free. <paramref name="row"/> null: plans
    /// aren't in force yet (db/008), and <paramref name="setupRuns"/> is the run quota.
    /// </summary>
    public static EffectivePlan Resolve(PlanRow? row, PlanCatalog catalog, DateTime now, int setupRuns)
    {
        if (row is null)
        {
            var pro = catalog.For(Tiers.Pro);
            return new EffectivePlan(Tiers.Pro, PlanSources.Setup, pro with { Runs = setupRuns, ActiveFlows = null, Premium = true }, null, null);
        }
        if (row.StripeSubscriptionId is not null && IsLive(row.Status) && Tiers.Paid.Contains(row.Tier))
            return new EffectivePlan(row.Tier, PlanSources.Subscription, catalog.For(row.Tier), row.TrialEndsAt, null);
        if (row.TrialEndsAt is null)
            return new EffectivePlan(Tiers.Pro, PlanSources.TrialPending, catalog.For(Tiers.Pro), null, null);
        if (row.TrialEndsAt > now)
            return new EffectivePlan(Tiers.Pro, PlanSources.Trial, catalog.For(Tiers.Pro), row.TrialEndsAt, DaysLeft(row.TrialEndsAt.Value, now));
        return new EffectivePlan(Tiers.Free, PlanSources.Free, catalog.For(Tiers.Free), row.TrialEndsAt, null);
    }

    /// <summary>Whole days left, rounded up: a trial ending in 3 hours has 1 day left.</summary>
    public static int DaysLeft(DateTime end, DateTime now) => Math.Max(0, (int)Math.Ceiling((end - now).TotalDays));

    /// <summary>The trial a new workspace gets: from now when billing is live, else not started (null).</summary>
    public static DateTime? NewTrialEnd(bool billingLive, DateTime now, PlanCatalog catalog) =>
        billingLive ? now.AddDays(catalog.TrialDays) : null;

    /// <summary>The run quota: a workspace's own (set by hand) wins, else its plan's.</summary>
    public static int RunQuota(WorkspaceRow ws, EffectivePlan plan) => ws.MonthlyRunQuota ?? plan.Limits.Runs;

    private static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["slack"] = "Slack",
        ["microsoft"] = "Microsoft 365",
        ["google"] = "Google",
        ["stripe"] = "Stripe",
        ["hubspot"] = "HubSpot",
        ["xero"] = "Xero",
        ["http"] = "HTTP requests",
        ["webhook"] = "Webhooks",
        ["flow"] = "Sub-flows",
        [StepTypes.Paths] = "Paths",
        ["approval"] = "Approvals",
        ["ai"] = "AI steps",
        ["smtp"] = "SMTP mailboxes",
    };

    public static string Label(string key) => Labels.TryGetValue(key, out var l) ? l : char.ToUpperInvariant(key[0]) + key[1..];

    /// <summary>Every premium app and step <paramref name="def"/> uses, trigger first.</summary>
    public static IReadOnlyList<PremiumUse> PremiumUses(FlowDefinition def, PlanCatalog catalog)
    {
        var uses = new List<PremiumUse>();
        if (catalog.IsPremium(def.Trigger.App, def.Trigger.Event, trigger: true)) uses.Add(new(null, Label(def.Trigger.App)));
        foreach (var s in def.AllSteps())
        {
            if (catalog.PremiumSteps.Contains(s.Type)) uses.Add(new(s.Id, Label(s.Type)));
            else if (s.Type == StepTypes.Action && catalog.IsPremium(s.App, s.Action, trigger: false)) uses.Add(new(s.Id, Label(s.App!)));
            // Send email from an own mailbox counts as that mailbox's app (Outlook as Microsoft 365, Gmail as Google, SMTP as smtp).
            else if (s.Type == StepTypes.Action && s.App == "email" && Mail.SenderRules.PremiumApp(s.Config["from"]) is { } mailbox && catalog.PremiumApps.Contains(mailbox))
                uses.Add(new(s.Id, Label(mailbox)));
        }
        return uses;
    }

    /// <summary>"Slack, HTTP requests and Paths".</summary>
    public static string Names(IEnumerable<PremiumUse> uses)
    {
        var names = uses.Select(u => u.Label).Distinct().ToList();
        return names.Count switch
        {
            0 => "",
            1 => names[0],
            _ => string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1],
        };
    }

    /// <summary>
    /// Can a flow be switched on? Its premium apps and steps need a tier that includes them; and on a tier with an
    /// active-flow limit, <paramref name="othersOn"/> (the workspace's other flows that are on) must be under it.
    /// </summary>
    public static PlanBlock? CanEnable(EffectivePlan plan, FlowDefinition def, int othersOn, PlanCatalog catalog)
    {
        if (!plan.Enforced) return null;
        if (!plan.Limits.Premium && PremiumUses(def, catalog) is { Count: > 0 } uses)
        {
            var to = catalog.Cheapest(l => l.Premium);
            return new PlanBlock(PlanCodes.Premium,
                $"{Names(uses)} {(uses.Select(u => u.Label).Distinct().Count() == 1 ? "is" : "are")} on {Tiers.Name(to)} and up. Upgrade to switch this flow on.", to);
        }
        if (plan.Limits.ActiveFlows is { } max && othersOn >= max)
        {
            var to = catalog.Cheapest(l => l.ActiveFlows is null || l.ActiveFlows > othersOn);
            return new PlanBlock(PlanCodes.Flows,
                $"{Tiers.Name(plan.Tier)} allows {max} active {(max == 1 ? "flow" : "flows")}. Switch one off, or upgrade for more.", to);
        }
        return null;
    }

    /// <summary>
    /// The flows to switch off when a workspace is on a smaller plan than its flows need. Nothing is deleted: those
    /// using premium steps the plan lacks, then — over the active-flow limit — the least recently run. Kept on: the
    /// ones that ran most recently (then the most recently edited).
    /// </summary>
    public static IReadOnlyList<ActiveFlowRow> FlowsToPause(IReadOnlyList<ActiveFlowRow> on, EffectivePlan plan, PlanCatalog catalog)
    {
        if (!plan.Enforced) return [];
        var pause = new List<ActiveFlowRow>();
        var keep = new List<ActiveFlowRow>();
        foreach (var f in on)
        {
            var def = FlowDefinition.Parse(f.Definition);
            if (!plan.Limits.Premium && def is not null && PremiumUses(def, catalog).Count > 0) pause.Add(f);
            else keep.Add(f);
        }
        if (plan.Limits.ActiveFlows is { } max && keep.Count > max)
        {
            var ordered = keep.OrderByDescending(f => f.LastRunAt ?? DateTime.MinValue).ThenByDescending(f => f.UpdatedAt).ToList();
            pause.AddRange(ordered.Skip(max));
        }
        return pause;
    }

    /// <summary>Why a paused flow is off (shown on it, and in the email).</summary>
    public static string PauseReason(ActiveFlowRow f, EffectivePlan plan, PlanCatalog catalog)
    {
        var def = FlowDefinition.Parse(f.Definition);
        if (!plan.Limits.Premium && def is not null && PremiumUses(def, catalog) is { Count: > 0 } uses)
            return $"Switched off: {Names(uses)} need{(uses.Select(u => u.Label).Distinct().Count() == 1 ? "s" : "")} {Tiers.Name(catalog.Cheapest(l => l.Premium))}.";
        return $"Switched off: {Tiers.Name(plan.Tier)} allows {plan.Limits.ActiveFlows} active flows.";
    }

    /// <summary>The usage notice a run's count crosses: 80% of the quota, or all of it; null for none.</summary>
    public static string? UsageNotice(int runsAfter, int quota)
    {
        if (quota <= 0) return null;
        if (runsAfter == quota) return PlanNotices.Usage100;
        if (runsAfter == Threshold80(quota) && runsAfter < quota) return PlanNotices.Usage80;
        return null;
    }

    /// <summary>The run that reaches 80% of the quota (rounded up, so a 100-run month warns at the 80th).</summary>
    public static int Threshold80(int quota) => (int)Math.Ceiling(quota * 0.8);

    /// <summary>When this month's runs start again: the first of next month, UTC.</summary>
    public static DateTime ResetsAt(DateTime now) => new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);

    /// <summary>
    /// The end of the workspace's own trial carried into a subscription bought during it, so subscribing early costs
    /// nothing until the trial ends; null when there's no trial left worth carrying (Stripe wants at least 48 hours).
    /// </summary>
    public static DateTime? CarriedTrialEnd(DateTime? trialEndsAt, DateTime now) =>
        trialEndsAt is { } end && end - now >= TimeSpan.FromHours(49) ? end : null;

    /// <summary>A price key's tier and billing interval (<c>pro_year</c> → pro, year); null when it isn't one.</summary>
    public static (string Tier, string Interval)? ParsePriceKey(string? key)
    {
        if (key is null || key.IndexOf('_') is var i && i <= 0) return null;
        var tier = key[..i];
        var interval = key[(i + 1)..];
        return Tiers.Paid.Contains(tier) && interval is "month" or "year" ? (tier, interval) : null;
    }
}

/// <summary>The once-a-period notices (flow.PlanNotices kinds).</summary>
public static class PlanNotices
{
    public const string Usage80 = "usage80";
    public const string Usage100 = "usage100";
    public const string PaymentFailed = "payment_failed";
    public const string Paused = "paused";
}
