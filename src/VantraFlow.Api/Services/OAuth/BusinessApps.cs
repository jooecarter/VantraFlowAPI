using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.OAuth;

/// <summary>
/// Stripe's, HubSpot's and Xero's actions on the workspace's connection (<see cref="OAuthConnections.CallAsync{T}"/>):
/// <list type="bullet">
/// <item><b>Stripe</b>: create a customer, add an invoice item, send an invoice (its pending items, finalised and
/// emailed by Stripe), find a customer by email.</item>
/// <item><b>HubSpot</b>: create or update a contact (by email), create a deal (linked to a contact), find a contact.</item>
/// <item><b>Xero</b>: create a contact, create a sales invoice, find a contact.</item>
/// </list>
/// Each hands on flat fields (ids, names, amounts in major units, a link) so later steps can use them as tokens.
/// </summary>
public sealed partial class BusinessActions(OAuthConnections connections)
{
    public Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct) => (call.Step.App, call.Step.Action) switch
    {
        ("stripe", "customer.create") => StripeCustomerAsync(call.WorkspaceId, call.Input, ct),
        ("stripe", "invoiceitem.create") => StripeItemAsync(call.WorkspaceId, call.Input, ct),
        ("stripe", "invoice.send") => StripeInvoiceAsync(call.WorkspaceId, call.Input, ct),
        ("stripe", "customer.find") => StripeFindAsync(call.WorkspaceId, call.Input, ct),
        ("hubspot", "contact.upsert") => HubSpotUpsertAsync(call.WorkspaceId, call.Input, ct),
        ("hubspot", "deal.create") => HubSpotDealAsync(call.WorkspaceId, call.Input, ct),
        ("hubspot", "contact.find") => HubSpotFindAsync(call.WorkspaceId, call.Input, ct),
        ("xero", "contact.create") => XeroContactAsync(call.WorkspaceId, call.Input, ct),
        ("xero", "invoice.create") => XeroInvoiceAsync(call.WorkspaceId, call.Input, ct),
        ("xero", "contact.find") => XeroFindAsync(call.WorkspaceId, call.Input, ct),
        _ => Task.FromResult(StepOutcome.Fail("This step isn't something Vantra Flow can do.")),
    };

    private static string T(JsonObject input, string key) => Tokens.Text(input[key]).Trim();

    // ── Stripe ──

    private Task<JsonNode?> StripeAsync(Guid ws, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>>? form, CancellationToken ct) =>
        connections.CallAsync(ws, "stripe", s => s.Provider.SendAsync(method, StripeProvider.Api + path, s.Token, null, ct,
            form is null ? null : StripeProvider.Form(form)), ct);

    /// <summary>Currencies Stripe counts in whole units (no cents).</summary>
    private static readonly HashSet<string> ZeroDecimal =
        ["bif", "clp", "djf", "gnf", "jpy", "kmf", "krw", "mga", "pyg", "rwf", "ugx", "vnd", "vuv", "xaf", "xof", "xpf"];

    /// <summary>An amount as Stripe takes it: in the currency's smallest unit ("12.50" GBP → 1250).</summary>
    public static long? MinorUnits(string amount, string currency)
    {
        var text = amount.Trim().TrimStart('£', '$', '€').Replace(",", "", StringComparison.Ordinal);
        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) || v <= 0 || v > 99_999_999) return null;
        return (long)Math.Round(ZeroDecimal.Contains(currency.ToLowerInvariant()) ? v : v * 100, MidpointRounding.AwayFromZero);
    }

    /// <summary>A Stripe amount back in major units.</summary>
    public static decimal MajorUnits(JsonNode? minor, string currency) =>
        (Formatter.ToNumber(minor) is { } n ? (decimal)n : 0) / (ZeroDecimal.Contains(currency.ToLowerInvariant()) ? 1 : 100);

    private static string Dashboard(bool live, string path) => $"https://dashboard.stripe.com/{(live ? "" : "test/")}{path}";

    public static JsonObject StripeCustomer(JsonNode? c) => new()
    {
        ["id"] = Tokens.Text(c?["id"]),
        ["email"] = Tokens.Text(c?["email"]),
        ["name"] = Tokens.Text(c?["name"]),
        ["phone"] = Tokens.Text(c?["phone"]),
        ["description"] = Tokens.Text(c?["description"]),
        ["created"] = Unix(c?["created"]),
        ["link"] = Dashboard(c?["livemode"] is JsonValue lv && lv.TryGetValue<bool>(out var live) && live, $"customers/{Tokens.Text(c?["id"])}"),
    };

    private async Task<StepOutcome> StripeCustomerAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var email = T(input, "email");
        if (email.Length > 0 && StepActions.Recipients(email) is { Error: not null } bad) return StepOutcome.Fail(bad.Error);
        var form = new List<KeyValuePair<string, string>>();
        foreach (var (k, field) in new[] { ("email", "email"), ("name", "name"), ("phone", "phone"), ("description", "description") })
            if (T(input, k) is { Length: > 0 } v) form.Add(new(field, v.Length > 500 ? v[..500] : v));
        if (form.Count == 0) return StepOutcome.Fail("Fill in at least an email or a name.");
        return StepOutcome.Done(StripeCustomer(await StripeAsync(ws, HttpMethod.Post, "customers", form, ct)));
    }

    private async Task<StepOutcome> StripeItemAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var customer = T(input, "customer");
        if (!customer.StartsWith("cus_", StringComparison.Ordinal)) return StepOutcome.Fail("The customer must be a Stripe customer id (cus_…), from Find customer or Create customer.");
        var currency = (T(input, "currency") is { Length: 3 } c ? c : "gbp").ToLowerInvariant();
        if (MinorUnits(T(input, "amount"), currency) is not { } amount) return StepOutcome.Fail($"“{T(input, "amount")}” isn't an amount.");
        var form = new List<KeyValuePair<string, string>>
        {
            new("customer", customer), new("amount", amount.ToString(CultureInfo.InvariantCulture)), new("currency", currency),
        };
        if (T(input, "description") is { Length: > 0 } d) form.Add(new("description", d.Length > 500 ? d[..500] : d));
        var res = await StripeAsync(ws, HttpMethod.Post, "invoiceitems", form, ct);
        return StepOutcome.Done(new JsonObject
        {
            ["id"] = Tokens.Text(res?["id"]), ["customer"] = customer, ["amount"] = MajorUnits(res?["amount"], currency), ["currency"] = currency,
            ["description"] = Tokens.Text(res?["description"]),
        });
    }

    private async Task<StepOutcome> StripeInvoiceAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var customer = T(input, "customer");
        if (!customer.StartsWith("cus_", StringComparison.Ordinal)) return StepOutcome.Fail("The customer must be a Stripe customer id (cus_…).");
        var days = Formatter.ToNumber(input["daysUntilDue"]) is { } n && n >= 1 && n <= 365 ? (int)n : 30;
        var form = new List<KeyValuePair<string, string>>
        {
            new("customer", customer), new("collection_method", "send_invoice"), new("days_until_due", days.ToString(CultureInfo.InvariantCulture)),
            new("pending_invoice_items_behavior", "include"), new("auto_advance", "false"),
        };
        if (T(input, "description") is { Length: > 0 } memo) form.Add(new("description", memo.Length > 500 ? memo[..500] : memo));
        var draft = await StripeAsync(ws, HttpMethod.Post, "invoices", form, ct);
        var id = Tokens.Text(draft?["id"]);
        if (Formatter.ToNumber(draft?["amount_due"]) is not > 0) return StepOutcome.Fail("The invoice has nothing on it. Add an invoice item first.");
        await StripeAsync(ws, HttpMethod.Post, $"invoices/{Uri.EscapeDataString(id)}/finalize", [], ct);
        var sent = await StripeAsync(ws, HttpMethod.Post, $"invoices/{Uri.EscapeDataString(id)}/send", [], ct);
        return StepOutcome.Done(StripeInvoice(sent));
    }

    public static JsonObject StripeInvoice(JsonNode? i)
    {
        var currency = Tokens.Text(i?["currency"]);
        return new JsonObject
        {
            ["id"] = Tokens.Text(i?["id"]),
            ["number"] = Tokens.Text(i?["number"]),
            ["status"] = Tokens.Text(i?["status"]),
            ["customer"] = Tokens.Text(i?["customer"]),
            ["customerEmail"] = Tokens.Text(i?["customer_email"]),
            ["customerName"] = Tokens.Text(i?["customer_name"]),
            ["amountDue"] = MajorUnits(i?["amount_due"], currency),
            ["amountPaid"] = MajorUnits(i?["amount_paid"], currency),
            ["currency"] = currency,
            ["dueDate"] = Unix(i?["due_date"]),
            ["hostedUrl"] = Tokens.Text(i?["hosted_invoice_url"]),
            ["pdf"] = Tokens.Text(i?["invoice_pdf"]),
        };
    }

    private async Task<StepOutcome> StripeFindAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var email = T(input, "email");
        if (email.Length == 0) return StepOutcome.Fail("Fill in the email to look for.");
        var res = await StripeAsync(ws, HttpMethod.Get, $"customers?limit=10&email={Uri.EscapeDataString(email)}", null, ct);
        var list = (res?["data"] as JsonArray ?? []).Select(StripeCustomer).ToList();
        var first = list.FirstOrDefault();
        var output = first?.DeepClone() as JsonObject ?? new JsonObject { ["id"] = "", ["email"] = "", ["name"] = "" };
        output["found"] = list.Count > 0;
        output["count"] = list.Count;
        output["customers"] = new JsonArray(list.Select(x => (JsonNode)x).ToArray());
        return StepOutcome.Done(output);
    }

    /// <summary>A Stripe event's object as a trigger's data: a payment, a customer or a paid invoice.</summary>
    public static JsonObject StripeTrigger(string kind, JsonNode? o) => kind switch
    {
        "payment.new" => StripePayment(o),
        "customer.new" => StripeCustomer(o),
        _ => StripeInvoice(o),
    };

    public static JsonObject StripePayment(JsonNode? c)
    {
        var currency = Tokens.Text(c?["currency"]);
        var live = c?["livemode"] is JsonValue lv && lv.TryGetValue<bool>(out var l) && l;
        return new JsonObject
        {
            ["id"] = Tokens.Text(c?["id"]),
            ["amount"] = MajorUnits(c?["amount"], currency),
            ["currency"] = currency,
            ["customer"] = Tokens.Text(c?["customer"]),
            ["email"] = Tokens.Text(c?["billing_details"]?["email"]) is { Length: > 0 } e ? e : Tokens.Text(c?["receipt_email"]),
            ["name"] = Tokens.Text(c?["billing_details"]?["name"]),
            ["description"] = Tokens.Text(c?["description"]),
            ["receiptUrl"] = Tokens.Text(c?["receipt_url"]),
            ["created"] = Unix(c?["created"]),
            ["link"] = Dashboard(live, "payments/" + (Tokens.Text(c?["payment_intent"]) is { Length: > 0 } pi ? pi : Tokens.Text(c?["id"]))),
        };
    }

    private static string Unix(JsonNode? n) =>
        Formatter.ToNumber(n) is { } s && s > 0 ? Formatter.IsoUtc(DateTimeOffset.FromUnixTimeSeconds((long)s).UtcDateTime) : "";

    // ── HubSpot ──

    private Task<JsonNode?> HubSpotAsync(Guid ws, HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        connections.CallAsync(ws, "hubspot", s => s.Provider.SendAsync(method, HubSpotProvider.Api + path, s.Token, body, ct), ct);

    public static readonly string[] ContactProps = ["email", "firstname", "lastname", "phone", "company", "createdate", "lifecyclestage"];
    public static readonly string[] DealProps = ["dealname", "amount", "dealstage", "pipeline", "closedate", "createdate", "hs_lastmodifieddate"];

    public static JsonObject HubSpotContact(JsonNode? c, string? portal)
    {
        var p = c?["properties"];
        var id = Tokens.Text(c?["id"]);
        return new JsonObject
        {
            ["id"] = id,
            ["email"] = Tokens.Text(p?["email"]),
            ["firstName"] = Tokens.Text(p?["firstname"]),
            ["lastName"] = Tokens.Text(p?["lastname"]),
            ["name"] = $"{Tokens.Text(p?["firstname"])} {Tokens.Text(p?["lastname"])}".Trim(),
            ["phone"] = Tokens.Text(p?["phone"]),
            ["company"] = Tokens.Text(p?["company"]),
            ["created"] = Tokens.Text(p?["createdate"]),
            ["link"] = portal is { Length: > 0 } && id.Length > 0 ? $"https://app.hubspot.com/contacts/{portal}/record/0-1/{id}" : "",
        };
    }

    public static JsonObject HubSpotDeal(JsonNode? d, string? portal)
    {
        var p = d?["properties"];
        var id = Tokens.Text(d?["id"]);
        return new JsonObject
        {
            ["id"] = id,
            ["name"] = Tokens.Text(p?["dealname"]),
            ["amount"] = Formatter.ToNumber(p?["amount"]) is { } a ? a : null,
            ["stage"] = Tokens.Text(p?["dealstage"]),
            ["pipeline"] = Tokens.Text(p?["pipeline"]),
            ["closeDate"] = Tokens.Text(p?["closedate"]),
            ["link"] = portal is { Length: > 0 } && id.Length > 0 ? $"https://app.hubspot.com/contacts/{portal}/record/0-3/{id}" : "",
        };
    }

    private async Task<(JsonNode? Found, string? Portal)> HubSpotByEmailAsync(Guid ws, string email, CancellationToken ct)
    {
        var portal = "";
        var res = await connections.CallAsync(ws, "hubspot", async s =>
        {
            portal = s.Row.AccountId;
            return await s.Provider.SendAsync(HttpMethod.Post, HubSpotProvider.Api + "crm/v3/objects/contacts/search", s.Token, new JsonObject
            {
                ["filterGroups"] = new JsonArray(new JsonObject
                {
                    ["filters"] = new JsonArray(new JsonObject { ["propertyName"] = "email", ["operator"] = "EQ", ["value"] = email }),
                }),
                ["properties"] = new JsonArray(ContactProps.Select(x => (JsonNode)x).ToArray()),
                ["limit"] = 1,
            }, ct);
        }, ct);
        return ((res?["results"] as JsonArray)?.FirstOrDefault(), portal);
    }

    private async Task<StepOutcome> HubSpotUpsertAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var email = T(input, "email");
        if (StepActions.Recipients(email) is { Error: not null } bad) return StepOutcome.Fail(bad.Error);
        var props = new JsonObject { ["email"] = email };
        foreach (var k in new[] { "firstname", "lastname", "phone", "company" })
            if (T(input, k) is { Length: > 0 } v) props[k] = v;
        if (input["properties"] is JsonObject more)
            foreach (var (k, v) in more)
                if (Regex.IsMatch(k, "^[a-z0-9_]{1,100}$") && Tokens.Text(v) is { Length: > 0 } text) props[k] = text;
        var portal = "";
        var res = await connections.CallAsync(ws, "hubspot", async s =>
        {
            portal = s.Row.AccountId;
            return await s.Provider.SendAsync(HttpMethod.Post, HubSpotProvider.Api + "crm/v3/objects/contacts/batch/upsert", s.Token, new JsonObject
            {
                ["inputs"] = new JsonArray(new JsonObject { ["idProperty"] = "email", ["id"] = email, ["properties"] = props }),
            }, ct);
        }, ct);
        var first = (res?["results"] as JsonArray)?.FirstOrDefault();
        var output = HubSpotContact(first, portal);
        if (Tokens.Text(output["email"]).Length == 0) output["email"] = email;
        output["created"] = first?["new"] is JsonValue nv && nv.TryGetValue<bool>(out var isNew) && isNew;
        return StepOutcome.Done(output);
    }

    private async Task<StepOutcome> HubSpotDealAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var name = T(input, "dealname");
        if (name.Length == 0) return StepOutcome.Fail("Name the deal.");
        var props = new JsonObject { ["dealname"] = name.Length > 250 ? name[..250] : name };
        if (T(input, "amount") is { Length: > 0 } amount)
        {
            if (Formatter.ToNumber(JsonValue.Create(amount.Replace(",", ""))) is not { } a) return StepOutcome.Fail($"“{amount}” isn't an amount.");
            props["amount"] = a.ToString(CultureInfo.InvariantCulture);
        }
        if (T(input, "pipeline") is { Length: > 0 } pipeline) props["pipeline"] = pipeline;
        if (T(input, "dealstage") is { Length: > 0 } stage) props["dealstage"] = stage;
        if (T(input, "closedate") is { Length: > 0 } close)
        {
            if (Formatter.ParseDate(close, "auto", TimeZoneInfo.Utc) is not { } at) return StepOutcome.Fail($"“{close}” isn't a date.");
            props["closedate"] = Formatter.IsoUtc(at);
        }
        var contact = T(input, "contact");
        if (contact.Contains('@'))
        {
            var (found, _) = await HubSpotByEmailAsync(ws, contact, ct);
            if (found is null) return StepOutcome.Fail($"HubSpot has no contact with the email {contact}.");
            contact = Tokens.Text(found["id"]);
        }
        var body = new JsonObject { ["properties"] = props };
        if (contact.Length > 0)
        {
            if (!Regex.IsMatch(contact, "^[0-9]{1,20}$")) return StepOutcome.Fail("The contact must be an email or a HubSpot contact id.");
            body["associations"] = new JsonArray(new JsonObject
            {
                ["to"] = new JsonObject { ["id"] = contact },
                ["types"] = new JsonArray(new JsonObject { ["associationCategory"] = "HUBSPOT_DEFINED", ["associationTypeId"] = 3 }),
            });
        }
        var portal = "";
        var res = await connections.CallAsync(ws, "hubspot", async s =>
        {
            portal = s.Row.AccountId;
            return await s.Provider.SendAsync(HttpMethod.Post, HubSpotProvider.Api + "crm/v3/objects/deals", s.Token, body, ct);
        }, ct);
        var output = HubSpotDeal(res, portal);
        output["contactId"] = contact;
        return StepOutcome.Done(output);
    }

    private async Task<StepOutcome> HubSpotFindAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var email = T(input, "email");
        if (email.Length == 0) return StepOutcome.Fail("Fill in the email to look for.");
        var (found, portal) = await HubSpotByEmailAsync(ws, email, ct);
        var output = found is null ? new JsonObject { ["id"] = "", ["email"] = "", ["name"] = "" } : HubSpotContact(found, portal);
        output["found"] = found is not null;
        return StepOutcome.Done(output);
    }

    /// <summary>The deal pipelines and their stages, for the pickers.</summary>
    public async Task<JsonNode> HubSpotPipelinesAsync(Guid ws, CancellationToken ct)
    {
        var res = await HubSpotAsync(ws, HttpMethod.Get, "crm/v3/pipelines/deals", null, ct);
        return new JsonArray((res?["results"] as JsonArray ?? []).Select(p => (JsonNode)new JsonObject
        {
            ["id"] = Tokens.Text(p?["id"]),
            ["name"] = Tokens.Text(p?["label"]),
            ["stages"] = new JsonArray((p?["stages"] as JsonArray ?? []).Select(st => (JsonNode)new JsonObject
            {
                ["id"] = Tokens.Text(st?["id"]), ["name"] = Tokens.Text(st?["label"]),
            }).ToArray()),
        }).ToArray());
    }

    // ── Xero ──

    private Task<JsonNode?> XeroAsync(Guid ws, HttpMethod method, string path, JsonNode? body, CancellationToken ct) =>
        connections.CallAsync(ws, "xero", s => s.Provider.SendAsync(method, XeroProvider.Api + path, s.Token, body, ct, headers: XeroProvider.Tenant(s.Row.TeamId)), ct);

    public static JsonObject XeroContact(JsonNode? c) => new()
    {
        ["id"] = Tokens.Text(c?["ContactID"]),
        ["name"] = Tokens.Text(c?["Name"]),
        ["email"] = Tokens.Text(c?["EmailAddress"]),
        ["firstName"] = Tokens.Text(c?["FirstName"]),
        ["lastName"] = Tokens.Text(c?["LastName"]),
        ["status"] = Tokens.Text(c?["ContactStatus"]),
        ["link"] = Tokens.Text(c?["ContactID"]) is { Length: > 0 } id ? $"https://go.xero.com/app/contacts/contact/{id}" : "",
    };

    public static JsonObject XeroInvoice(JsonNode? i) => new()
    {
        ["id"] = Tokens.Text(i?["InvoiceID"]),
        ["number"] = Tokens.Text(i?["InvoiceNumber"]),
        ["contactId"] = Tokens.Text(i?["Contact"]?["ContactID"]),
        ["contactName"] = Tokens.Text(i?["Contact"]?["Name"]),
        ["total"] = Formatter.ToNumber(i?["Total"]) is { } t ? t : null,
        ["amountDue"] = Formatter.ToNumber(i?["AmountDue"]) is { } d ? d : null,
        ["currency"] = Tokens.Text(i?["CurrencyCode"]),
        ["status"] = Tokens.Text(i?["Status"]),
        ["reference"] = Tokens.Text(i?["Reference"]),
        ["date"] = XeroDate(i?["DateString"] ?? i?["Date"]),
        ["dueDate"] = XeroDate(i?["DueDateString"] ?? i?["DueDate"]),
        ["link"] = Tokens.Text(i?["InvoiceID"]) is { Length: > 0 } id ? $"https://go.xero.com/AccountsReceivable/View.aspx?InvoiceID={id}" : "",
    };

    /// <summary>Xero's dates: <c>/Date(1573755038314+0000)/</c> or an ISO date, as an ISO date.</summary>
    public static string XeroDate(JsonNode? n)
    {
        var s = Tokens.Text(n);
        if (XeroDateRe().Match(s) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var ms))
            return Formatter.IsoUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime);
        return s;
    }

    [GeneratedRegex(@"/Date\((-?\d+)(?:[+-]\d{4})?\)/")]
    private static partial Regex XeroDateRe();

    private async Task<StepOutcome> XeroContactAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var name = T(input, "name");
        if (name.Length == 0) return StepOutcome.Fail("Name the contact.");
        var c = new JsonObject { ["Name"] = name.Length > 255 ? name[..255] : name };
        if (T(input, "email") is { Length: > 0 } email)
        {
            if (StepActions.Recipients(email) is { Error: not null } bad) return StepOutcome.Fail(bad.Error);
            c["EmailAddress"] = email;
        }
        if (T(input, "firstName") is { Length: > 0 } f) c["FirstName"] = f;
        if (T(input, "lastName") is { Length: > 0 } l) c["LastName"] = l;
        if (T(input, "phone") is { Length: > 0 } p) c["Phones"] = new JsonArray(new JsonObject { ["PhoneType"] = "DEFAULT", ["PhoneNumber"] = p });
        var res = await XeroAsync(ws, HttpMethod.Post, "Contacts", new JsonObject { ["Contacts"] = new JsonArray(c) }, ct);
        return StepOutcome.Done(XeroContact((res?["Contacts"] as JsonArray)?.FirstOrDefault()));
    }

    private async Task<StepOutcome> XeroInvoiceAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var contact = T(input, "contact");
        if (contact.Length == 0) return StepOutcome.Fail("Choose the contact: a Xero contact id, or a name.");
        if (Formatter.ToNumber(JsonValue.Create(T(input, "unitAmount").Replace(",", ""))) is not { } amount || amount <= 0)
            return StepOutcome.Fail($"“{T(input, "unitAmount")}” isn't an amount.");
        var quantity = Formatter.ToNumber(input["quantity"]) is { } q && q > 0 ? q : 1;
        var line = new JsonObject
        {
            ["Description"] = T(input, "description") is { Length: > 0 } d ? (d.Length > 4000 ? d[..4000] : d) : "Item",
            ["Quantity"] = quantity, ["UnitAmount"] = amount,
        };
        if (T(input, "accountCode") is { Length: > 0 } code) line["AccountCode"] = code;
        var invoice = new JsonObject
        {
            ["Type"] = "ACCREC",
            ["Contact"] = Guid.TryParse(contact, out _) ? new JsonObject { ["ContactID"] = contact } : new JsonObject { ["Name"] = contact },
            ["LineItems"] = new JsonArray(line),
            ["Status"] = T(input, "status") == "AUTHORISED" ? "AUTHORISED" : "DRAFT",
            ["Date"] = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        };
        var dueText = T(input, "dueDate");
        if (dueText.Length > 0)
        {
            if (Formatter.ParseDate(dueText, "auto", TimeZoneInfo.Utc) is not { } due) return StepOutcome.Fail($"“{dueText}” isn't a date.");
            invoice["DueDate"] = due.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else invoice["DueDate"] = DateTime.UtcNow.AddDays(30).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (T(input, "reference") is { Length: > 0 } r) invoice["Reference"] = r.Length > 255 ? r[..255] : r;
        var res = await XeroAsync(ws, HttpMethod.Post, "Invoices", new JsonObject { ["Invoices"] = new JsonArray(invoice) }, ct);
        return StepOutcome.Done(XeroInvoice((res?["Invoices"] as JsonArray)?.FirstOrDefault()));
    }

    private async Task<StepOutcome> XeroFindAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var term = T(input, "search");
        if (term.Length == 0) return StepOutcome.Fail("Fill in an email or a name to look for.");
        var res = await XeroAsync(ws, HttpMethod.Get, $"Contacts?summaryOnly=true&searchTerm={Uri.EscapeDataString(term)}", null, ct);
        var list = (res?["Contacts"] as JsonArray ?? []).Take(25).Select(XeroContact).ToList();
        var first = list.FirstOrDefault(c => string.Equals(Tokens.Text(c["email"]), term, StringComparison.OrdinalIgnoreCase)) ?? list.FirstOrDefault();
        var output = first?.DeepClone() as JsonObject ?? new JsonObject { ["id"] = "", ["name"] = "", ["email"] = "" };
        output["found"] = list.Count > 0;
        output["count"] = list.Count;
        output["contacts"] = new JsonArray(list.Select(x => (JsonNode)x).ToArray());
        return StepOutcome.Done(output);
    }
}

/// <summary>
/// Stripe's, HubSpot's and Xero's polling triggers (on <see cref="PollTriggers"/>' tick, each flow with its own cursor):
/// <list type="bullet">
/// <item><b>Stripe</b>: a new payment (<c>charge.succeeded</c>), a new customer (<c>customer.created</c>), an invoice paid
/// (<c>invoice.paid</c>), read from Stripe's event list since the last one seen.</item>
/// <item><b>HubSpot</b>: a new contact (by its created date); a deal entering a stage (with a stage chosen: the stage's
/// own "date entered"; without one: any change of stage, against the stages it last saw).</item>
/// <item><b>Xero</b>: a new contact, a new sales invoice (what changed since the last poll and hasn't been seen before;
/// the first poll notes what's already there).</item>
/// </list>
/// The first poll starts from now: what's already there never starts a flow.
/// </summary>
public sealed partial class BusinessPolls(OAuthConnections connections)
{
    public const int MaxKnown = 5000;
    public const int MaxStages = 3000;

    public Task<PollResult> PollAsync(Guid ws, TriggerDef t, string? bookmark, CancellationToken ct) => t.App switch
    {
        "stripe" => StripeAsync(ws, t.Event, bookmark, ct),
        "hubspot" when t.Event == "contact.new" => HubSpotContactsAsync(ws, bookmark, ct),
        "hubspot" => HubSpotDealsAsync(ws, t.Config, bookmark, ct),
        "xero" => XeroAsync(ws, t.Event, bookmark, ct),
        _ => throw new AppRefusedException(400, "This trigger doesn't poll.", "no_poll"),
    };

    // ── Stripe ──

    private sealed record StripeMark(long Since);

    public static string StripeEventType(string trigger) => trigger switch
    {
        "payment.new" => "charge.succeeded",
        "customer.new" => "customer.created",
        _ => "invoice.paid",
    };

    private async Task<PollResult> StripeAsync(Guid ws, string kind, string? bookmark, CancellationToken ct)
    {
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<StripeMark>(bookmark);
        if (mark is null) return new PollResult([], JsonSerializer.Serialize(new StripeMark(DateTimeOffset.UtcNow.ToUnixTimeSeconds())));
        var url = $"{StripeProvider.Api}events?limit=100&type={StripeEventType(kind)}&created[gte]={mark.Since}";
        var res = await connections.CallAsync(ws, "stripe", s => s.Provider.SendAsync(HttpMethod.Get, url, s.Token, null, ct), ct);
        var events = (res?["data"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(e => (Id: Tokens.Text(e["id"]), Created: (long)(Formatter.ToNumber(e["created"]) ?? 0), Obj: e["data"]?["object"]))
            .Where(e => e.Id.Length > 0).OrderBy(e => e.Created).Take(PollTriggers.MaxItems).ToList();
        var items = events.Select(e => (e.Id, BusinessActions.StripeTrigger(kind, e.Obj))).ToList();
        // Same-second events come back next time too; their ids keep them from starting a flow twice.
        var since = events.Count > 0 ? Math.Max(mark.Since, events[^1].Created) : mark.Since;
        return new PollResult(items, JsonSerializer.Serialize(new StripeMark(since)));
    }

    // ── HubSpot ──

    private sealed record HubSpotMark(long Since, Dictionary<string, string>? Stages = null, long Started = 0);

    private async Task<(JsonNode? Res, string Portal)> SearchAsync(Guid ws, string objects, JsonObject body, CancellationToken ct)
    {
        var portal = "";
        var res = await connections.CallAsync(ws, "hubspot", async s =>
        {
            portal = s.Row.AccountId;
            return await s.Provider.SendAsync(HttpMethod.Post, $"{HubSpotProvider.Api}crm/v3/objects/{objects}/search", s.Token, body, ct);
        }, ct);
        return (res, portal);
    }

    private static JsonObject Search(string property, string op, string value, string sortBy, string direction, string[] props, int limit, JsonObject[]? more = null)
    {
        var filters = new JsonArray(new JsonObject { ["propertyName"] = property, ["operator"] = op, ["value"] = value });
        foreach (var f in more ?? []) filters.Add(f);
        return new JsonObject
        {
            ["filterGroups"] = new JsonArray(new JsonObject { ["filters"] = filters }),
            ["sorts"] = new JsonArray(new JsonObject { ["propertyName"] = sortBy, ["direction"] = direction }),
            ["properties"] = new JsonArray(props.Select(x => (JsonNode)x).ToArray()),
            ["limit"] = limit,
        };
    }

    private static long Ms(JsonNode? n)
    {
        var s = Tokens.Text(n);
        if (long.TryParse(s, out var ms)) return ms;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? new DateTimeOffset(DateTime.SpecifyKind(d, DateTimeKind.Utc)).ToUnixTimeMilliseconds() : 0;
    }

    private async Task<PollResult> HubSpotContactsAsync(Guid ws, string? bookmark, CancellationToken ct)
    {
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<HubSpotMark>(bookmark);
        if (mark is null) return new PollResult([], JsonSerializer.Serialize(new HubSpotMark(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())));
        var (res, portal) = await SearchAsync(ws, "contacts",
            Search("createdate", "GT", mark.Since.ToString(CultureInfo.InvariantCulture), "createdate", "ASCENDING", BusinessActions.ContactProps, PollTriggers.MaxItems), ct);
        var found = (res?["results"] as JsonArray ?? []).ToList();
        var items = found.Select(c => ($"c:{Tokens.Text(c?["id"])}", BusinessActions.HubSpotContact(c, portal))).ToList();
        var since = found.Count > 0 ? Math.Max(mark.Since, found.Max(c => Ms(c?["properties"]?["createdate"]))) : mark.Since;
        return new PollResult(items, JsonSerializer.Serialize(new HubSpotMark(since)));
    }

    private async Task<PollResult> HubSpotDealsAsync(Guid ws, JsonObject config, string? bookmark, CancellationToken ct)
    {
        var stage = Tokens.Text(config["stage"]).Trim();
        var pipeline = Tokens.Text(config["pipeline"]).Trim();
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<HubSpotMark>(bookmark);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inPipeline = pipeline.Length > 0 ? new[] { new JsonObject { ["propertyName"] = "pipeline", ["operator"] = "EQ", ["value"] = pipeline } } : null;

        if (stage.Length > 0)
        {
            // A chosen stage: the deals whose "date entered" that stage is after the last one seen.
            var entered = $"hs_date_entered_{stage}";
            if (mark is null) return new PollResult([], JsonSerializer.Serialize(new HubSpotMark(now)));
            var (res, portal) = await SearchAsync(ws, "deals",
                Search(entered, "GT", mark.Since.ToString(CultureInfo.InvariantCulture), entered, "ASCENDING", [.. BusinessActions.DealProps, entered], PollTriggers.MaxItems, inPipeline), ct);
            var found = (res?["results"] as JsonArray ?? []).ToList();
            var items = found.Select(d =>
            {
                var o = BusinessActions.HubSpotDeal(d, portal);
                o["enteredAt"] = Ms(d?["properties"]?[entered]) is var ms && ms > 0 ? Formatter.IsoUtc(DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime) : "";
                return ($"d:{Tokens.Text(d?["id"])}:{stage}:{Tokens.Text(d?["properties"]?[entered])}", o);
            }).ToList();
            var since = found.Count > 0 ? Math.Max(mark.Since, found.Max(d => Ms(d?["properties"]?[entered]))) : mark.Since;
            return new PollResult(items, JsonSerializer.Serialize(new HubSpotMark(since)));
        }

        // Any stage: the deals changed since the last poll whose stage isn't the one it last saw.
        if (mark is null)
        {
            var stages = new Dictionary<string, string>(StringComparer.Ordinal);
            var (res, _) = await SearchAsync(ws, "deals", Search("hs_lastmodifieddate", "GT", "0", "hs_lastmodifieddate", "DESCENDING", BusinessActions.DealProps, 100, inPipeline), ct);
            foreach (var d in res?["results"] as JsonArray ?? []) stages[Tokens.Text(d?["id"])] = Tokens.Text(d?["properties"]?["dealstage"]);
            return new PollResult([], JsonSerializer.Serialize(new HubSpotMark(now, stages, now)));
        }
        var seen = mark.Stages ?? new Dictionary<string, string>(StringComparer.Ordinal);
        var (changed, portal2) = await SearchAsync(ws, "deals",
            Search("hs_lastmodifieddate", "GT", mark.Since.ToString(CultureInfo.InvariantCulture), "hs_lastmodifieddate", "ASCENDING", BusinessActions.DealProps, 100, inPipeline), ct);
        var list = (changed?["results"] as JsonArray ?? []).ToList();
        var moved = new List<(string, JsonObject)>();
        foreach (var d in list)
        {
            var id = Tokens.Text(d?["id"]);
            var now2 = Tokens.Text(d?["properties"]?["dealstage"]);
            var was = seen.GetValueOrDefault(id);
            var isNew = Ms(d?["properties"]?["createdate"]) > mark.Started;
            seen[id] = now2;
            if ((was is not null && was != now2) || (was is null && isNew))
            {
                if (moved.Count >= PollTriggers.MaxItems) continue;
                var o = BusinessActions.HubSpotDeal(d, portal2);
                o["previousStage"] = was ?? "";
                moved.Add(($"d:{id}:{now2}:{Tokens.Text(d?["properties"]?["hs_lastmodifieddate"])}", o));
            }
        }
        if (seen.Count > MaxStages) seen = seen.Skip(seen.Count - MaxStages).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        var next = list.Count > 0 ? Math.Max(mark.Since, list.Max(d => Ms(d?["properties"]?["hs_lastmodifieddate"]))) : mark.Since;
        return new PollResult(moved, JsonSerializer.Serialize(new HubSpotMark(next, seen, mark.Started)));
    }

    // ── Xero ──

    private sealed record XeroMark(string Since, List<string> Known);

    /// <summary>A short, stable stand-in for an id in the list of those already seen.</summary>
    public static string Short(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..10];

    private async Task<PollResult> XeroAsync(Guid ws, string kind, string? bookmark, CancellationToken ct)
    {
        var contacts = kind == "contact.new";
        var path = contacts ? "Contacts?summaryOnly=true" : "Invoices?where=" + Uri.EscapeDataString("Type==\"ACCREC\"") + "&summaryOnly=true";
        var key = contacts ? "Contacts" : "Invoices";
        var idKey = contacts ? "ContactID" : "InvoiceID";
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<XeroMark>(bookmark);
        var now = DateTime.UtcNow;
        async Task<List<JsonNode?>> PageAsync(string order, int page, DateTime? since)
        {
            var url = $"{XeroProvider.Api}{path}&order={Uri.EscapeDataString(order)}&page={page}";
            var res = await connections.CallAsync(ws, "xero", s =>
            {
                var headers = new Dictionary<string, string>(XeroProvider.Tenant(s.Row.TeamId));
                if (since is { } d) headers["If-Modified-Since"] = d.ToString("R", CultureInfo.InvariantCulture);
                return s.Provider.SendAsync(HttpMethod.Get, url, s.Token, null, ct, headers: headers);
            }, ct);
            return (res?[key] as JsonArray ?? []).ToList();
        }
        if (mark is null)
        {
            // The first poll notes what's there, the most recently changed first.
            var known = new List<string>();
            for (var page = 1; page <= 10; page++)
            {
                var list = await PageAsync("UpdatedDateUTC DESC", page, null);
                known.AddRange(list.Select(x => Short(Tokens.Text(x?[idKey]))));
                if (list.Count < 100) break;
            }
            return new PollResult([], JsonSerializer.Serialize(new XeroMark(Formatter.IsoUtc(now), known)));
        }
        var seen = new HashSet<string>(mark.Known, StringComparer.Ordinal);
        var since = DateTime.Parse(mark.Since, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        var items = new List<(string, JsonObject)>();
        var latest = since;
        for (var page = 1; page <= 3; page++)
        {
            var list = await PageAsync("UpdatedDateUTC ASC", page, since.AddMinutes(-1));
            foreach (var x in list)
            {
                var id = Tokens.Text(x?[idKey]);
                if (id.Length == 0) continue;
                if (DateTime.TryParse(BusinessActions.XeroDate(x?["UpdatedDateUTC"]), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var updated)
                    && updated > latest) latest = updated;
                if (!seen.Add(Short(id)) || items.Count >= PollTriggers.MaxItems) continue;
                items.Add(($"{kind}:{id}", contacts ? BusinessActions.XeroContact(x) : BusinessActions.XeroInvoice(x)));
            }
            if (list.Count < 100) break;
        }
        var keep = seen.Count > MaxKnown ? mark.Known.Concat(items.Select(i => Short(i.Item1[(kind.Length + 1)..]))).TakeLast(MaxKnown).ToList() : seen.ToList();
        return new PollResult(items, JsonSerializer.Serialize(new XeroMark(Formatter.IsoUtc(latest), keep)));
    }
}
