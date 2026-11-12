using System.Collections.Concurrent;
using Stripe;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Billing;

/// <summary>Why a run doesn't start (<see cref="PlanCodes"/>), and the cheapest tier that would let it.</summary>
public sealed record RunRefusal(string Code, string Message, string UpgradeTo);

/// <summary>
/// Plans in force (db/008): the plan a workspace is on, whether a run may start or a flow be switched on, the
/// once-a-period emails to owners, and the sweep that starts trials and switches off what a smaller plan doesn't
/// cover (never deleting anything). Before db/008 has run nothing here limits anything beyond the server's run quota.
/// </summary>
public sealed class Plans(
    PlansRepository repo, FlowSchema schema, StripeBilling stripe, PlanCatalog catalog, FlowLimits limits, FlowsRepository flows,
    WorkspacesRepository workspaces, EmailService email, IConfiguration config, ILogger<Plans> log)
{
    /// <summary>How long before a trial ends its owners hear about it.</summary>
    public static readonly TimeSpan TrialWarning = TimeSpan.FromDays(3);

    // Notices already claimed in this process, so a workspace over its quota doesn't ask the database on every run.
    private static readonly ConcurrentDictionary<string, bool> Claimed = new();

    public PlanCatalog Catalog => catalog;
    public StripeBilling Stripe => stripe;
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow-web.azurewebsites.net").TrimEnd('/');

    public async Task<bool> ReadyAsync()
    {
        try { return await schema.PlansReadyAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    /// <summary>The plan in force for <paramref name="ws"/> (its row made, with its trial, on first sight).</summary>
    public async Task<(EffectivePlan Plan, PlanRow? Row)> ForAsync(WorkspaceRow ws, DateTime now)
    {
        if (!await ReadyAsync()) return (PlanRules.Resolve(null, catalog, now, ws.MonthlyRunQuota ?? limits.MonthlyRuns), null);
        var row = await repo.GetAsync(ws.Id) ?? await repo.EnsureAsync(ws.Id, PlanRules.NewTrialEnd(stripe.Enabled, now, catalog));
        return (PlanRules.Resolve(row, catalog, now, limits.MonthlyRuns), row);
    }

    /// <summary>
    /// A run about to start: null when it may (counted against the month), else why it's skipped — its flow uses
    /// premium steps the plan lacks, or the month's runs are used. Owners hear at 80% and 100%, once each a month.
    /// </summary>
    public async Task<RunRefusal?> AdmitRunAsync(WorkspaceRow ws, FlowDefinition def, DateTime now)
    {
        var (plan, _) = await ForAsync(ws, now);
        if (plan.Enforced && !plan.Limits.Premium && PlanRules.PremiumUses(def, catalog) is { Count: > 0 } uses)
        {
            var to = catalog.Cheapest(l => l.Premium);
            return new RunRefusal(PlanCodes.Premium,
                $"{PlanRules.Names(uses)} {(uses.Select(u => u.Label).Distinct().Count() == 1 ? "is" : "are")} on {Tiers.Name(to)} and up.", to);
        }
        var quota = PlanRules.RunQuota(ws, plan);
        var period = UsagePeriods.Month(now);
        var after = await repo.CountRunAsync(ws.Id, period, quota);
        if (after is null)
        {
            if (plan.Enforced) await NotifyUsageAsync(ws, plan, PlanNotices.Usage100, quota, quota, now);
            return new RunRefusal(PlanCodes.Runs, $"This workspace has used its {quota:N0} runs for the month.",
                catalog.Cheapest(l => l.Runs > quota));
        }
        if (plan.Enforced && PlanRules.UsageNotice(after.Value, quota) is { } kind) await NotifyUsageAsync(ws, plan, kind, after.Value, quota, now);
        return null;
    }

    /// <summary>Can this flow be switched on under the workspace's plan? Null when it can.</summary>
    public async Task<PlanBlock?> CanEnableAsync(WorkspaceRow ws, string flowId, FlowDefinition def, DateTime now)
    {
        var (plan, _) = await ForAsync(ws, now);
        if (!plan.Enforced) return null;
        var others = plan.Limits.ActiveFlows is null ? 0 : await repo.ActiveCountAsync(ws.Id, flowId);
        return PlanRules.CanEnable(plan, def, others, catalog);
    }

    /// <summary>A flow that's on may only be saved with premium steps the plan includes. Null when it may.</summary>
    public async Task<PlanBlock?> CanSaveOnAsync(WorkspaceRow ws, FlowDefinition def, DateTime now)
    {
        var (plan, _) = await ForAsync(ws, now);
        if (!plan.Enforced || plan.Limits.Premium) return null;
        var block = PlanRules.CanEnable(plan, def, 0, catalog);
        return block?.Code == PlanCodes.Premium ? block : null;
    }

    public static TierLimitsDto Dto(TierLimits l) => new(l.Tier, Tiers.Name(l.Tier), l.Runs, l.ActiveFlows, l.Premium, l.AiCredits, l.PrioritySupport);

    /// <summary>The plan as Plan &amp; billing shows it.</summary>
    public async Task<PlanDto> DtoAsync(WorkspaceRow ws, bool canManage, DateTime now)
    {
        var (plan, row) = await ForAsync(ws, now);
        var period = UsagePeriods.Month(now);
        var usage = new PlanUsageDto(await repo.RunsAsync(ws.Id, period), PlanRules.RunQuota(ws, plan), await repo.ActiveCountAsync(ws.Id), period,
            Controllers.FlowControllerBase.Iso(PlanRules.ResetsAt(now)));
        var billing = new BillingSetupDto(row is not null, stripe.Enabled, stripe.AvailableKeys, row?.StripeCustomerId is not null, StripeBilling.Currencies);
        var sub = plan.Source == PlanSources.Subscription;
        return new PlanDto(plan.Tier, Tiers.Name(plan.Tier), plan.Source, row?.Status, sub ? row?.Interval : null, Controllers.FlowControllerBase.Iso(plan.TrialEndsAt),
            plan.TrialDaysLeft, sub ? Controllers.FlowControllerBase.Iso(row?.CurrentPeriodEnd) : null, sub && row!.CancelAtPeriodEnd, row?.Currency,
            Dto(plan.Limits), usage, billing, canManage, catalog.All.Select(Dto).ToList(),
            new PremiumDto(catalog.PremiumApps.ToList(), catalog.PremiumSteps.ToList()));
    }

    // ── Emails ───────────────────────────────────────────────────────────

    private async Task<bool> ClaimAsync(Guid workspaceId, string kind, string period)
    {
        var key = $"{workspaceId:N}|{kind}|{period}";
        if (Claimed.ContainsKey(key)) return false;
        var won = await repo.ClaimNoticeAsync(workspaceId, kind, period);
        if (Claimed.Count > 10_000) Claimed.Clear();
        Claimed[key] = true;
        return won;
    }

    private async Task<int> SendToOwnersAsync(Guid workspaceId, (string Subject, string Html) mail)
    {
        if (!email.CanSend()) return 0;
        var sent = 0;
        foreach (var owner in await repo.OwnersAsync(workspaceId))
            if (await email.SendAsync("", "Vantra Flow", owner.Email, owner.Name ?? "", mail.Subject, mail.Html)) sent++;
        return sent;
    }

    private async Task NotifyUsageAsync(WorkspaceRow ws, EffectivePlan plan, string kind, int used, int quota, DateTime now)
    {
        try
        {
            if (!await ClaimAsync(ws.Id, kind, UsagePeriods.Month(now))) return;
            var name = plan.Source == PlanSources.Trial ? "the Pro trial" : Tiers.Name(plan.Tier);
            var sent = await SendToOwnersAsync(ws.Id, PlanMail.Usage(kind, ws.Name, used, quota, name, PlanRules.ResetsAt(now), AppBase, ws.Id));
            log.LogInformation("Workspace {Workspace} reached {Kind} of its runs; told {Sent} owner(s)", ws.Id, kind, sent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Couldn't send the {Kind} usage email for workspace {Workspace}", kind, ws.Id);
        }
    }

    // ── The sweep (FlowWorker, every few minutes) ──────────────────────────

    /// <summary>
    /// Starts the trials waiting on billing (once it's live), warns owners three days before a trial ends, and
    /// switches off the flows a workspace's plan no longer covers — premium ones first, then the least recently run
    /// over the active-flow limit — with the reason on each and one email to its owners.
    /// </summary>
    public async Task SweepAsync(DateTime now, CancellationToken ct)
    {
        if (!await ReadyAsync()) return;
        if (stripe.Enabled)
        {
            var started = await repo.StartTrialsAsync(now.AddDays(catalog.TrialDays));
            if (started > 0) log.LogInformation("Billing is live: started the {Days}-day trial for {Count} workspace(s)", catalog.TrialDays, started);
            foreach (var row in await repo.TrialsEndingAsync(now, now + TrialWarning))
            {
                if (ct.IsCancellationRequested) return;
                if (!await repo.ClaimTrialNoticeAsync(row.WorkspaceId, now)) continue;
                if (await workspaces.GetAsync(row.WorkspaceId) is not { } ws) continue;
                var free = PlanRules.Resolve(row with { TrialEndsAt = now.AddSeconds(-1) }, catalog, now, limits.MonthlyRuns);
                var wouldPause = PlanRules.FlowsToPause(await repo.ActiveFlowsAsync(ws.Id), free, catalog).Count;
                await SendToOwnersAsync(ws.Id, PlanMail.TrialEnding(ws.Name, row.TrialEndsAt!.Value, wouldPause, AppBase, ws.Id));
            }
        }
        foreach (var row in await repo.RestrictedAsync(now))
        {
            if (ct.IsCancellationRequested) return;
            var plan = PlanRules.Resolve(row, catalog, now, limits.MonthlyRuns);
            var pause = PlanRules.FlowsToPause(await repo.ActiveFlowsAsync(row.WorkspaceId), plan, catalog);
            if (pause.Count == 0) continue;
            var done = new List<(string Name, string Reason)>();
            foreach (var f in pause)
            {
                var reason = PlanRules.PauseReason(f, plan, catalog);
                if (await flows.PauseAsync(row.WorkspaceId, f.Id, reason)) done.Add((f.Name, reason));
            }
            if (done.Count == 0) continue;
            log.LogInformation("Workspace {Workspace} is on {Tier}: switched off {Count} flow(s) it doesn't cover", row.WorkspaceId, plan.Tier, done.Count);
            try
            {
                if (await workspaces.GetAsync(row.WorkspaceId) is { } ws)
                    await SendToOwnersAsync(ws.Id, PlanMail.Paused(ws.Name, done, Tiers.Name(plan.Tier), AppBase, ws.Id));
            }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Couldn't send the switched-off email for workspace {Workspace}", row.WorkspaceId); }
        }
    }

    // ── Stripe ───────────────────────────────────────────────────────────

    /// <summary>
    /// A Flow subscription's state onto its workspace: a live one sets the tier it pays for; an ended one (or one
    /// that can't be billed) puts the workspace back on Free — or what's left of its trial — if it's the one on record.
    /// </summary>
    public async Task ApplyAsync(SubscriptionView sub, Guid? hint, CancellationToken ct)
    {
        var wsId = sub.WorkspaceId ?? hint ?? await repo.WorkspaceOfAsync(sub.Id, sub.CustomerId);
        if (wsId is not { } id || await workspaces.GetAsync(id) is null)
        {
            log.LogInformation("Stripe subscription {SubscriptionId} isn't for a Flow workspace here; ignored", sub.Id);
            return;
        }
        var row = await repo.GetAsync(id) ?? await repo.EnsureAsync(id, null);
        // Live, on a price no longer in Stripe:Prices (rotated out of config): the tier it had stays.
        if (PlanRules.IsLive(sub.Status) && sub.Tier is null)
        {
            log.LogWarning("Subscription {SubscriptionId} is on a price not in Stripe:Prices; workspace {Workspace} keeps its tier", sub.Id, id);
            if (row.StripeSubscriptionId == sub.Id && Tiers.Paid.Contains(row.Tier)) sub = sub with { Tier = row.Tier, Interval = sub.Interval ?? row.Interval };
            else return;
        }
        if (PlanRules.IsLive(sub.Status) && sub.Tier is not null)
        {
            // Another live subscription on record (a second checkout before the first's webhook): the newer stays.
            if (row.StripeSubscriptionId is { } other && other != sub.Id && PlanRules.IsLive(row.Status))
            {
                try
                {
                    if (await stripe.SubscriptionAsync(other, ct) is { } current && PlanRules.IsLive(current.Status) && current.Created > sub.Created)
                    {
                        log.LogWarning("Workspace {Workspace} has subscriptions {Kept} and {Other}; kept the newer", id, other, sub.Id);
                        return;
                    }
                }
                catch (StripeException e) when (StripeBilling.IsMissing(e)) { /* gone: the new one takes its place */ }
            }
            await repo.ApplyAsync(id, sub.Tier, sub.Status, sub.Interval ?? "month", sub.PeriodEnd, sub.CancelAtPeriodEnd, sub.Id, sub.CustomerId, sub.Currency);
            log.LogInformation("Workspace {Workspace} is on {Tier} ({Status})", id, sub.Tier, sub.Status);
        }
        else if (row.StripeSubscriptionId == sub.Id)
        {
            await repo.EndAsync(sub.Id, sub.Status);
            log.LogInformation("Workspace {Workspace}'s subscription ended ({Status}); back on Free", id, sub.Status);
        }
        else if (sub.CustomerId is not null && row.StripeCustomerId is null)
        {
            await repo.SetCustomerAsync(id, sub.CustomerId);
        }
    }

    /// <summary>Brings the workspace's plan in line with Stripe now (after checkout, or when a webhook was missed).</summary>
    public async Task SyncAsync(Guid workspaceId, CancellationToken ct)
    {
        if (await repo.GetAsync(workspaceId) is not { StripeCustomerId: { } customer } row) return;
        var subs = await stripe.SubscriptionsOfAsync(customer, ct);
        if (subs.FirstOrDefault(s => PlanRules.IsLive(s.Status) && s.Tier is not null) is { } live) await ApplyAsync(live, workspaceId, ct);
        else if (row.StripeSubscriptionId is { } recorded && subs.FirstOrDefault(s => s.Id == recorded) is { } ended) await ApplyAsync(ended, workspaceId, ct);
    }

    /// <summary>A failed payment: its owners hear once per invoice.</summary>
    public async Task PaymentFailedAsync(string? subscriptionId, string? customerId, string invoiceId, DateTime? nextAttempt)
    {
        if (await repo.WorkspaceOfAsync(subscriptionId, customerId) is not { } id || await workspaces.GetAsync(id) is not { } ws) return;
        if (!await repo.ClaimNoticeAsync(id, PlanNotices.PaymentFailed, invoiceId)) return;
        var row = await repo.GetAsync(id);
        await SendToOwnersAsync(id, PlanMail.PaymentFailed(ws.Name, Tiers.Name(row?.Tier ?? Tiers.Free), nextAttempt, AppBase, id));
        log.LogInformation("Workspace {Workspace}: a payment failed; told its owners", id);
    }
}
