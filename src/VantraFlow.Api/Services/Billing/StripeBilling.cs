using Stripe;

namespace VantraFlow.Api.Services.Billing;

/// <summary>A Stripe subscription as Flow reads it: whose it is, the tier it pays for, and its state.</summary>
public sealed record SubscriptionView(
    string Id, Guid? WorkspaceId, string? CustomerId, string? Tier, string? Interval, string Status, DateTime? PeriodEnd, bool CancelAtPeriodEnd,
    string? Currency, DateTime Created);

/// <summary>
/// Flow's side of Stripe, as Vantra Projects' StripeBillingService: Checkout and the customer portal for one flat
/// price per workspace, reading subscriptions, and verifying webhook signatures. Off (<see cref="Enabled"/> false)
/// until <c>Stripe:SecretKey</c> is set, and then the studio says billing isn't set up yet. The amounts live in
/// Stripe: <c>Stripe:Prices:{key}</c> names a price id for each of starter_month, starter_year, pro_month, pro_year,
/// business_month and business_year, each with currency options for GBP, EUR, AUD and CAD as Projects' prices have.
/// <para>The Stripe account may be shared with Vantra Projects, whose webhook then sees Flow's events and Flow's
/// sees Projects'. Everything Flow makes is stamped <c>vantra_app=flow</c> (and <c>flow_workspace_id</c>, a key
/// Projects doesn't read), and anything not stamped or priced as Flow's is ignored.</para>
/// </summary>
public sealed class StripeBilling
{
    public const string AppKey = "vantra_app";
    public const string AppValue = "flow";
    public const string WorkspaceKey = "flow_workspace_id";

    public static readonly IReadOnlyList<string> PriceKeys =
        ["starter_month", "starter_year", "pro_month", "pro_year", "business_month", "business_year"];
    /// <summary>The currencies the studio shows prices in (as Projects).</summary>
    public static readonly IReadOnlyList<string> Currencies = ["usd", "gbp", "eur", "aud", "cad"];

    private readonly ILogger<StripeBilling>? log;
    private readonly string secretKey;
    private readonly string webhookSecret;
    private readonly IReadOnlyDictionary<string, string> prices;                        // key → price id
    private readonly IReadOnlyDictionary<string, (string Tier, string Interval)> byId; // price id → tier and interval
    private readonly SemaphoreSlim portalLock = new(1, 1);
    private string? portalConfigId;

    public StripeBilling(IConfiguration config, ILogger<StripeBilling>? log = null)
    {
        this.log = log;
        // Trimmed: these are pasted into App Service by hand, and a trailing space is "no such price" to Stripe.
        secretKey = config["Stripe:SecretKey"]?.Trim() ?? "";
        webhookSecret = config["Stripe:WebhookSecret"]?.Trim() ?? "";
        var map = new Dictionary<string, string>();
        foreach (var key in PriceKeys)
            if (config[$"Stripe:Prices:{key}"]?.Trim() is { Length: > 0 } id) map[key] = id;
        // One price under two keys can't say which it sells: neither is used, and the log names them.
        foreach (var dup in map.GroupBy(kv => kv.Value).Where(g => g.Count() > 1).ToList())
        {
            var keys = dup.Select(kv => kv.Key).ToList();
            log?.LogError("Stripe price {PriceId} is configured under {Keys}; each Stripe:Prices key needs its own price", dup.Key, string.Join(", ", keys));
            foreach (var k in keys) map.Remove(k);
        }
        prices = map;
        byId = map.ToDictionary(kv => kv.Value, kv => PlanRules.ParsePriceKey(kv.Key)!.Value);
    }

    /// <summary>A secret key is set: checkout, the portal and the webhook are live (and trials start).</summary>
    public bool Enabled => secretKey.Length > 0;

    /// <summary>The price keys checkout can sell.</summary>
    public IReadOnlyList<string> AvailableKeys => PriceKeys.Where(prices.ContainsKey).ToList();

    public string? PriceIdFor(string? key) => key is not null && prices.TryGetValue(key, out var id) ? id : null;

    public (string Tier, string Interval)? ForPrice(string? priceId) => priceId is not null && byId.TryGetValue(priceId, out var v) ? v : null;

    private StripeClient Client => new(secretKey);

    /// <summary>A Stripe customer for the workspace (its own, so the portal shows only Flow's subscription).</summary>
    public async Task<string> CreateCustomerAsync(Guid workspaceId, string workspaceName, string? email, CancellationToken ct)
    {
        var customer = await new CustomerService(Client).CreateAsync(new CustomerCreateOptions
        {
            Name = workspaceName.Length > 250 ? workspaceName[..250] : workspaceName,
            Email = string.IsNullOrWhiteSpace(email) ? null : email,
            Description = "Vantra Flow workspace",
            Metadata = Stamp(workspaceId),
        }, cancellationToken: ct);
        return customer.Id;
    }

    public static Dictionary<string, string> Stamp(Guid workspaceId) => new() { [AppKey] = AppValue, [WorkspaceKey] = workspaceId.ToString("D") };

    /// <summary>
    /// Refuses (<see cref="CurrencyUnavailableException"/>) a currency the price isn't sold in, rather than charging
    /// in another than the page showed.
    /// </summary>
    public async Task EnsureSoldInAsync(string priceId, string? currency, CancellationToken ct)
    {
        if (currency is null) return;
        var price = await new PriceService(Client).GetAsync(priceId, new PriceGetOptions { Expand = ["currency_options"] }, cancellationToken: ct);
        if (string.Equals(price.Currency, currency, StringComparison.OrdinalIgnoreCase)) return;
        if (price.CurrencyOptions?.ContainsKey(currency) == true) return;
        throw new CurrencyUnavailableException();
    }

    public sealed class CurrencyUnavailableException : Exception;

    /// <summary>
    /// A subscription Checkout session for one price (one per workspace, quantity 1); its URL. A trial still running
    /// carries over (<paramref name="trialEnd"/>), so nothing is charged until it ends.
    /// </summary>
    public async Task<string> CheckoutAsync(string priceId, string customerId, Guid workspaceId, string returnBase, string? currency, DateTime? trialEnd, CancellationToken ct)
    {
        var options = new Stripe.Checkout.SessionCreateOptions
        {
            Mode = "subscription",
            Customer = customerId,
            ClientReferenceId = workspaceId.ToString("D"),
            LineItems = [new() { Price = priceId, Quantity = 1 }],
            AllowPromotionCodes = true,
            Currency = currency,
            Metadata = Stamp(workspaceId),
            SubscriptionData = new Stripe.Checkout.SessionSubscriptionDataOptions
            {
                Metadata = Stamp(workspaceId),
                TrialEnd = trialEnd,
            },
            // With a trial carried over nothing is due today; the card is still taken, for when it ends.
            PaymentMethodCollection = "always",
            SuccessUrl = $"{returnBase}/settings?tab=billing&checkout=done",
            CancelUrl = $"{returnBase}/plans?checkout=cancelled",
        };
        var session = await new Stripe.Checkout.SessionService(Client).CreateAsync(options, cancellationToken: ct);
        return session.Url;
    }

    /// <summary>The customer portal (invoices, the card, switching between Flow's plans, cancelling at period end).</summary>
    public async Task<string> PortalAsync(string customerId, string returnUrl, CancellationToken ct)
    {
        var config = await PortalConfigurationAsync(ct);
        var session = await new Stripe.BillingPortal.SessionService(Client).CreateAsync(new Stripe.BillingPortal.SessionCreateOptions
        {
            Customer = customerId,
            ReturnUrl = returnUrl,
            Configuration = config,
        }, cancellationToken: ct);
        return session.Url;
    }

    /// <summary>
    /// Flow's own portal configuration, made once and kept in step with the configured prices: never the account's
    /// default, which may offer another product's plans.
    /// </summary>
    private async Task<string> PortalConfigurationAsync(CancellationToken ct)
    {
        if (portalConfigId is { } known) return known;
        await portalLock.WaitAsync(ct);
        try
        {
            if (portalConfigId is { } again) return again;
            var priceSvc = new PriceService(Client);
            var products = new Dictionary<string, List<string>>();
            foreach (var id in prices.Values)
            {
                var p = await priceSvc.GetAsync(id, cancellationToken: ct);
                if (p.ProductId is null || p.Active != true) continue;
                (products.TryGetValue(p.ProductId, out var list) ? list : products[p.ProductId] = []).Add(p.Id);
            }
            var features = new Stripe.BillingPortal.ConfigurationFeaturesOptions
            {
                InvoiceHistory = new() { Enabled = true },
                PaymentMethodUpdate = new() { Enabled = true },
                CustomerUpdate = new() { Enabled = true, AllowedUpdates = ["email", "address", "tax_id"] },
                SubscriptionCancel = new() { Enabled = true, Mode = "at_period_end" },
                SubscriptionUpdate = products.Count == 0 ? new() { Enabled = false } : new()
                {
                    Enabled = true,
                    DefaultAllowedUpdates = ["price", "promotion_code"],
                    ProrationBehavior = "create_prorations",
                    Products = products.Select(kv => new Stripe.BillingPortal.ConfigurationFeaturesSubscriptionUpdateProductOptions { Product = kv.Key, Prices = kv.Value }).ToList(),
                },
            };
            var svc = new Stripe.BillingPortal.ConfigurationService(Client);
            var existing = (await svc.ListAsync(new Stripe.BillingPortal.ConfigurationListOptions { Active = true, Limit = 100 }, cancellationToken: ct))
                .FirstOrDefault(c => c.Metadata?.TryGetValue(AppKey, out var v) == true && v == AppValue);
            var business = new Stripe.BillingPortal.ConfigurationBusinessProfileOptions { Headline = "Vantra Flow" };
            var made = existing is null
                ? await svc.CreateAsync(new Stripe.BillingPortal.ConfigurationCreateOptions
                    { Features = features, BusinessProfile = business, Metadata = new() { [AppKey] = AppValue } }, cancellationToken: ct)
                : await svc.UpdateAsync(existing.Id, new Stripe.BillingPortal.ConfigurationUpdateOptions { Features = features, BusinessProfile = business }, cancellationToken: ct);
            portalConfigId = made.Id;
            return made.Id;
        }
        finally { portalLock.Release(); }
    }

    public async Task<SubscriptionView?> SubscriptionAsync(string id, CancellationToken ct) =>
        Read(await new SubscriptionService(Client).GetAsync(id, cancellationToken: ct));

    /// <summary>The customer's subscriptions that are Flow's, newest first.</summary>
    public async Task<IReadOnlyList<SubscriptionView>> SubscriptionsOfAsync(string customerId, CancellationToken ct)
    {
        var list = await new SubscriptionService(Client).ListAsync(new SubscriptionListOptions { Customer = customerId, Status = "all", Limit = 20 }, cancellationToken: ct);
        return list.Data.Select(Read).OfType<SubscriptionView>().OrderByDescending(s => s.Created).ToList();
    }

    /// <summary>
    /// A subscription as Flow reads it, or null when it isn't Flow's: neither stamped <c>vantra_app=flow</c> nor on
    /// one of Flow's prices.
    /// </summary>
    public SubscriptionView? Read(Subscription? sub)
    {
        if (sub is null) return null;
        var stamped = sub.Metadata?.TryGetValue(AppKey, out var app) == true && app == AppValue;
        var item = sub.Items?.Data?.FirstOrDefault(i => ForPrice(i.Price?.Id) is not null);
        if (!stamped && item is null) return null;
        var plan = ForPrice(item?.Price?.Id);
        Guid? ws = sub.Metadata?.TryGetValue(WorkspaceKey, out var raw) == true && Guid.TryParse(raw, out var g) ? g : null;
        if (plan is null && item is null && stamped)
            log?.LogWarning("Subscription {SubscriptionId} is Flow's but on a price not in Stripe:Prices; it's read as no plan", sub.Id);
        return new SubscriptionView(sub.Id, ws, sub.CustomerId, plan?.Tier, plan?.Interval, sub.Status, sub.CurrentPeriodEnd, sub.CancelAtPeriodEnd,
            sub.Currency, sub.Created);
    }

    /// <summary>Verifies the signature (<c>Stripe:WebhookSecret</c>) and reads the event; throws when it doesn't verify.</summary>
    public Event ConstructEvent(string json, string signature)
    {
        if (webhookSecret.Length == 0) throw new StripeException("The webhook secret isn't set.");
        return EventUtility.ConstructEvent(json, signature, webhookSecret, throwOnApiVersionMismatch: false);
    }

    public bool WebhookReady => Enabled && webhookSecret.Length > 0;

    /// <summary>A stored id Stripe has no record of (typically one saved under test keys).</summary>
    public static bool IsMissing(StripeException ex) => ex.StripeError?.Code == "resource_missing";
}
