using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Orbit's connector. Connect: Orbit's integration endpoints (<c>/api/integrations/flow/v1</c>) take the
/// assertion and, for an owner or admin, mint an API key named "Vantra Flow" for that workspace (it shows under
/// Developers → API keys in Orbit, where it can be revoked). Everything after that is Orbit's own REST API v1
/// with that key: tables for the pickers, records for the actions (with an Idempotency-Key, so a retried step
/// never makes a record twice), and <c>/api/v1/webhooks</c> to register Flow's receiver on a table — Orbit's
/// outgoing webhooks (db/012): queued in the writer's own transaction, signed, retried with backoff. A record
/// Flow writes comes back as an event whose <c>source</c> is this key: the origin marker the loop protection
/// reads.
/// </summary>
public sealed class OrbitApi(IHttpClientFactory http, IConfiguration config) : AppApi(http, config)
{
    public override string App => "orbit";
    public override string AppName => "Vantra Orbit";
    protected override string DefaultApiUrl => "https://vantraorbit.azurewebsites.net";
    protected override string DefaultAppUrl => "https://orbit.vantrasoftware.com";
    public static readonly string[] Events = ["record.created", "record.updated"];

    protected override void Authenticate(HttpRequestMessage req, string key) =>
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

    /// <summary>The events a subscription asks for: a table's record events, or a sequence scope's (<c>seq:…</c>) sequence events.</summary>
    public static IReadOnlyList<string> EventsFor(string scope) =>
        scope.StartsWith(TriggerMatcher.SequencePrefix, StringComparison.Ordinal) ? FlowCatalog.SequenceEvents : Events;

    /// <summary>
    /// The tables for the pickers, and the workspace's sequences (the "Add to sequence" and sequence triggers' dropdown).
    /// Before Orbit has sequences, <c>sequences</c> is empty and <c>sequencesOff</c> says why, so the builder can say so.
    /// </summary>
    public override async Task<JsonObject> SchemaAsync(string key, CancellationToken ct)
    {
        var tables = await SendAsync<JsonArray>(HttpMethod.Get, "/api/v1/tables", null, key, null, ct);
        var o = new JsonObject { ["tables"] = tables };
        try { o["sequences"] = await SequencesAsync(key, ct); }
        catch (AppUnavailableException e) { o["sequences"] = new JsonArray(); o["sequencesOff"] = e.Message; }
        catch (AppRefusedException e) { o["sequences"] = new JsonArray(); o["sequencesOff"] = e.Message; }
        // Dashboards for "Get a report" (an Orbit from before reports answers 404: none).
        try { o["dashboards"] = await DashboardsAsync(key, ct); }
        catch (AppUnavailableException) { o["dashboards"] = new JsonArray(); }
        catch (AppRefusedException) { o["dashboards"] = new JsonArray(); }
        return o;
    }

    /// <summary>The connected workspace's dashboards the key's creator sees: [{ id, name, outputs: [{ key, title }] }].</summary>
    public Task<JsonArray> DashboardsAsync(string key, CancellationToken ct) =>
        SendAsync<JsonArray>(HttpMethod.Get, "/api/v1/reports/dashboards", null, key, null, ct);

    /// <summary>
    /// "Get a report": the dashboard worked out as the person who connected Orbit (Orbit's own numbers, what they can see), for a
    /// range: { dashboard, range, link, numbers: { key: { title, value, text, change, changeText, previous, dir } }, pdf? } — the PDF
    /// as base64 when asked for.
    /// </summary>
    public Task<JsonObject> ReportAsync(string key, string dashboardId, string range, bool makePdf, CancellationToken ct) =>
        SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/reports/dashboards/{Uri.EscapeDataString(dashboardId)}", new { range, makePdf }, key, null, ct);

    /// <summary>
    /// The report schedules the person who connected Orbit can see (Orbit's own rights), for the Schedule page:
    /// <c>{ available, reason, schedules: [{ id, title, dashboardName, summary, rangeLabel, recipientCount, enabled, runs, link, canChange, … }] }</c>.
    /// A count of recipients, never their addresses. Before Orbit's db/074, <c>available: false</c>.
    /// </summary>
    public Task<JsonObject> ReportSchedulesAsync(string key, DateTime fromUtc, DateTime toUtc, CancellationToken ct) =>
        SendAsync<JsonObject>(HttpMethod.Get,
            $"/api/v1/reports/schedules?from={Uri.EscapeDataString(Formatter.IsoUtc(fromUtc))}&to={Uri.EscapeDataString(Formatter.IsoUtc(toUtc))}", null, key, null, ct);

    /// <summary>Pause or resume a report schedule in Orbit (as the person who connected it, with their rights there): <c>{ schedule }</c>.</summary>
    public Task<JsonObject> SetReportEnabledAsync(string key, string id, bool enabled, CancellationToken ct) =>
        SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/reports/schedules/{Uri.EscapeDataString(id)}/enabled", new { enabled }, key, null, ct);

    /// <summary>Send a report schedule's email to its recipients now (Orbit rate-limits it): <c>{ status }</c>.</summary>
    public Task<JsonObject> SendReportNowAsync(string key, string id, CancellationToken ct) =>
        SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/reports/schedules/{Uri.EscapeDataString(id)}/send", new { }, key, null, ct);

    /// <summary>The connected workspace's sequences (those of tables the key can read): [{ id, name, status, tableId, tableName }].</summary>
    public Task<JsonArray> SequencesAsync(string key, CancellationToken ct) =>
        SendAsync<JsonArray>(HttpMethod.Get, "/api/v1/sequences", null, key, null, ct);

    public override async Task<AppHook> SubscribeAsync(string key, string scope, string url, CancellationToken ct)
    {
        if (scope.StartsWith(TriggerMatcher.SequencePrefix, StringComparison.Ordinal))
        {
            var seq = scope[TriggerMatcher.SequencePrefix.Length..];
            var made = await SendAsync<JsonObject>(HttpMethod.Post, "/api/v1/sequences/webhooks",
                new { sequenceId = seq == "*" ? null : seq, url, events = FlowCatalog.SequenceEvents, name = "Vantra Flow" }, key, null, ct);
            var hid = Tokens.Text(made["id"]);
            var hsecret = Tokens.Text(made["secret"]);
            if (hid.Length == 0 || hsecret.Length == 0) throw new AppUnavailableException("Vantra Orbit answered something unexpected.");
            return new AppHook(hid, hsecret);
        }
        var res = await SendAsync<JsonObject>(HttpMethod.Post, "/api/v1/webhooks",
            new { tableId = scope, url, events = Events, name = "Vantra Flow" }, key, null, ct);
        var id = Tokens.Text(res["id"]);
        var secret = Tokens.Text(res["secret"]);
        if (id.Length == 0 || secret.Length == 0) throw new AppUnavailableException("Vantra Orbit answered something unexpected.");
        return new AppHook(id, secret);
    }

    /// <summary>Remove a subscription: a sequence one (its id starts <c>sqh_</c>) or a table's.</summary>
    public override Task UnsubscribeAsync(string key, string remoteId, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, (remoteId.StartsWith("sqh_", StringComparison.Ordinal) ? "/api/v1/sequences/webhooks/" : "/api/v1/webhooks/")
            + Uri.EscapeDataString(remoteId), null, key, null, ct);

    public override async Task<JsonObject?> SampleAsync(string key, TriggerDef trigger, CancellationToken ct)
    {
        if (FlowCatalog.IsSequenceEvent(trigger.Event)) return null; // the catalog's sample: a real reply isn't there to read
        var tableId = Tokens.Text(trigger.Config["tableId"]);
        if (tableId.Length == 0) return null;
        var t = Uri.EscapeDataString(tableId);
        var table = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}", null, key, null, ct);
        var page = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}/records?limit=1&sort=createdAt&order=desc", null, key, null, ct);
        if (page["data"] is not JsonArray { Count: > 0 } data || data[0] is not JsonObject rec) return null;
        return TriggerData(trigger.Event == "record.created" ? "record.created" : "record.updated",
            new JsonObject { ["id"] = tableId, ["name"] = table["name"]?.DeepClone() }, Record(rec), new JsonArray(),
            new JsonObject { ["type"] = "user", ["name"] = "Sample" });
    }

    /// <summary>A table as REST API v1 describes it: <c>{ id, name, primaryFieldId, fields: [{ id, name, type }] }</c>.</summary>
    public Task<JsonObject> TableAsync(string key, string tableId, CancellationToken ct) =>
        SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{Uri.EscapeDataString(tableId)}", null, key, null, ct);

    /// <summary>One record as it is now (fields by name, with its link), for a button run.</summary>
    public async Task<JsonObject> RecordAsync(string key, string tableId, string recordId, CancellationToken ct) =>
        Record(await SendAsync<JsonObject>(HttpMethod.Get,
            $"/api/v1/tables/{Uri.EscapeDataString(tableId)}/records/{Uri.EscapeDataString(recordId)}", null, key, null, ct));

    /// <summary>The most records a date scan reads from one table (an older Orbit, without range filters, is paged this far).</summary>
    public const int MaxDateScan = 2000;

    /// <summary>
    /// The records whose date field (an id) falls on a day in [from, to]: REST API v1's range filters
    /// (<c>filter[field][gte]</c>, <c>[lte]</c>), sorted by that field and paged. An Orbit without range filters refuses
    /// them (400): the table is then read in pages, sorted by the field, up to <see cref="MaxDateScan"/> records, and
    /// the days checked here.
    /// </summary>
    public async Task<List<JsonObject>> DateRangeAsync(string key, string tableId, string fieldId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var t = Uri.EscapeDataString(tableId);
        var f = Uri.EscapeDataString(fieldId);
        var range = $"{Uri.EscapeDataString($"filter[{fieldId}][gte]")}={from:yyyy-MM-dd}&{Uri.EscapeDataString($"filter[{fieldId}][lte]")}={to:yyyy-MM-dd}";
        try { return await PagesAsync(key, $"/api/v1/tables/{t}/records?limit=100&sort={f}&{range}", ct); }
        catch (AppRefusedException e) when (e.Status == 400) { /* an Orbit without range filters: read it all and check here */ }
        var all = await PagesAsync(key, $"/api/v1/tables/{t}/records?limit=100&sort={f}", ct);
        return all;
    }

    private async Task<List<JsonObject>> PagesAsync(string key, string path, CancellationToken ct)
    {
        var list = new List<JsonObject>();
        string? cursor = null;
        do
        {
            var page = await SendAsync<JsonObject>(HttpMethod.Get, cursor is null ? path : $"{path}&cursor={Uri.EscapeDataString(cursor)}", null, key, null, ct);
            foreach (var r in (page["data"] as JsonArray ?? []).OfType<JsonObject>()) list.Add(Record(r));
            cursor = Tokens.Text(page["nextCursor"]) is { Length: > 0 } c ? c : null;
        } while (cursor is not null && list.Count < MaxDateScan);
        return list;
    }

    /// <summary>What an Orbit trigger hands its flow: the event, table, record, changes and who made it.</summary>
    public static JsonObject TriggerData(string kind, JsonNode? table, JsonNode? record, JsonNode? changes, JsonNode? actor) => new()
    {
        ["event"] = kind,
        ["table"] = table?.DeepClone(),
        ["record"] = record?.DeepClone(),
        ["changes"] = changes?.DeepClone() ?? new JsonArray(),
        ["actor"] = actor?.DeepClone(),
    };

    /// <summary>
    /// What a sequence trigger hands its flow (Orbit's sequence webhook, as it sends it): the event, the sequence, the record,
    /// its table, the address and when. Never a subject or a snippet: Orbit doesn't send them.
    /// </summary>
    public static JsonObject SequenceTriggerData(JsonObject root)
    {
        var type = Tokens.Text(root["type"]);
        var o = new JsonObject
        {
            ["event"] = type,
            ["sequence"] = root["sequence"]?.DeepClone(),
            ["record"] = root["record"]?.DeepClone(),
            ["tableId"] = root["tableId"]?.DeepClone(),
            ["email"] = root["email"]?.DeepClone(),
            ["enrollment"] = root["enrollment"]?.DeepClone(),
        };
        foreach (var k in new[] { "repliedAt", "bookedAt", "finishedAt" })
            if (root[k] is { } v) o[k] = v.DeepClone();
        return o;
    }

    private JsonObject Record(JsonObject rec)
    {
        var id = Tokens.Text(rec["id"]);
        var o = (JsonObject)rec.DeepClone();
        o["url"] = $"{AppUrl}/r/{Uri.EscapeDataString(id)}";
        return o;
    }

    public override async Task<JsonNode?> ActAsync(string key, string action, JsonObject input, string idempotencyKey, CancellationToken ct)
    {
        if (action == "record.find") return await FindAsync(key, input, ct);
        if (action == "record.comment") return await CommentAsync(key, input, idempotencyKey, ct);
        if (action == "sequence.add") return await AddToSequenceAsync(key, input, idempotencyKey, ct);
        if (action == ReportStep.Action) throw new AppRefusedException(400, "Vantra Flow can't do that.", "unknown_action"); // StepActions runs it (it keeps the PDF)
        var table = Uri.EscapeDataString(Required(input, "tableId", "The table"));
        var fields = Fields(input["fields"]);
        if (fields.Count == 0) throw new AppRefusedException(400, "None of the fields have a value.", "empty");
        switch (action)
        {
            case "record.create":
                return Record(await SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/tables/{table}/records", new { fields }, key, null, ct, idempotencyKey));
            case "record.update":
                var record = Uri.EscapeDataString(Required(input, "recordId", "The record"));
                return Record(await SendAsync<JsonObject>(HttpMethod.Patch, $"/api/v1/tables/{table}/records/{record}", new { fields }, key, null, ct));
            default:
                throw new AppRefusedException(400, "Vantra Orbit can't do that.", "unknown_action");
        }
    }

    /// <summary>
    /// "Find records": up to <c>limit</c> (1–50, default 10) newest-first records of a table, optionally where one
    /// field equals a value (Orbit's <c>filter[field]=value</c>) and / or changed in the last <c>updatedWithinHours</c>.
    /// Hands on <c>{ count, first, records, names }</c> — names: the primary field of each, joined.
    /// </summary>
    private async Task<JsonNode> FindAsync(string key, JsonObject input, CancellationToken ct)
    {
        var tableId = Required(input, "tableId", "The table");
        var t = Uri.EscapeDataString(tableId);
        var limit = int.TryParse(Tokens.Text(input["limit"]), out var l) ? Math.Clamp(l, 1, 50) : 10;
        var q = new List<string> { $"limit={limit}", "sort=updatedAt", "order=desc" };
        var field = Tokens.Text(input["matchField"]).Trim();
        var value = Tokens.Text(input["matchValue"]).Trim();
        if (field.Length > 0 && value.Length > 0) q.Add($"{Uri.EscapeDataString($"filter[{field}]")}={Uri.EscapeDataString(value)}");
        if (double.TryParse(Tokens.Text(input["updatedWithinHours"]), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0)
            q.Add("updatedSince=" + Uri.EscapeDataString(DateTime.UtcNow.AddHours(-Math.Min(hours, 24 * 90)).ToString("yyyy-MM-ddTHH:mm:ssZ")));
        var table = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}", null, key, null, ct);
        var page = await SendAsync<JsonObject>(HttpMethod.Get, $"/api/v1/tables/{t}/records?{string.Join("&", q)}", null, key, null, ct);
        var records = new JsonArray();
        var names = new List<string>();
        var primary = Tokens.Text(table["primaryFieldId"]);
        var primaryName = (table["fields"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(f => Tokens.Text(f["id"]) == primary)?["name"];
        foreach (var r in (page["data"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var rec = Record(r);
            records.Add(rec);
            if (primaryName is not null && Tokens.Resolve(rec, "fields." + Tokens.Text(primaryName)) is { } n) names.Add(Tokens.Text(n));
        }
        return new JsonObject
        {
            ["count"] = records.Count,
            ["first"] = records.Count > 0 ? records[0]!.DeepClone() : null,
            ["records"] = records,
            ["names"] = string.Join(", ", names),
        };
    }

    /// <summary>
    /// "Add comment to record": a comment on the record's timeline, as the key's creator (REST API v1
    /// <c>…/records/{id}/comments</c>, with the step's Idempotency-Key so a retry never adds it twice).
    /// Hands on <c>{ id, text, recordId, url }</c>.
    /// </summary>
    private async Task<JsonNode> CommentAsync(string key, JsonObject input, string idempotencyKey, CancellationToken ct)
    {
        var table = Uri.EscapeDataString(Required(input, "tableId", "The table"));
        var recordId = Required(input, "recordId", "The record");
        var text = Required(input, "text", "The comment");
        if (text.Length > MaxComment) text = text[..MaxComment];
        var res = await SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/tables/{table}/records/{Uri.EscapeDataString(recordId)}/comments",
            new { text }, key, null, ct, idempotencyKey);
        return new JsonObject
        {
            ["id"] = res["id"]?.DeepClone(),
            ["text"] = res["text"]?.DeepClone() ?? text,
            ["recordId"] = recordId,
            ["url"] = $"{AppUrl}/r/{Uri.EscapeDataString(recordId)}",
        };
    }

    public const int MaxComment = 20_000;

    /// <summary>
    /// "Add to sequence": the record (a record id, e.g. {{trigger.record.id}}, of the sequence's table) or an email address (the record
    /// with it), added through Orbit's REST API. Hands on <c>{ enrollmentId, state, skipped, reason, recordId }</c>; someone already in a
    /// sequence, unsubscribed or without an address is skipped, not a failure. Before Orbit has sequences, it fails with Orbit's own
    /// "Sequences aren't switched on in this Orbit workspace yet."
    /// </summary>
    private async Task<JsonNode> AddToSequenceAsync(string key, JsonObject input, string idempotencyKey, CancellationToken ct)
    {
        var sequence = Required(input, "sequenceId", "The sequence");
        var recordId = Tokens.Text(input["recordId"]).Trim();
        var email = Tokens.Text(input["email"]).Trim();
        if (recordId.Length == 0 && email.Length == 0) throw new AppRefusedException(400, "Choose the record, or give an email address.", "empty");
        var tableId = Tokens.Text(input["tableId"]).Trim();
        var res = await SendAsync<JsonObject>(HttpMethod.Post, $"/api/v1/sequences/{Uri.EscapeDataString(sequence)}/enrollments",
            new { tableId = tableId.Length > 0 ? tableId : null, recordId = recordId.Length > 0 ? recordId : null, email = recordId.Length > 0 ? null : email },
            key, null, ct, idempotencyKey);
        var skipped = res["skipped"] is JsonValue sv && sv.TryGetValue<bool>(out var sk) && sk;
        return new JsonObject
        {
            ["enrollmentId"] = res["enrollmentId"]?.DeepClone(),
            ["state"] = skipped ? "skipped" : Tokens.Text(res["state"]) is { Length: > 0 } st ? st : "active",
            ["skipped"] = skipped,
            ["reason"] = res["reason"]?.DeepClone(),
            ["message"] = res["message"]?.DeepClone(),
            ["recordId"] = res["recordId"]?.DeepClone() ?? (recordId.Length > 0 ? recordId : null),
        };
    }

    /// <summary>The field values to write: by name or id, empty ones left out (an empty token doesn't clear a field).</summary>
    public static JsonObject Fields(JsonNode? node)
    {
        var o = new JsonObject();
        if (node is not JsonObject fields) return o;
        foreach (var (name, value) in fields)
        {
            if (string.IsNullOrWhiteSpace(name) || DefinitionRules.Empty(value)) continue;
            o[name.Trim()] = value!.DeepClone();
        }
        return o;
    }
}
