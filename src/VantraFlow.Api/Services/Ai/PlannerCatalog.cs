using System.Text;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Ai;

/// <summary>
/// The catalog as the AI planner is told it, summarised from <see cref="FlowCatalog"/> itself: every trigger and action
/// the server knows, with the config keys it requires. So a trigger or action added there is offered with no change
/// here. <see cref="Hints"/> adds what the catalog doesn't hold (optional keys, what a step hands on) for the ones that
/// have them; one without hints is still offered by its required keys.
/// </summary>
public static class PlannerCatalog
{
    /// <summary>Optional config keys (with a word on each) and the outputs later steps read, by <c>app/key</c>.</summary>
    public sealed record Hint(string[] Optional, string[] Outputs);

    private static Hint H(string[] optional, params string[] outputs) => new(optional, outputs);

    public static readonly IReadOnlyDictionary<string, Hint> Hints = new Dictionary<string, Hint>(StringComparer.Ordinal)
    {
        // Triggers: optional keys only (what they hand on comes from the catalog's sample data).
        ["trigger:orbit/record.updated"] = H(["fieldIds: list of field ids; only when these change (leave out for any change)"]),
        ["trigger:projects/task.created"] = H(["projectId: one project's id (leave out for every project)"]),
        ["trigger:projects/task.completed"] = H(["projectId: one project's id (leave out for every project)"]),
        ["trigger:schedule/interval"] = H(["unit: minutes | hours"]),
        ["trigger:slack/message"] = H(["replies: true to include thread replies", "bots: true to include other apps' messages"]),
        ["trigger:schedule/monthly"] = H(["day: 1–31 or \"last\" (default the 1st)"]),
        ["trigger:hubspot/deal.stage"] = H(["pipeline: a pipeline id (leave out for every pipeline)",
            "stage: a stage id, e.g. closedwon (leave out for any change of stage)"]),

        ["orbit/record.create"] = H([], "id", "url"),
        ["orbit/record.update"] = H([], "id", "url"),
        ["orbit/record.find"] = H(["matchField: a field name to match on", "matchValue: the value it equals (text or a token)",
            "updatedWithinHours: number", "limit: number (default 10)"], "count", "names", "first.id", "first.url", "records (a list)"),
        ["orbit/record.comment"] = H([], "id", "url"),
        ["projects/project.create"] = H(["description", "dueDate: yyyy-MM-dd or a token"], "id", "name", "url"),
        ["projects/task.create"] = H(["description", "priority: Low | Medium | High | Urgent", "dueDate: yyyy-MM-dd or a token"], "id", "name", "url"),
        ["projects/task.find"] = H(["projectId", "q: name contains", "status", "assignee: an email", "open: \"true\" for open tasks only",
            "dueWithinDays: number", "limit: number"], "count", "names", "first.id", "first.name", "tasks (a list)"),
        ["projects/task.update"] = H(["status: a column name, e.g. In Review", "assignee: an email", "dueDate: yyyy-MM-dd"], "id", "name", "status", "url"),
        ["projects/task.comment"] = H([], "id", "taskId"),
        ["email/send"] = H(["body: the message (plain text)", "replyTo: an email"], "to", "subject"),
        ["http/request"] = H(["method: POST | GET | PUT | PATCH | DELETE (default POST)", "query: object of parameter name to value",
            "headers: object of header name to value (never put secrets here: leave them for the person to add)",
            "bodyType: json | form | raw (default json)", "body: text (JSON for json)", "form: object of field name to value (for form)",
            "auth: object { type: none | bearer | basic | header, header: the header name for header } (leave secretId out: the person picks a saved secret)",
            "errors: fail | continue (continue hands on a 4xx/5xx response instead of failing)"], "status", "ok", "body", "headers"),
        ["flow/run"] = H(["input: object of name to value (text or tokens)"], "flowName", "eventId"),
        ["slack/message.send"] = H([], "ts", "channel"),
        ["slack/dm.send"] = H([], "ts", "user"),
        ["microsoft/mail.send"] = H(["cc", "body"], "to", "subject"),
        ["microsoft/teams.post"] = H([], "id", "link"),
        ["microsoft/event.create"] = H(["end: yyyy-MM-dd HH:mm", "attendees: emails, comma separated", "location", "body"], "id", "link", "start", "end"),
        ["google/gmail.send"] = H(["cc", "body"], "id", "threadId", "subject"),
        ["google/sheets.row.add"] = H([], "rowNumber", "values"),
        ["google/sheets.row.find"] = H(["value: the value the column equals"], "found", "rowNumber", "values", "count", "rows (a list)"),
        ["google/sheets.row.update"] = H([], "rowNumber", "values"),
        ["stripe/customer.create"] = H(["email", "name", "phone", "description"], "id", "email", "name", "link"),
        ["stripe/invoiceitem.create"] = H(["currency: gbp | usd | eur | aud | cad (default gbp)", "description"], "id", "amount", "currency"),
        ["stripe/invoice.send"] = H(["daysUntilDue: number (default 30)", "description: the invoice's memo"], "id", "number", "amountDue", "hostedUrl", "pdf"),
        ["stripe/customer.find"] = H([], "found", "id", "email", "name", "link", "customers (a list)"),
        ["hubspot/contact.upsert"] = H(["firstname", "lastname", "company", "phone", "properties: object of HubSpot property name to value"],
            "id", "email", "created", "link"),
        ["hubspot/deal.create"] = H(["amount: a number", "pipeline: a pipeline id (default the default pipeline)", "dealstage: a stage id, e.g. appointmentscheduled",
            "closedate: yyyy-MM-dd or a token", "contact: an email or a contact id (e.g. {{steps.s1.id}}) to link"], "id", "name", "amount", "stage", "link"),
        ["hubspot/contact.find"] = H([], "found", "id", "email", "name", "phone", "company", "link"),
        ["xero/contact.create"] = H(["email", "firstName", "lastName", "phone"], "id", "name", "email", "link"),
        ["xero/invoice.create"] = H(["description", "quantity: a number (default 1)", "accountCode: e.g. 200", "dueDate: yyyy-MM-dd (default in 30 days)",
            "reference", "status: DRAFT | AUTHORISED (default DRAFT)"], "id", "number", "total", "status", "link"),
        ["xero/contact.find"] = H([], "found", "id", "name", "email", "link", "contacts (a list)"),
        // AI steps: each run uses one of the plan's AI credits; feed them the text with a token.
        ["ai/summarize"] = H(["length: short | medium | bullets (default short)", "focus: what to pay most attention to"], "summary", "bullets (a list)"),
        ["ai/classify"] = H([], "category", "confidence (0 to 1)", "reasoning"),
        ["ai/extract"] = H([], "fields.<name> (each field by its name)", "found", "missing (a list)", "reasoning"),
        ["ai/write"] = H(["context: the text to work from, e.g. the email to reply to", "tone: friendly | professional | formal | casual | concise (default friendly)",
            "format: text | email (email also writes a subject)"], "text", "subject"),
        ["ai/translate"] = H([], "text", "detected"),
    };

    /// <summary>A few config keys whose value is a JSON object or list rather than text.</summary>
    public static readonly IReadOnlySet<string> ObjectKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "fields", "values", "headers", "input", "condition", "assignments", "rows", "fieldIds", "days", "query", "form", "auth", "properties", "approvers", "categories",
    };

    /// <summary>The catalog, the step types and the token rules, as the planner's instructions describe them.</summary>
    public static string Describe(DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("TRIGGERS (trigger.app / trigger.event). Keys marked * are required.");
        foreach (var t in FlowCatalog.Triggers)
        {
            sb.Append("- ").Append(t.App).Append(" / ").Append(t.Key).Append(" — ").Append(t.Title).Append('.');
            AppendKeys(sb, t, Hints.GetValueOrDefault($"trigger:{t.App}/{t.Key}"));
            // A trigger the catalog has no sample of yet gets the generic one: say nothing rather than mislead.
            var sample = FlowCatalog.Sample(new TriggerDef { App = t.App, Event = t.Key }, now);
            var generic = t.App != "webhook" && sample.ToJsonString() == FlowCatalog.Sample(new TriggerDef { App = "", Event = "" }, now).ToJsonString();
            var data = generic ? [] : TokensOf(sample);
            if (data.Count > 0) sb.Append(" Data: ").Append(string.Join(", ", data.Select(p => "{{trigger." + p + "}}")));
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("ACTIONS (step.app / step.action, step.type \"action\"). Keys marked * are required.");
        foreach (var a in FlowCatalog.Actions)
        {
            var hint = Hints.GetValueOrDefault($"{a.App}/{a.Key}");
            sb.Append("- ").Append(a.App).Append(" / ").Append(a.Key).Append(" — ").Append(a.Title).Append('.');
            AppendKeys(sb, a, hint);
            // An AI step's config holds lists of objects (categories, fields): its description says their shape.
            if (a.App == AiStepRules.App && a.Description is { } d) sb.Append(' ').Append(d);
            if (hint is { Outputs.Length: > 0 }) sb.Append(" Hands on: ").Append(string.Join(", ", hint.Outputs.Select(o => "{{steps.<id>." + o.Split(' ')[0] + "}}")));
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static void AppendKeys(StringBuilder sb, CatalogEntry e, Hint? hint)
    {
        var keys = e.Required.Select(k => $"{k}* ({e.Label(k)})").Concat(hint?.Optional ?? []).ToList();
        sb.Append(keys.Count == 0 ? " No config." : " Config: " + string.Join("; ", keys) + ".");
    }

    /// <summary>The token paths in a trigger's sample data (three levels deep at most, lists by their name).</summary>
    public static List<string> TokensOf(JsonNode? node, string prefix = "", int depth = 0)
    {
        var list = new List<string>();
        if (node is not JsonObject o || depth > 2) return list;
        foreach (var (key, value) in o)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is JsonObject inner && inner.Count > 0) list.AddRange(TokensOf(inner, path, depth + 1));
            else list.Add(path);
        }
        return list;
    }
}
