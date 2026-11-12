using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>A trigger or action the studio offers: its app, its name, the config keys it needs and how to name them.</summary>
public sealed record CatalogEntry(string App, string Key, string Title, IReadOnlyList<string> Required, IReadOnlyDictionary<string, string> Labels)
{
    public string Label(string key) => Labels.TryGetValue(key, out var l) ? l : key;
    /// <summary>What it does and the config it takes, in a sentence or two (for people and for the AI builder).</summary>
    public string? Description { get; init; }
    /// <summary>A paid-plan app or step: plans may gate it (nothing here does).</summary>
    public bool Premium { get; init; }
}

/// <summary>A kind of step that isn't an app's action (a delay, a branch, an approval…), described for the AI builder.</summary>
public sealed record StepKind(string Type, string Title, string Description, bool Premium = false);

/// <summary>
/// What Flow can listen to and do (v1). The studio has the same list with its forms; the server holds the parts
/// that decide whether a flow can run: which config keys are required, and which app each needs connected.
/// </summary>
public static class FlowCatalog
{
    private static CatalogEntry E(string app, string key, string title, params (string Key, string Label)[] required) =>
        new(app, key, title, required.Select(r => r.Key).ToList(), required.ToDictionary(r => r.Key, r => r.Label));

    public static readonly IReadOnlyList<CatalogEntry> Triggers =
    [
        E("orbit", "record.created", "Record created", ("tableId", "a table")),
        E("orbit", "record.updated", "Record updated", ("tableId", "a table")),
        E("orbit", "record.matches", "Record matches a condition", ("tableId", "a table"), ("condition", "a condition")),
        E("projects", "project.created", "Project created"),
        E("projects", "task.created", "Task created"),
        E("projects", "task.completed", "Task completed"),
        E("projects", "client.created", "Client created"),
        E("schedule", "interval", "Every few minutes or hours", ("every", "how often")),
        E("schedule", "daily", "Every day", ("time", "a time")),
        E("schedule", "weekly", "Every week", ("time", "a time"), ("days", "the days")) with
        {
            Description = "On chosen days at a time: config { time: \"HH:mm\", days: [\"mon\"…\"sun\"] }. Weekdays are mon–fri.",
        },
        E("schedule", "monthly", "Every month", ("time", "a time"), ("day", "the day")) with
        {
            Description = "On a day of the month at a time, in the workspace's time zone: config { day: 1–28 or \"last\", time: \"HH:mm\" }.",
        },
        E("webhook", "catch", "Webhook received") with
        {
            Description = "A unique URL for this flow that takes JSON or form posts (optionally signed with X-Flow-Signature). Data: { body, query, headers, receivedAt }.",
        },
        E("flow", "called", "Called by another flow"),
        E("slack", "message", "New message in channel", ("channel", "a channel")),
        E("microsoft", "mail.new", "New email in Outlook"),
        E("google", "gmail.new", "New email in Gmail"),
        E("google", "sheets.row.new", "New row in a sheet", ("spreadsheet", "the spreadsheet"), ("sheet", "the sheet")),
        E("stripe", "payment.new", "New payment") with
        {
            Description = "A payment succeeds in Stripe (checked every couple of minutes). Data: { id, amount, currency, customer, email, name, description, receiptUrl, created, link }.",
            Premium = true,
        },
        E("stripe", "customer.new", "New customer") with
        {
            Description = "A customer is created in Stripe. Data: { id, email, name, phone, description, created, link }.", Premium = true,
        },
        E("stripe", "invoice.paid", "Invoice paid") with
        {
            Description = "A Stripe invoice is paid. Data: { id, number, customer, customerEmail, customerName, amountPaid, currency, hostedUrl, pdf }.", Premium = true,
        },
        E("hubspot", "contact.new", "New contact") with
        {
            Description = "A contact is created in HubSpot. Data: { id, email, firstName, lastName, name, phone, company, created, link }.", Premium = true,
        },
        E("hubspot", "deal.stage", "Deal stage changed") with
        {
            Description = "A HubSpot deal moves stage: config { pipeline?, stage? } (a stage id: only deals entering it). Data: { id, name, amount, stage, previousStage, pipeline, closeDate, link }.",
            Premium = true,
        },
        E("xero", "contact.new", "New contact") with
        {
            Description = "A contact is added in Xero. Data: { id, name, email, firstName, lastName, status, link }.", Premium = true,
        },
        E("xero", "invoice.new", "New invoice") with
        {
            Description = "A sales invoice is added in Xero. Data: { id, number, contactId, contactName, total, amountDue, currency, status, reference, date, dueDate, link }.",
            Premium = true,
        },
    ];

    public static readonly IReadOnlyList<CatalogEntry> Actions =
    [
        E("orbit", "record.create", "Create record", ("tableId", "a table"), ("fields", "at least one field")),
        E("orbit", "record.update", "Update record", ("tableId", "a table"), ("recordId", "the record"), ("fields", "at least one field")),
        E("orbit", "record.find", "Find records", ("tableId", "a table")),
        E("orbit", "record.comment", "Add comment to record", ("tableId", "a table"), ("recordId", "the record"), ("text", "the comment")),
        E("projects", "project.create", "Create project", ("name", "the project's name")),
        E("projects", "task.create", "Create task", ("projectId", "the project"), ("name", "the task's name")),
        E("projects", "task.find", "Find tasks"),
        E("projects", "task.update", "Update task", ("taskId", "the task")),
        E("projects", "task.comment", "Add comment", ("taskId", "the task"), ("body", "the comment")),
        E("email", "send", "Send email", ("to", "who it goes to"), ("subject", "a subject")),
        E("http", "request", "HTTP request", ("url", "the URL")) with
        {
            Description = "Calls a public URL: config { method, url, query: {name: value}, headers: {name: value}, bodyType: json | form | raw, body, form: {name: value}, " +
                "auth: { type: none | bearer | basic | header, secretId, username, header }, errors: fail | continue }. Credentials come from the workspace's saved " +
                "secrets. Output: { status, ok, url, headers, contentType, body (parsed JSON), truncated }.",
        },
        E("flow", "run", "Run another flow", ("flowId", "the flow to run")),
        E("slack", "message.send", "Send channel message", ("channel", "a channel"), ("text", "the message")),
        E("slack", "dm.send", "Send direct message", ("user", "who it goes to"), ("text", "the message")),
        E("microsoft", "mail.send", "Send email (Outlook)", ("to", "who it goes to"), ("subject", "a subject")),
        E("microsoft", "teams.post", "Post to a Teams channel", ("teamId", "a team"), ("channelId", "a channel"), ("text", "the message")),
        E("microsoft", "event.create", "Create calendar event", ("subject", "a title"), ("start", "when it starts")),
        E("google", "gmail.send", "Send email (Gmail)", ("to", "who it goes to"), ("subject", "a subject")),
        E("google", "sheets.row.add", "Add row", ("spreadsheet", "the spreadsheet"), ("sheet", "the sheet"), ("values", "at least one column")),
        E("google", "sheets.row.find", "Find row", ("spreadsheet", "the spreadsheet"), ("sheet", "the sheet"), ("column", "the column to look in")),
        E("google", "sheets.row.update", "Update row", ("spreadsheet", "the spreadsheet"), ("sheet", "the sheet"), ("rowNumber", "the row"), ("values", "at least one column")),
        E("stripe", "customer.create", "Create customer") with
        {
            Description = "Creates a Stripe customer: config { email, name, phone, description }. Output: { id, email, name, link }.", Premium = true,
        },
        E("stripe", "invoiceitem.create", "Create invoice item", ("customer", "the customer"), ("amount", "an amount")) with
        {
            Description = "Adds a line to a customer's next invoice: config { customer (cus_…), amount (e.g. 12.50), currency (gbp), description }. Output: { id, amount, currency }.",
            Premium = true,
        },
        E("stripe", "invoice.send", "Send invoice", ("customer", "the customer")) with
        {
            Description = "Invoices a customer for their pending invoice items and emails it from Stripe: config { customer, daysUntilDue (30), description }. " +
                "Output: { id, number, status, amountDue, currency, hostedUrl, pdf }.",
            Premium = true,
        },
        E("stripe", "customer.find", "Find customer", ("email", "the email")) with
        {
            Description = "Looks up a Stripe customer by email: config { email }. Output: { found, id, email, name, link, customers }.", Premium = true,
        },
        E("hubspot", "contact.upsert", "Create or update contact", ("email", "the email")) with
        {
            Description = "Creates a HubSpot contact, or updates the one with that email: config { email, firstname, lastname, phone, company, properties: {name: value} }. " +
                "Output: { id, email, name, created (true when new), link }.",
            Premium = true,
        },
        E("hubspot", "deal.create", "Create deal", ("dealname", "the deal's name")) with
        {
            Description = "Creates a HubSpot deal: config { dealname, amount, pipeline, dealstage, closedate, contact (an email or contact id to link) }. Output: { id, name, amount, stage, link }.",
            Premium = true,
        },
        E("hubspot", "contact.find", "Find contact", ("email", "the email")) with
        {
            Description = "Looks up a HubSpot contact by email: config { email }. Output: { found, id, email, name, phone, company, link }.", Premium = true,
        },
        E("xero", "contact.create", "Create contact", ("name", "the contact's name")) with
        {
            Description = "Creates a Xero contact: config { name, email, firstName, lastName, phone }. Output: { id, name, email, link }.", Premium = true,
        },
        E("xero", "invoice.create", "Create invoice", ("contact", "the contact"), ("unitAmount", "an amount")) with
        {
            Description = "Creates a Xero sales invoice: config { contact (a ContactID or a name), description, quantity, unitAmount, accountCode, dueDate, reference, " +
                "status: DRAFT | AUTHORISED }. Output: { id, number, total, status, link }.",
            Premium = true,
        },
        E("xero", "contact.find", "Find contact", ("search", "an email or name")) with
        {
            Description = "Looks up a Xero contact by email or name: config { search }. Output: { found, id, name, email, link, contacts }.", Premium = true,
        },
    ];

    /// <summary>The steps that aren't an app's action, with the config each takes (the AI builder reads these).</summary>
    public static readonly IReadOnlyList<StepKind> Steps =
    [
        new(StepTypes.Delay, "Delay", "Waits, then carries on: config { amount, unit: minutes | hours | days } (up to 30 days), or { mode: \"until\", at: a date and time or token, zone }."),
        new(StepTypes.Wait, "Wait until", "Re-checks a condition until it holds or time runs out: config { condition, every (minutes, default 15), amount, unit (the most to wait, " +
            "default 3 days), refresh (an earlier lookup step to run again before each check), onTimeout: continue | stop }. Output: { met, timedOut, checks }."),
        new(StepTypes.Approval, "Approval", "Emails chosen workspace members Approve / Reject links and waits for the first decision: config { title, details, approvers: [member ids], " +
            "amount, unit (how long to wait, default 3 days) }. Three paths, in order: Approved, Rejected, Timed out. Output: { decision, decidedBy, decidedAt, comment }.", Premium: true),
        new(StepTypes.Branch, "If / else", "Two sides on a condition: config { condition }, steps in then and else."),
        new(StepTypes.Paths, "Paths", "Up to 5 paths, each with a condition, tried in order; config { otherwise: true } adds an otherwise path."),
        new(StepTypes.Filter, "Only continue if", "Stops the run (as a success) unless config { condition } holds."),
        new(StepTypes.Loop, "Repeat for each", "Runs its steps once per item of config { items } (a list token), at most 100."),
        new(StepTypes.Formatter, "Format", "Changes one value: text, number, date or lookup (config { kind, op, input, … })."),
        new(StepTypes.Variable, "Set variable", "Sets {{vars.name}}: config { assignments: [{ name, op: set | add | append, value }] }."),
    ];

    public static CatalogEntry? Trigger(string? app, string? key) => Triggers.FirstOrDefault(t => t.App == app && t.Key == key);
    public static CatalogEntry? Action(string? app, string? key) => Actions.FirstOrDefault(a => a.App == app && a.Key == key);

    /// <summary>The apps a definition needs connected (Orbit, Projects), trigger included.</summary>
    public static IReadOnlySet<string> AppsNeeded(FlowDefinition def)
    {
        var apps = new HashSet<string>(StringComparer.Ordinal);
        if (def.Trigger.App is "orbit" or "projects") apps.Add(def.Trigger.App);
        foreach (var s in def.AllSteps())
            if (s.Type == StepTypes.Action && s.App is "orbit" or "projects") apps.Add(s.App);
        return apps;
    }

    /// <summary>The outside apps a definition needs connected (Slack, Microsoft 365, Google), trigger included.</summary>
    public static IReadOnlySet<string> ProvidersNeeded(FlowDefinition def)
    {
        var apps = new HashSet<string>(StringComparer.Ordinal);
        if (OAuth.OAuthProviders.IsProvider(def.Trigger.App)) apps.Add(def.Trigger.App);
        foreach (var s in def.AllSteps())
            if (s.Type == StepTypes.Action && OAuth.OAuthProviders.IsProvider(s.App)) apps.Add(s.App!);
        return apps;
    }

    /// <summary>What a trigger hands the first step when there's no real event to test with.</summary>
    public static JsonObject Sample(TriggerDef t, DateTime now) => (t.App, t.Event) switch
    {
        ("orbit", _) => new JsonObject
        {
            ["event"] = t.Event == "record.matches" ? "record.updated" : t.Event,
            ["table"] = new JsonObject { ["id"] = t.Config["tableId"]?.GetValue<string>() ?? "tbl_sample", ["name"] = "Sample table" },
            ["record"] = new JsonObject
            {
                ["id"] = "rec_sample",
                ["url"] = "https://orbit.vantrasoftware.com/r/rec_sample",
                ["fields"] = new JsonObject { ["Name"] = "Ada Lovelace", ["Email"] = "ada@example.com", ["Stage"] = "Won" },
            },
            ["changes"] = new JsonArray(),
            ["actor"] = new JsonObject { ["type"] = "user", ["name"] = "Sample person" },
        },
        ("projects", "project.created") => new JsonObject
        {
            ["event"] = t.Event,
            ["project"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Website refresh", ["url"] = "https://projects.vantrasoftware.com" },
            ["actor"] = "Sample person",
        },
        ("projects", "client.created") => new JsonObject
        {
            ["event"] = t.Event,
            ["client"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Example Ltd", ["email"] = "hello@example.com" },
            ["actor"] = "Sample person",
        },
        ("projects", _) => new JsonObject
        {
            ["event"] = t.Event,
            ["task"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Send the proposal", ["projectId"] = Guid.Empty },
            ["project"] = new JsonObject { ["id"] = Guid.Empty, ["name"] = "Website refresh" },
            ["actor"] = "Sample person",
        },
        ("schedule", _) => new JsonObject { ["scheduledFor"] = now.ToString("o"), ["timeZone"] = "UTC" },
        ("slack", _) => new JsonObject
        {
            ["text"] = "Can someone look at the Harbour Foods proposal?", ["user"] = "U0SAMPLE", ["channel"] = Tokens.Text(t.Config["channel"]) is { Length: > 0 } ch ? ch : "C0SAMPLE",
            ["ts"] = "1794387600.000100", ["threadTs"] = "", ["isReply"] = false, ["isBot"] = false, ["sentAt"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
        },
        ("microsoft", _) or ("google", "gmail.new") => new JsonObject
        {
            ["id"] = "msg_sample", ["subject"] = "Proposal for Harbour Foods", ["from"] = new JsonObject { ["name"] = "Ada Lovelace", ["email"] = "ada@example.com" },
            ["to"] = "you@example.com", ["receivedAt"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"), ["preview"] = "Hi, here's the proposal we talked about…",
            ["snippet"] = "Hi, here's the proposal we talked about…", ["link"] = "https://example.com/mail/msg_sample",
        },
        ("google", _) => new JsonObject
        {
            ["rowNumber"] = 2, ["sheet"] = Tokens.Text(t.Config["sheet"]),
            ["values"] = new JsonObject { ["Name"] = "Ada Lovelace", ["Email"] = "ada@example.com", ["Company"] = "Analytical Engines" },
        },
        ("stripe", "payment.new") => new JsonObject
        {
            ["id"] = "ch_sample", ["amount"] = 1250.00, ["currency"] = "gbp", ["customer"] = "cus_sample", ["email"] = "ada@example.com", ["name"] = "Ada Lovelace",
            ["description"] = "Annual plan", ["receiptUrl"] = "https://pay.stripe.com/receipts/sample", ["created"] = Formatter.IsoUtc(now),
            ["link"] = "https://dashboard.stripe.com/test/payments/pi_sample",
        },
        ("stripe", "customer.new") => new JsonObject
        {
            ["id"] = "cus_sample", ["email"] = "ada@example.com", ["name"] = "Ada Lovelace", ["phone"] = "", ["description"] = "", ["created"] = Formatter.IsoUtc(now),
            ["link"] = "https://dashboard.stripe.com/test/customers/cus_sample",
        },
        ("stripe", _) => new JsonObject
        {
            ["id"] = "in_sample", ["number"] = "NW-0042", ["status"] = "paid", ["customer"] = "cus_sample", ["customerEmail"] = "ada@example.com",
            ["customerName"] = "Ada Lovelace", ["amountDue"] = 480.00, ["amountPaid"] = 480.00, ["currency"] = "gbp", ["hostedUrl"] = "https://invoice.stripe.com/i/sample",
        },
        ("hubspot", "contact.new") => new JsonObject
        {
            ["id"] = "51", ["email"] = "ada@example.com", ["firstName"] = "Ada", ["lastName"] = "Lovelace", ["name"] = "Ada Lovelace", ["phone"] = "",
            ["company"] = "Analytical Engines", ["created"] = Formatter.IsoUtc(now), ["link"] = "https://app.hubspot.com/contacts/0/record/0-1/51",
        },
        ("hubspot", _) => new JsonObject
        {
            ["id"] = "9001", ["name"] = "Harbour Foods rollout", ["amount"] = 18500, ["stage"] = Tokens.Text(t.Config["stage"]) is { Length: > 0 } st ? st : "contractsent",
            ["previousStage"] = "presentationscheduled", ["pipeline"] = "default", ["closeDate"] = Formatter.IsoUtc(now.AddDays(14)), ["link"] = "https://app.hubspot.com/contacts/0/record/0-3/9001",
        },
        ("xero", "contact.new") => new JsonObject
        {
            ["id"] = "6f1b2c3d-0000-4000-8000-000000000001", ["name"] = "Harbour Foods", ["email"] = "accounts@example.com", ["firstName"] = "Grace", ["lastName"] = "Hopper",
            ["status"] = "ACTIVE", ["link"] = "https://go.xero.com/app/contacts/contact/6f1b2c3d-0000-4000-8000-000000000001",
        },
        ("xero", _) => new JsonObject
        {
            ["id"] = "8a2b3c4d-0000-4000-8000-000000000002", ["number"] = "INV-0107", ["contactName"] = "Harbour Foods", ["total"] = 2400.00, ["amountDue"] = 2400.00,
            ["currency"] = "GBP", ["status"] = "AUTHORISED", ["reference"] = "PO-5521", ["dueDate"] = Formatter.IsoUtc(now.AddDays(30)),
        },
        ("flow", _) => new JsonObject
        {
            ["input"] = new JsonObject { ["name"] = "Ada Lovelace", ["email"] = "ada@example.com" },
            ["caller"] = new JsonObject { ["flowId"] = "flw_sample", ["flowName"] = "Another flow", ["runId"] = "frn_sample", ["depth"] = 1 },
        },
        _ => new JsonObject { ["body"] = new JsonObject { ["hello"] = "world" }, ["headers"] = new JsonObject() },
    };
}
