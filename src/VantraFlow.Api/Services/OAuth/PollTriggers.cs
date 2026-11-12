using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.OAuth;

/// <summary>What one poll found: the new items (each with an id the provider gives it) and where the next poll starts.</summary>
public sealed record PollResult(IReadOnlyList<(string Id, JsonObject Data)> Items, string Bookmark);

/// <summary>Reads a polling trigger's new items from its app (the SQL-free part, so tests can fake it).</summary>
public interface IPollSource
{
    /// <summary>
    /// With no bookmark, the trigger's starting point and no items: a flow starts from what arrives after it is switched
    /// on (or saved). Throws the app exceptions; a bookmark the provider no longer knows starts again from now.
    /// </summary>
    Task<PollResult> PollAsync(Guid workspaceId, TriggerDef trigger, string? bookmark, CancellationToken ct);
}

/// <summary>
/// The polling triggers, on the worker's minute tick (db/004): Outlook's new email (a Microsoft Graph delta link on the
/// inbox), Gmail's new email (the mailbox's history id), a Google Sheet's new row (its row count), and Stripe's, HubSpot's
/// and Xero's (<see cref="BusinessPolls"/>, db/007). Each enabled flow
/// has its own cursor (flow.PollCursors), claimed under a lease so one instance polls it; every
/// <c>Connectors:PollSeconds</c> (120 by default), doubling after each failure up to an hour and waiting at least as
/// long as a provider's 429 asks. A trigger's config or the flow's last save changes its fingerprint, which starts it
/// afresh. New items become events for that flow, with ids from the provider's, so an item never starts it twice.
/// </summary>
public sealed class PollTriggers(OAuthRepository repo, EngineRepository engine, FlowSchema schema, IPollSource source, FlowSignal signal, IConfiguration config,
    ILogger<PollTriggers> log)
{
    public const int MaxItems = 25;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);

    public TimeSpan Interval => TimeSpan.FromSeconds(int.TryParse(config["Connectors:PollSeconds"], out var s) ? Math.Clamp(s, 60, 3600) : 120);

    /// <summary>The wait after <paramref name="failures"/> failures in a row: the interval, doubling, at most an hour, and never less than the provider asked.</summary>
    public static TimeSpan Backoff(TimeSpan interval, int failures, TimeSpan? asked = null)
    {
        var wait = TimeSpan.FromSeconds(Math.Min(interval.TotalSeconds * Math.Pow(2, Math.Clamp(failures, 0, 12)), MaxBackoff.TotalSeconds));
        return asked is { } a && a > wait ? (a > MaxBackoff ? MaxBackoff : a) : wait;
    }

    /// <summary>Changes when the trigger's settings do, or the flow is saved or switched on again (a fresh start).</summary>
    public static string Fingerprint(string triggerEvent, JsonObject config, DateTime updatedAt) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{triggerEvent}|{config.ToJsonString()}|{updatedAt.Ticks}")))[..32];

    /// <summary>An event id for an item: the same item for the same flow is always the same id.</summary>
    public static string EventId(string flowId, string itemId) =>
        "fev_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{flowId}|{itemId}")))[..32];

    public async Task<int> RunDueAsync(DateTime now, CancellationToken ct)
    {
        if (!await schema.ConnectorsReadyAsync()) return 0;
        var queued = 0;
        foreach (var due in await repo.DuePollsAsync(now, 25))
        {
            if (ct.IsCancellationRequested) break;
            if (FlowDefinition.Parse(due.Definition) is not { } def) continue;
            if (!await repo.ClaimPollAsync(due, now, now.Add(Lease))) continue;
            var fingerprint = Fingerprint(def.Trigger.Event, def.Trigger.Config, due.UpdatedAt);
            var bookmark = due.Fingerprint == fingerprint ? due.Bookmark : null;
            var failures = due.Failures ?? 0;
            try
            {
                var result = await source.PollAsync(due.WorkspaceId, def.Trigger, bookmark, ct);
                foreach (var (id, data) in result.Items.Take(MaxItems))
                {
                    if (await EnqueueAsync(due, def.Trigger.Event, id, data, now)) queued++;
                }
                await repo.SavePollAsync(due.FlowId, fingerprint, result.Bookmark, DateTime.UtcNow.Add(Interval), 0, null, DateTime.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException or OAuthUnauthorizedException or RateLimitedException)
            {
                failures++;
                var asked = (e as RateLimitedException ?? e.InnerException as RateLimitedException)?.RetryAfter;
                var wait = e is AppRefusedException { Code: "not_connected" } or AppRevokedException ? MaxBackoff : Backoff(Interval, failures, asked);
                log.LogInformation("Poll for flow {Flow} failed ({Failures} in a row): {Reason}", due.FlowId, failures, e.Message);
                await repo.SavePollAsync(due.FlowId, fingerprint, bookmark, DateTime.UtcNow.Add(wait), failures, e.Message, DateTime.UtcNow);
            }
        }
        if (queued > 0) signal.Poke();
        return queued;
    }

    private async Task<bool> EnqueueAsync(PollDueRow due, string kind, string itemId, JsonObject data, DateTime now)
    {
        try
        {
            await engine.EnqueueAsync(due.WorkspaceId, due.Provider, kind, null, due.FlowId, data.ToJsonString(), false, now, id: EventId(due.FlowId, itemId));
            return true;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; } // already queued
    }
}

/// <summary>The real <see cref="IPollSource"/>: Microsoft Graph and Google's APIs on the workspace's connection.</summary>
public sealed class OAuthPollSource(OAuthConnections connections) : IPollSource
{
    private const int MaxPages = 10;

    public Task<PollResult> PollAsync(Guid workspaceId, TriggerDef trigger, string? bookmark, CancellationToken ct) => (trigger.App, trigger.Event) switch
    {
        ("microsoft", "mail.new") => OutlookAsync(workspaceId, bookmark, ct),
        ("google", "gmail.new") => GmailAsync(workspaceId, bookmark, ct),
        ("google", "sheets.row.new") => SheetAsync(workspaceId, trigger.Config, bookmark, ct),
        ("stripe" or "hubspot" or "xero", _) => new BusinessPolls(connections).PollAsync(workspaceId, trigger, bookmark, ct),
        _ => throw new AppRefusedException(400, "This trigger doesn't poll.", "no_poll"),
    };

    private sealed record OutlookMark(string Delta, string Since);

    private Task<JsonNode?> GetAsync(Guid ws, string provider, string url, CancellationToken ct) =>
        connections.CallAsync(ws, provider, s => s.Provider.SendAsync(HttpMethod.Get, url, s.Token, null, ct), ct);

    private async Task<PollResult> OutlookAsync(Guid ws, string? bookmark, CancellationToken ct)
    {
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<OutlookMark>(bookmark);
        var since = mark?.Since ?? DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var url = mark?.Delta ?? MicrosoftProvider.Graph + "me/mailFolders/inbox/messages/delta?$select=subject,from,toRecipients,receivedDateTime,bodyPreview,webLink,hasAttachments" +
            "&$filter=" + Uri.EscapeDataString($"receivedDateTime ge {since}");
        var items = new List<(string, JsonObject)>();
        string? next = url;
        for (var page = 0; page < MaxPages && next is not null; page++)
        {
            JsonNode? res;
            try { res = await GetAsync(ws, "microsoft", next, ct); }
            catch (AppRefusedException e) when (e.Status is 410 && mark is not null) { return await OutlookAsync(ws, null, ct); } // the delta link expired: from now
            foreach (var m in res?["value"] as JsonArray ?? [])
            {
                if (m is not JsonObject msg || msg["@removed"] is not null || mark is null) continue;
                if (string.CompareOrdinal(Tokens.Text(msg["receivedDateTime"]), since) < 0) continue; // an older message that changed (read, moved)
                items.Add((Tokens.Text(msg["id"]), new JsonObject
                {
                    ["id"] = Tokens.Text(msg["id"]),
                    ["subject"] = Tokens.Text(msg["subject"]),
                    ["from"] = new JsonObject { ["name"] = Tokens.Text(msg["from"]?["emailAddress"]?["name"]), ["email"] = Tokens.Text(msg["from"]?["emailAddress"]?["address"]) },
                    ["to"] = string.Join(", ", (msg["toRecipients"] as JsonArray ?? []).Select(r => Tokens.Text(r?["emailAddress"]?["address"]))),
                    ["receivedAt"] = Tokens.Text(msg["receivedDateTime"]),
                    ["preview"] = Tokens.Text(msg["bodyPreview"]),
                    ["link"] = Tokens.Text(msg["webLink"]),
                    ["hasAttachments"] = msg["hasAttachments"]?.DeepClone(),
                }));
            }
            if (Tokens.Text(res?["@odata.deltaLink"]) is { Length: > 0 } delta) { next = null; url = delta; }
            else { next = Tokens.Text(res?["@odata.nextLink"]) is { Length: > 0 } n ? n : null; if (next is not null) url = next; }
        }
        return new PollResult(items, JsonSerializer.Serialize(new OutlookMark(url, since)));
    }

    private sealed record GmailMark(string History);

    private async Task<PollResult> GmailAsync(Guid ws, string? bookmark, CancellationToken ct)
    {
        const string api = "https://gmail.googleapis.com/gmail/v1/users/me/";
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<GmailMark>(bookmark);
        if (mark is null)
        {
            var profile = await GetAsync(ws, "google", api + "profile", ct);
            return new PollResult([], JsonSerializer.Serialize(new GmailMark(Tokens.Text(profile?["historyId"]))));
        }
        var ids = new List<string>();
        var latest = mark.History;
        string? pageToken = null;
        for (var page = 0; page < MaxPages; page++)
        {
            var url = $"{api}history?startHistoryId={Uri.EscapeDataString(mark.History)}&historyTypes=messageAdded&labelId=INBOX&maxResults=100" +
                (pageToken is null ? "" : $"&pageToken={Uri.EscapeDataString(pageToken)}");
            JsonNode? res;
            try { res = await GetAsync(ws, "google", url, ct); }
            catch (AppRefusedException e) when (e.Status == 404) { return await GmailAsync(ws, null, ct); } // too old: from now
            foreach (var h in res?["history"] as JsonArray ?? [])
                foreach (var added in h?["messagesAdded"] as JsonArray ?? [])
                    if (Tokens.Text(added?["message"]?["id"]) is { Length: > 0 } id && !ids.Contains(id)) ids.Add(id);
            if (Tokens.Text(res?["historyId"]) is { Length: > 0 } hid) latest = hid;
            pageToken = Tokens.Text(res?["nextPageToken"]) is { Length: > 0 } t ? t : null;
            if (pageToken is null) break;
        }
        var items = new List<(string, JsonObject)>();
        foreach (var id in ids.Take(PollTriggers.MaxItems))
        {
            JsonNode? m;
            try { m = await GetAsync(ws, "google", $"{api}messages/{Uri.EscapeDataString(id)}?format=metadata&metadataHeaders=From&metadataHeaders=To&metadataHeaders=Subject&metadataHeaders=Date", ct); }
            catch (AppRefusedException e) when (e.Status == 404) { continue; } // deleted since
            var labels = (m?["labelIds"] as JsonArray ?? []).Select(Tokens.Text).ToList();
            if (!labels.Contains("INBOX") || labels.Contains("DRAFT")) continue;
            var headers = (m?["payload"]?["headers"] as JsonArray ?? []).ToDictionary(x => Tokens.Text(x?["name"]).ToLowerInvariant(), x => Tokens.Text(x?["value"]), StringComparer.Ordinal);
            var (name, email) = Address(headers.GetValueOrDefault("from") ?? "");
            var received = long.TryParse(Tokens.Text(m?["internalDate"]), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime : DateTime.UtcNow;
            items.Add((id, new JsonObject
            {
                ["id"] = id,
                ["threadId"] = Tokens.Text(m?["threadId"]),
                ["subject"] = headers.GetValueOrDefault("subject") ?? "",
                ["from"] = new JsonObject { ["name"] = name, ["email"] = email },
                ["to"] = headers.GetValueOrDefault("to") ?? "",
                ["receivedAt"] = Formatter.IsoUtc(received),
                ["snippet"] = System.Net.WebUtility.HtmlDecode(Tokens.Text(m?["snippet"])),
                ["link"] = $"https://mail.google.com/mail/u/0/#inbox/{id}",
            }));
        }
        return new PollResult(items, JsonSerializer.Serialize(new GmailMark(latest)));
    }

    /// <summary>"Ada Lovelace &lt;ada@example.com&gt;" → its name and address.</summary>
    public static (string Name, string Email) Address(string header)
    {
        var lt = header.LastIndexOf('<');
        var gt = header.LastIndexOf('>');
        if (lt >= 0 && gt > lt) return (header[..lt].Trim().Trim('"'), header[(lt + 1)..gt].Trim());
        return ("", header.Trim());
    }

    private sealed record SheetMark(int Rows);

    private async Task<PollResult> SheetAsync(Guid ws, JsonObject config, string? bookmark, CancellationToken ct)
    {
        if (OAuthActions.SpreadsheetId(Tokens.Text(config["spreadsheet"])) is not { } id) throw new AppRefusedException(400, "Paste the spreadsheet's link, or its id.", "bad_spreadsheet");
        var sheet = Tokens.Text(config["sheet"]).Trim();
        var url = $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(id)}/values/{Uri.EscapeDataString(OAuthActions.A1(sheet, $"A1:ZZ{OAuthActions.MaxSheetRows}"))}";
        var res = await GetAsync(ws, "google", url, ct);
        var rows = (res?["values"] as JsonArray ?? []).Select(r => (r as JsonArray ?? []).Select(Tokens.Text).ToList()).ToList();
        var mark = bookmark is null ? null : JsonSerializer.Deserialize<SheetMark>(bookmark);
        if (mark is null || rows.Count < mark.Rows) return new PollResult([], JsonSerializer.Serialize(new SheetMark(rows.Count)));
        var headers = rows.FirstOrDefault() ?? [];
        var items = new List<(string, JsonObject)>();
        var upTo = Math.Min(rows.Count, mark.Rows + PollTriggers.MaxItems);
        for (var i = Math.Max(mark.Rows, 1); i < upTo; i++)
        {
            var values = new JsonObject();
            for (var c = 0; c < headers.Count; c++)
                if (headers[c].Trim().Length > 0 && !values.ContainsKey(headers[c].Trim())) values[headers[c].Trim()] = c < rows[i].Count ? rows[i][c] : "";
            var content = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f", rows[i]))))[..12];
            items.Add(($"{id}:{sheet}:{i + 1}:{content}", new JsonObject { ["rowNumber"] = i + 1, ["values"] = values, ["sheet"] = sheet }));
        }
        return new PollResult(items, JsonSerializer.Serialize(new SheetMark(upTo)));
    }
}
