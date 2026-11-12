using System.Reflection;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Billing;

/// <summary>Flow's tiers, cheapest first. A workspace is on exactly one (per workspace, not per seat).</summary>
public static class Tiers
{
    public const string Free = "free";
    public const string Starter = "starter";
    public const string Pro = "pro";
    public const string Business = "business";

    public static readonly IReadOnlyList<string> All = [Free, Starter, Pro, Business];
    /// <summary>The tiers sold through Stripe.</summary>
    public static readonly IReadOnlyList<string> Paid = [Starter, Pro, Business];

    public static bool IsKnown(string? tier) => tier is not null && All.Contains(tier);
    public static int Rank(string? tier) => tier is null ? -1 : All.ToList().IndexOf(tier);

    public static string Name(string tier) => tier switch
    {
        Starter => "Starter",
        Pro => "Pro",
        Business => "Business",
        _ => "Free",
    };
}

/// <summary>What a tier allows. <c>ActiveFlows</c> null: no limit. <c>Premium</c>: premium apps and steps can be switched on.</summary>
public sealed record TierLimits(string Tier, int Runs, int? ActiveFlows, bool Premium, int AiCredits, bool PrioritySupport);

/// <summary>
/// The plans' numbers, from config so they're easy to change without a release (<c>Billing:*</c>):
/// <code>
/// Billing:TrialDays                    14   (Pro, for new workspaces)
/// Billing:Tiers:{tier}:Runs            free 100, starter 2,000, pro 10,000, business 50,000 (a calendar month, UTC)
/// Billing:Tiers:{tier}:ActiveFlows     free 3; the others unlimited (0 or empty)
/// Billing:Tiers:{tier}:Premium         pro and business
/// Billing:Tiers:{tier}:AiCredits       pro 200, business 1,000 (a month)
/// Billing:PremiumApps                  "slack,microsoft,google,http,webhook,flow,smtp" (apps whose steps and triggers are premium; smtp: Send email from an SMTP mailbox)
/// Billing:PremiumSteps                 "paths,approval" (step types that are premium)
/// </code>
/// The amounts people pay are not here: they live in Stripe (prices under <c>Stripe:Prices:{key}</c>), with the
/// studio's display copy in one catalogue file (src/lib/planCatalog.ts).
/// </summary>
public sealed class PlanCatalog
{
    public const int DefaultTrialDays = 14;

    private static readonly IReadOnlyDictionary<string, TierLimits> Defaults = new Dictionary<string, TierLimits>
    {
        [Tiers.Free] = new(Tiers.Free, 100, 3, false, 0, false),
        [Tiers.Starter] = new(Tiers.Starter, 2_000, null, false, 0, false),
        [Tiers.Pro] = new(Tiers.Pro, 10_000, null, true, 200, false),
        [Tiers.Business] = new(Tiers.Business, 50_000, null, true, 1_000, true),
    };

    /// <summary>The apps the brief names premium: the outside apps, HTTP requests, webhooks and sub-flows.</summary>
    public static readonly IReadOnlyList<string> DefaultPremiumApps = ["slack", "microsoft", "google", "http", "webhook", "flow", "smtp"];
    /// <summary>Step types that are premium whatever app they're in: paths and approvals.</summary>
    public static readonly IReadOnlyList<string> DefaultPremiumSteps = [StepTypes.Paths, "approval"];

    private readonly IReadOnlyDictionary<string, TierLimits> tiers;

    public int TrialDays { get; }
    public IReadOnlySet<string> PremiumApps { get; }
    public IReadOnlySet<string> PremiumSteps { get; }

    public PlanCatalog(IReadOnlyDictionary<string, TierLimits>? tiers = null, int trialDays = DefaultTrialDays,
        IEnumerable<string>? premiumApps = null, IEnumerable<string>? premiumSteps = null)
    {
        this.tiers = tiers ?? Defaults;
        TrialDays = Math.Clamp(trialDays, 0, 90);
        PremiumApps = new HashSet<string>(premiumApps ?? DefaultPremiumApps, StringComparer.Ordinal);
        PremiumSteps = new HashSet<string>(premiumSteps ?? DefaultPremiumSteps, StringComparer.Ordinal);
    }

    public static PlanCatalog From(IConfiguration? c)
    {
        int Int(string key, int fallback, int min, int max) => int.TryParse(c?[key], out var v) ? Math.Clamp(v, min, max) : fallback;
        bool Bool(string key, bool fallback) => bool.TryParse(c?[key], out var v) ? v : fallback;
        var tiers = new Dictionary<string, TierLimits>();
        foreach (var (tier, d) in Defaults)
        {
            var p = $"Billing:Tiers:{tier}:";
            var flows = c?[p + "ActiveFlows"] is { } raw ? (int.TryParse(raw, out var n) && n > 0 ? Math.Min(n, 100_000) : (int?)null) : d.ActiveFlows;
            tiers[tier] = new TierLimits(tier,
                Int(p + "Runs", d.Runs, 0, 100_000_000), flows, Bool(p + "Premium", d.Premium),
                Int(p + "AiCredits", d.AiCredits, 0, 1_000_000), Bool(p + "PrioritySupport", d.PrioritySupport));
        }
        static IEnumerable<string>? List(string? raw) => string.IsNullOrWhiteSpace(raw) ? null
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant());
        return new PlanCatalog(tiers, Int("Billing:TrialDays", DefaultTrialDays, 0, 90), List(c?["Billing:PremiumApps"]), List(c?["Billing:PremiumSteps"]));
    }

    public TierLimits For(string? tier) => tiers.TryGetValue(tier ?? Tiers.Free, out var l) ? l : tiers[Tiers.Free];

    public IReadOnlyList<TierLimits> All => Tiers.All.Select(For).ToList();

    /// <summary>The cheapest tier that allows <paramref name="need"/>.</summary>
    public string Cheapest(Func<TierLimits, bool> need) => Tiers.All.FirstOrDefault(t => need(For(t))) ?? Tiers.Business;

    // The catalog's own word, once its entries carry one (a bool Premium property). Read by reflection so this
    // builds with or without it; until then the lists above decide.
    private static readonly PropertyInfo? EntryPremium = typeof(CatalogEntry).GetProperty("Premium", BindingFlags.Public | BindingFlags.Instance);

    /// <summary>Is this app's trigger or action premium? The catalog's flag, or the list.</summary>
    public bool IsPremium(string? app, string? key, bool trigger)
    {
        if (string.IsNullOrEmpty(app)) return false;
        if (PremiumApps.Contains(app)) return true;
        if (EntryPremium is null) return false;
        var entry = trigger ? FlowCatalog.Trigger(app, key) : FlowCatalog.Action(app, key);
        return entry is not null && EntryPremium.GetValue(entry) is true;
    }
}
