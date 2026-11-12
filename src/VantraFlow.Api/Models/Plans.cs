namespace VantraFlow.Api.Models;

/// <summary>
/// A workspace's plan (db/008, flow.WorkspacePlans). <c>Tier</c> is the paid tier while <c>StripeSubscriptionId</c>
/// is live, else <c>free</c>; <c>TrialEndsAt</c> is Flow's own Pro trial (null: not started — it starts when billing
/// goes live).
/// </summary>
public sealed record PlanRow(
    Guid WorkspaceId, string Tier, string? Status, string? Interval, DateTime? TrialEndsAt, DateTime? CurrentPeriodEnd, bool CancelAtPeriodEnd,
    string? StripeCustomerId, string? StripeSubscriptionId, string? Currency, DateTime? TrialNoticeAt, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>An enabled flow as the downgrade sweep weighs it.</summary>
public sealed record ActiveFlowRow(string Id, string Name, string Definition, DateTime? LastRunAt, DateTime UpdatedAt);

/// <summary>Someone to tell about the plan: the workspace's owners.</summary>
public sealed record PlanContact(string Email, string? Name);

public sealed record TierLimitsDto(string Tier, string Name, int Runs, int? ActiveFlows, bool Premium, int AiCredits, bool PrioritySupport);

public sealed record PlanUsageDto(int Runs, int RunQuota, int ActiveFlows, string Period, string ResetsAt);

/// <summary>
/// Billing as the studio shows it. <c>Ready</c>: db/008 has run. <c>Enabled</c>: Stripe is set up (else "Not set up
/// yet"). <c>Prices</c>: the price keys checkout can sell (<c>pro_month</c>, …).
/// </summary>
public sealed record BillingSetupDto(bool Ready, bool Enabled, IReadOnlyList<string> Prices, bool HasCustomer, IReadOnlyList<string> Currencies);

public sealed record PremiumDto(IReadOnlyList<string> Apps, IReadOnlyList<string> Steps);

/// <summary>
/// <c>Source</c>: <c>subscription</c> (paid), <c>trial</c> (Pro, <c>TrialDaysLeft</c>), <c>trial_pending</c> (Pro until
/// billing goes live, when the 14 days start), <c>free</c>, or <c>setup</c> (db/008 hasn't run: nothing is limited
/// beyond the server's run quota).
/// </summary>
public sealed record PlanDto(
    string Tier, string Name, string Source, string? Status, string? Interval, string? TrialEndsAt, int? TrialDaysLeft, string? PeriodEnd,
    bool CancelAtPeriodEnd, string? Currency, TierLimitsDto Limits, PlanUsageDto Usage, BillingSetupDto Billing, bool CanManage,
    IReadOnlyList<TierLimitsDto> Tiers, PremiumDto PremiumSet);

public sealed record CheckoutBody(string? PriceKey, string? Currency);

public sealed record BillingUrlDto(string Url);
