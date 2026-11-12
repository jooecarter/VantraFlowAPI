using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Stripe;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Billing;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Plan &amp; billing, per workspace (Flow is billed on its own, apart from Orbit and Projects).
/// <code>
/// GET  api/w/{ws}/plan            → the plan in force, its limits and usage, every tier's limits, and whether billing is set up
/// POST api/w/{ws}/plan/checkout   { priceKey, currency } → { url }: Stripe Checkout (owners and admins)
/// POST api/w/{ws}/plan/portal     → { url }: Stripe's billing portal (owners and admins)
/// POST api/w/{ws}/plan/sync       → the plan, read again from Stripe (owners and admins; after checkout)
/// </code>
/// Anyone in the workspace sees its plan; only owners and admins change it. Without Stripe set up the plan still
/// reads (billing.enabled false: "Not set up yet") and checkout answers 503 <c>billing_off</c>; before db/008 the
/// plan reads as <c>setup</c> and checkout answers 503 <c>not_ready</c>.
/// </summary>
public sealed class PlanController(WorkspacesRepository workspaces, Plans plans, PlansRepository repo, IConfiguration config, ILogger<PlanController> log)
    : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/plan")]
    public async Task<IActionResult> Get(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        return Ok(await plans.DtoAsync(ws, FlowRoles.CanManage(role), DateTime.UtcNow));
    }

    [HttpPost("api/w/{workspaceId:guid}/plan/checkout")]
    public async Task<IActionResult> Checkout(Guid workspaceId, [FromBody] CheckoutBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await Unavailable() is { } off) return off;
        var key = body.PriceKey?.Trim().ToLowerInvariant();
        if (plans.Stripe.PriceIdFor(key) is not { } priceId)
            return BadRequest(new { error = "That plan isn't on sale yet. This is a billing setup problem, not something you did.", code = "unknown_price" });
        var currency = body.Currency?.Trim().ToLowerInvariant() is { Length: > 0 } c ? c : null;
        if (currency is not null && !StripeBilling.Currencies.Contains(currency))
            return Conflict(new { error = "Checkout isn't available in that currency.", code = "currency_unavailable" });

        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        var (plan, row) = await plans.ForAsync(ws, now);
        if (row is null) return NotReady();
        // A workspace with a subscription changes it in the billing portal: a second checkout would bill twice.
        if (plan.Source == PlanSources.Subscription)
            return Conflict(new { error = "This workspace already has a plan. Change it in the billing portal.", code = "has_subscription" });

        var trialEnd = plan.Source == PlanSources.Trial ? PlanRules.CarriedTrialEnd(plan.TrialEndsAt, now) : null;
        try
        {
            await plans.Stripe.EnsureSoldInAsync(priceId, currency, ct);
            var customer = row.StripeCustomerId ?? await NewCustomerAsync(ws, ct);
            try
            {
                return Ok(new BillingUrlDto(await plans.Stripe.CheckoutAsync(priceId, customer, workspaceId, ReturnBase(), currency, trialEnd, ct)));
            }
            catch (StripeException e) when (StripeBilling.IsMissing(e) && e.StripeError?.Param == "customer")
            {
                // A customer saved under other keys (test mode): a new one, and the dead id goes.
                log.LogWarning("Workspace {Workspace}'s Stripe customer is unknown to Stripe; making a new one", workspaceId);
                await repo.ClearCustomerAsync(workspaceId);
                customer = await NewCustomerAsync(ws, ct);
                return Ok(new BillingUrlDto(await plans.Stripe.CheckoutAsync(priceId, customer, workspaceId, ReturnBase(), currency, trialEnd, ct)));
            }
        }
        catch (StripeBilling.CurrencyUnavailableException)
        {
            return Conflict(new { error = "Checkout isn't available in that currency yet. This is a billing setup problem, not something you did.", code = "currency_unavailable" });
        }
        catch (StripeException e)
        {
            // The code and param name the fault ("line_items[0][price]": the configured price isn't in this account).
            log.LogError(e, "Stripe refused checkout for workspace {Workspace}, price key {Key}", workspaceId, key);
            return StatusCode(502, new { error = "Stripe couldn't start checkout just now. Try again in a moment.", code = "stripe_error", stripe = e.StripeError?.Code });
        }
    }

    [HttpPost("api/w/{workspaceId:guid}/plan/portal")]
    public async Task<IActionResult> Portal(Guid workspaceId, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await Unavailable() is { } off) return off;
        if (await repo.GetAsync(workspaceId) is not { StripeCustomerId: { } customer })
            return Conflict(new { error = "There's no billing to manage yet. Choose a plan first.", code = "no_customer" });
        try
        {
            return Ok(new BillingUrlDto(await plans.Stripe.PortalAsync(customer, $"{ReturnBase()}/settings?tab=billing", ct)));
        }
        catch (StripeException e)
        {
            log.LogError(e, "Couldn't open the Stripe billing portal for workspace {Workspace}", workspaceId);
            return StatusCode(502, new { error = "Couldn't open billing just now. Try again in a moment.", code = "stripe_error" });
        }
    }

    [HttpPost("api/w/{workspaceId:guid}/plan/sync")]
    public async Task<IActionResult> Sync(Guid workspaceId, CancellationToken ct)
    {
        var (role, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await Unavailable() is { } off) return off;
        try { await plans.SyncAsync(workspaceId, ct); }
        catch (StripeException e)
        {
            log.LogWarning(e, "Couldn't read workspace {Workspace}'s subscription from Stripe", workspaceId);
            return StatusCode(502, new { error = "Couldn't reach Stripe just now. Try again in a moment.", code = "stripe_error" });
        }
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        return Ok(await plans.DtoAsync(ws, FlowRoles.CanManage(role), DateTime.UtcNow));
    }

    private async Task<string> NewCustomerAsync(WorkspaceRow ws, CancellationToken ct)
    {
        var id = await plans.Stripe.CreateCustomerAsync(ws.Id, ws.Name, Email, ct);
        await repo.SetCustomerAsync(ws.Id, id);
        return id;
    }

    private async Task<IActionResult?> Unavailable()
    {
        if (!plans.Stripe.Enabled) return StatusCode(503, new { error = "Billing isn't set up yet.", code = "billing_off" });
        return await plans.ReadyAsync() ? null : NotReady();
    }

    private ObjectResult NotReady() => StatusCode(503, new { error = "Plans arrive with the next server update.", code = "not_ready" });

    /// <summary>Where Stripe sends people back: the studio they came from when it's one of ours, else App:BaseUrl.</summary>
    private string ReturnBase()
    {
        var origin = Request.Headers.Origin.ToString().TrimEnd('/');
        var allowed = config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        return origin.Length > 0 && allowed.Any(a => string.Equals(a.TrimEnd('/'), origin, StringComparison.OrdinalIgnoreCase)) ? origin : plans.AppBase;
    }
}

/// <summary>
/// Stripe's webhook for Flow (<c>POST api/billing/webhook</c>): the signature verified with
/// <c>Stripe:WebhookSecret</c>, each event handled once (flow.BillingEvents), and the subscription re-read from
/// Stripe rather than trusting the event's copy, which may be older than a change since. Events that aren't Flow's
/// (a shared Stripe account also carries Vantra Projects') are acknowledged and ignored. Until db/008 has run it
/// answers 503, so Stripe keeps the events and tries again.
/// </summary>
[ApiController]
[AllowAnonymous]
[EnableRateLimiting(IncomingController.RateLimitPolicy)]
public sealed class BillingWebhookController(Plans plans, PlansRepository repo, ILogger<BillingWebhookController> log) : ControllerBase
{
    private static readonly HashSet<string> Handled =
    [
        Stripe.Events.CheckoutSessionCompleted, Stripe.Events.CustomerSubscriptionCreated, Stripe.Events.CustomerSubscriptionUpdated,
        Stripe.Events.CustomerSubscriptionDeleted, Stripe.Events.InvoicePaymentFailed,
    ];

    [HttpPost("api/billing/webhook")]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        if (!plans.Stripe.Enabled) return NotFound();
        if (!plans.Stripe.WebhookReady) return StatusCode(503, new { error = "The webhook secret isn't set." });
        string json;
        using (var reader = new StreamReader(Request.Body)) json = await reader.ReadToEndAsync(ct);
        Event evt;
        try { evt = plans.Stripe.ConstructEvent(json, Request.Headers["Stripe-Signature"].ToString()); }
        catch (Exception e) when (e is StripeException or System.Text.Json.JsonException or ArgumentException) { return BadRequest(); }

        if (!Handled.Contains(evt.Type)) return Ok();
        if (!await plans.ReadyAsync()) return StatusCode(503, new { error = "Not ready." });
        if (!await repo.ClaimEventAsync(evt.Id, evt.Type)) return Ok(); // handled already
        try
        {
            await HandleAsync(evt, ct);
            return Ok();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Let Stripe's retry handle it again.
            await repo.ReleaseEventAsync(evt.Id);
            log.LogError(e, "Stripe event {EventId} ({Type}) failed; Stripe will retry", evt.Id, evt.Type);
            return StatusCode(500);
        }
    }

    private async Task HandleAsync(Event evt, CancellationToken ct)
    {
        switch (evt.Data.Object)
        {
            case Stripe.Checkout.Session session when evt.Type == Stripe.Events.CheckoutSessionCompleted:
            {
                var ours = session.Metadata?.TryGetValue(StripeBilling.AppKey, out var app) == true && app == StripeBilling.AppValue;
                if (!ours || !Guid.TryParse(session.Metadata![StripeBilling.WorkspaceKey], out var wsId)) return;
                if (session.CustomerId is not null && await repo.GetAsync(wsId) is { StripeCustomerId: null }) await repo.SetCustomerAsync(wsId, session.CustomerId);
                if (session.SubscriptionId is not null && await plans.Stripe.SubscriptionAsync(session.SubscriptionId, ct) is { } bought)
                    await plans.ApplyAsync(bought, wsId, ct);
                return;
            }
            case Subscription carried:
            {
                if (plans.Stripe.Read(carried) is not { } view) return; // not Flow's
                if (evt.Type != Stripe.Events.CustomerSubscriptionDeleted)
                {
                    try { view = await plans.Stripe.SubscriptionAsync(carried.Id, ct) ?? view; }
                    catch (StripeException e) { log.LogWarning(e, "Couldn't re-read subscription {SubscriptionId}; applying the event's copy", carried.Id); }
                }
                await plans.ApplyAsync(view, null, ct);
                return;
            }
            case Invoice invoice when evt.Type == Stripe.Events.InvoicePaymentFailed:
                await plans.PaymentFailedAsync(invoice.SubscriptionId, invoice.CustomerId, invoice.Id, invoice.NextPaymentAttempt);
                return;
        }
    }
}
