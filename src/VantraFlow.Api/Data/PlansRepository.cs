using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>Plans and billing (db/008): each workspace's plan, the once-a-period notices and the Stripe events handled.</summary>
public sealed class PlansRepository(SqlConnectionFactory factory)
{
    private const string Cols =
        "WorkspaceId, Tier, Status, Interval, TrialEndsAt, CurrentPeriodEnd, CancelAtPeriodEnd, StripeCustomerId, StripeSubscriptionId, Currency, " +
        "TrialNoticeAt, CreatedAt, UpdatedAt";

    public async Task<PlanRow?> GetAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<PlanRow>($"SELECT {Cols} FROM flow.WorkspacePlans WHERE WorkspaceId = @WorkspaceId;",
            new { WorkspaceId = workspaceId });
    }

    /// <summary>The workspace's plan, made (Free, with <paramref name="trialEndsAt"/>) if it has none yet.</summary>
    public async Task<PlanRow> EnsureAsync(Guid workspaceId, DateTime? trialEndsAt)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleAsync<PlanRow>($@"
IF NOT EXISTS (SELECT 1 FROM flow.WorkspacePlans WHERE WorkspaceId = @WorkspaceId)
BEGIN
    BEGIN TRY
        INSERT INTO flow.WorkspacePlans (WorkspaceId, TrialEndsAt) VALUES (@WorkspaceId, @TrialEndsAt);
    END TRY
    BEGIN CATCH
        IF ERROR_NUMBER() NOT IN (2601, 2627) THROW; -- made by another request just now
    END CATCH
END;
SELECT {Cols} FROM flow.WorkspacePlans WHERE WorkspaceId = @WorkspaceId;", new { WorkspaceId = workspaceId, TrialEndsAt = trialEndsAt });
    }

    /// <summary>Billing has gone live: every trial not yet started starts now (workspaces with no subscription).</summary>
    public async Task<int> StartTrialsAsync(DateTime trialEndsAt)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.WorkspacePlans SET TrialEndsAt = @TrialEndsAt, UpdatedAt = SYSUTCDATETIME()
WHERE TrialEndsAt IS NULL AND StripeSubscriptionId IS NULL;", new { TrialEndsAt = trialEndsAt });
    }

    /// <summary>Trials ending before <paramref name="before"/> that haven't been told so, with no subscription.</summary>
    public async Task<IReadOnlyList<PlanRow>> TrialsEndingAsync(DateTime now, DateTime before)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<PlanRow>($@"
SELECT TOP (200) {Cols} FROM flow.WorkspacePlans
WHERE TrialNoticeAt IS NULL AND StripeSubscriptionId IS NULL AND TrialEndsAt > @Now AND TrialEndsAt <= @Before;",
            new { Now = now, Before = before })).ToList();
    }

    /// <summary>Claims the trial-ending email (once per workspace); false when it went already.</summary>
    public async Task<bool> ClaimTrialNoticeAsync(Guid workspaceId, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(
            "UPDATE flow.WorkspacePlans SET TrialNoticeAt = @Now WHERE WorkspaceId = @WorkspaceId AND TrialNoticeAt IS NULL;",
            new { WorkspaceId = workspaceId, Now = now }) > 0;
    }

    /// <summary>
    /// Workspaces that may be on a smaller plan than their flows need — no live subscription and the trial over, or a
    /// subscription to a tier without premium steps — and that have a flow switched on.
    /// </summary>
    public async Task<IReadOnlyList<PlanRow>> RestrictedAsync(DateTime now)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<PlanRow>($@"
SELECT TOP (500) {Cols} FROM flow.WorkspacePlans p
WHERE EXISTS (SELECT 1 FROM flow.Flows f WHERE f.WorkspaceId = p.WorkspaceId AND f.Enabled = 1)
  AND (  (p.StripeSubscriptionId IS NOT NULL AND p.Status IN (N'active', N'trialing', N'past_due') AND p.Tier IN (N'free', N'starter'))
      OR ((p.StripeSubscriptionId IS NULL OR p.Status IS NULL OR p.Status NOT IN (N'active', N'trialing', N'past_due'))
          AND p.TrialEndsAt IS NOT NULL AND p.TrialEndsAt <= @Now));",
            new { Now = now })).ToList();
    }

    /// <summary>The workspace's flows that are on, for the downgrade sweep.</summary>
    public async Task<IReadOnlyList<ActiveFlowRow>> ActiveFlowsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ActiveFlowRow>(@"
SELECT Id, Name, Definition, LastRunAt, UpdatedAt FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Enabled = 1;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>How many of the workspace's flows are on, leaving out <paramref name="except"/>.</summary>
    public async Task<int> ActiveCountAsync(Guid workspaceId, string? except = null)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Enabled = 1 AND (@Except IS NULL OR Id <> @Except);",
            new { WorkspaceId = workspaceId, Except = except });
    }

    /// <summary>
    /// Counts one run against the month, within <paramref name="quota"/>: the month's runs after it, or null (nothing
    /// counted) when the quota is used.
    /// </summary>
    public async Task<int?> CountRunAsync(Guid workspaceId, string period, int quota)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<int?>(@"
MERGE flow.Usage WITH (HOLDLOCK) AS u
USING (SELECT @WorkspaceId AS WorkspaceId, @Period AS Period) AS s
ON u.WorkspaceId = s.WorkspaceId AND u.Period = s.Period
WHEN NOT MATCHED THEN INSERT (WorkspaceId, Period, Runs, Emails) VALUES (s.WorkspaceId, s.Period, 0, 0);
UPDATE flow.Usage SET Runs = Runs + 1 OUTPUT inserted.Runs
WHERE WorkspaceId = @WorkspaceId AND Period = @Period AND Runs < @Quota;", new { WorkspaceId = workspaceId, Period = period, Quota = quota });
    }

    /// <summary>The month's runs so far.</summary>
    public async Task<int> RunsAsync(Guid workspaceId, string period)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int?>("SELECT Runs FROM flow.Usage WHERE WorkspaceId = @WorkspaceId AND Period = @Period;",
            new { WorkspaceId = workspaceId, Period = period }) ?? 0;
    }

    /// <summary>Claims a once-a-period notice; false when it was sent already.</summary>
    public async Task<bool> ClaimNoticeAsync(Guid workspaceId, string kind, string period)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
INSERT INTO flow.PlanNotices (WorkspaceId, Kind, Period)
SELECT @WorkspaceId, @Kind, @Period
WHERE NOT EXISTS (SELECT 1 FROM flow.PlanNotices WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND Kind = @Kind AND Period = @Period);",
            new { WorkspaceId = workspaceId, Kind = kind, Period = period }) > 0;
    }

    /// <summary>The workspace's owners with an email address.</summary>
    public async Task<IReadOnlyList<PlanContact>> OwnersAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<PlanContact>(@"
SELECT Email, Name FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId AND Role = N'owner' AND Email IS NOT NULL AND Email <> N'';", new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task SetCustomerAsync(Guid workspaceId, string customerId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.WorkspacePlans SET StripeCustomerId = @CustomerId, UpdatedAt = SYSUTCDATETIME() WHERE WorkspaceId = @WorkspaceId;",
            new { WorkspaceId = workspaceId, CustomerId = customerId });
    }

    public async Task ClearCustomerAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.WorkspacePlans SET StripeCustomerId = NULL, UpdatedAt = SYSUTCDATETIME() WHERE WorkspaceId = @WorkspaceId AND StripeSubscriptionId IS NULL;",
            new { WorkspaceId = workspaceId });
    }

    /// <summary>The workspace a subscription or customer belongs to, when it's one of ours.</summary>
    public async Task<Guid?> WorkspaceOfAsync(string? subscriptionId, string? customerId)
    {
        using var conn = factory.Flow();
        return await conn.QueryFirstOrDefaultAsync<Guid?>(@"
SELECT TOP (1) WorkspaceId FROM flow.WorkspacePlans
WHERE (@SubscriptionId IS NOT NULL AND StripeSubscriptionId = @SubscriptionId)
   OR (@CustomerId IS NOT NULL AND StripeCustomerId = @CustomerId)
ORDER BY CASE WHEN StripeSubscriptionId = @SubscriptionId THEN 0 ELSE 1 END;",
            new { SubscriptionId = subscriptionId, CustomerId = customerId });
    }

    /// <summary>A live subscription's state onto its workspace: the tier it pays for, its status and period.</summary>
    public async Task ApplyAsync(Guid workspaceId, string tier, string status, string interval, DateTime? periodEnd, bool cancelAtPeriodEnd,
        string subscriptionId, string? customerId, string? currency)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.WorkspacePlans SET
    Tier = @Tier, Status = @Status, Interval = @Interval, CurrentPeriodEnd = @PeriodEnd, CancelAtPeriodEnd = @Cancel,
    StripeSubscriptionId = @SubscriptionId, StripeCustomerId = COALESCE(@CustomerId, StripeCustomerId), Currency = COALESCE(@Currency, Currency),
    UpdatedAt = SYSUTCDATETIME()
WHERE WorkspaceId = @WorkspaceId;",
            new
            {
                WorkspaceId = workspaceId, Tier = tier, Status = status, Interval = interval, PeriodEnd = periodEnd, Cancel = cancelAtPeriodEnd,
                SubscriptionId = subscriptionId, CustomerId = customerId, Currency = currency?.ToLowerInvariant(),
            });
    }

    /// <summary>
    /// A subscription has ended (or can no longer be billed): the workspace goes back to Free — its trial, if any is
    /// left, still applies. Only when it's the subscription on record.
    /// </summary>
    public async Task<bool> EndAsync(string subscriptionId, string status)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.WorkspacePlans SET
    Tier = N'free', Status = @Status, StripeSubscriptionId = NULL, CancelAtPeriodEnd = 0, CurrentPeriodEnd = NULL, Interval = NULL,
    UpdatedAt = SYSUTCDATETIME()
WHERE StripeSubscriptionId = @SubscriptionId;", new { SubscriptionId = subscriptionId, Status = status }) > 0;
    }

    /// <summary>Claims a Stripe event (its id); false when it was handled already.</summary>
    public async Task<bool> ClaimEventAsync(string id, string type)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
INSERT INTO flow.BillingEvents (Id, Type)
SELECT @Id, @Type WHERE NOT EXISTS (SELECT 1 FROM flow.BillingEvents WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id);",
            new { Id = id, Type = type.Length > 80 ? type[..80] : type }) > 0;
    }

    /// <summary>Lets an event that failed part way be handled again when Stripe retries it.</summary>
    public async Task ReleaseEventAsync(string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.BillingEvents WHERE Id = @Id;", new { Id = id });
    }

    /// <summary>Handled events older than <paramref name="before"/> go (Stripe retries for three days).</summary>
    public async Task PruneAsync(DateTime before)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE TOP (5000) FROM flow.BillingEvents WHERE ReceivedAt < @Before;
DELETE TOP (5000) FROM flow.PlanNotices WHERE SentAt < @Notices;", new { Before = before, Notices = before.AddDays(-90) });
    }
}
