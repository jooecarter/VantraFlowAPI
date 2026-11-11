using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.OAuth;

/// <summary>
/// The outside apps' actions, each on the workspace's connection (<see cref="OAuthConnections.CallAsync{T}"/>):
/// <list type="bullet">
/// <item><b>Slack</b>: send a channel message, send a direct message (to an email or a Slack user id).</item>
/// <item><b>Microsoft 365</b>: send an email as the person (Outlook), post to a Teams channel, create a calendar event.</item>
/// <item><b>Google</b>: send an email as the person (Gmail); add, find and update a row in a Google Sheet (by the
/// sheet's header row).</item>
/// </list>
/// Also the pickers' lookups (Slack channels, Teams and their channels, a spreadsheet's sheets and columns).
/// </summary>
public sealed partial class OAuthActions(OAuthConnections connections, OAuthProviders providers)
{
    public const int MaxText = 3500;
    public const int MaxSheetRows = 5000;

    public async Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct)
    {
        var input = call.Input;
        string T(string key) => Tokens.Text(input[key]).Trim();
        return (call.Step.App, call.Step.Action) switch
        {
            ("slack", "message.send") => await SlackPostAsync(call.WorkspaceId, T("channel").TrimStart('#'), Tokens.Text(input["text"]), null, ct),
            ("slack", "dm.send") => await SlackDmAsync(call.WorkspaceId, T("user"), Tokens.Text(input["text"]), ct),
            ("microsoft", "mail.send") => await OutlookSendAsync(call.WorkspaceId, input, ct),
            ("microsoft", "teams.post") => await TeamsPostAsync(call.WorkspaceId, T("teamId"), T("channelId"), Tokens.Text(input["text"]), ct),
            ("microsoft", "event.create") => await EventAsync(call.WorkspaceId, input, call.TimeZone, ct),
            ("google", "gmail.send") => await GmailSendAsync(call.WorkspaceId, input, ct),
            ("google", "sheets.row.add") => await SheetAddAsync(call.WorkspaceId, input, ct),
            ("google", "sheets.row.find") => await SheetFindAsync(call.WorkspaceId, input, ct),
            ("google", "sheets.row.update") => await SheetUpdateAsync(call.WorkspaceId, input, ct),
            _ => StepOutcome.Fail("This step isn't something Vantra Flow can do."),
        };
    }

    // ── Slack ──

    private async Task<StepOutcome> SlackPostAsync(Guid ws, string channel, string text, string? user, CancellationToken ct)
    {
        if (channel.Length == 0) return StepOutcome.Fail("Choose a channel.");
        if (text.Trim().Length == 0) return StepOutcome.Fail("The message is empty.");
        if (text.Length > MaxText) text = text[..MaxText];
        var res = await connections.CallAsync(ws, "slack", s => providers.Slack.CallAsync("chat.postMessage", s.Token,
            [new("channel", channel), new("text", text), new("unfurl_links", "false")], ct), ct);
        var output = new JsonObject { ["ts"] = Tokens.Text(res?["ts"]), ["channel"] = Tokens.Text(res?["channel"]), ["text"] = text };
        if (user is not null) output["user"] = user;
        return StepOutcome.Done(output);
    }

    private async Task<StepOutcome> SlackDmAsync(Guid ws, string who, string text, CancellationToken ct)
    {
        if (who.Length == 0) return StepOutcome.Fail("Choose who to message: an email or a Slack user id.");
        var userId = who;
        if (who.Contains('@'))
        {
            var found = await connections.CallAsync(ws, "slack", s => providers.Slack.CallAsync("users.lookupByEmail", s.Token, [new("email", who)], ct), ct);
            userId = Tokens.Text(found?["user"]?["id"]);
        }
        var opened = await connections.CallAsync(ws, "slack", s => providers.Slack.CallAsync("conversations.open", s.Token, [new("users", userId)], ct), ct);
        var channel = Tokens.Text(opened?["channel"]?["id"]);
        if (channel.Length == 0) return StepOutcome.Fail("Slack couldn't open a conversation with them.");
        return await SlackPostAsync(ws, channel, text, userId, ct);
    }

    // ── Microsoft 365 ──

    private static JsonArray Addresses(IEnumerable<string> list) =>
        new(list.Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a } }).ToArray());

    private async Task<StepOutcome> OutlookSendAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var to = StepActions.Recipients(Tokens.Text(input["to"]));
        if (to.Error is not null) return StepOutcome.Fail(to.Error);
        var cc = Tokens.Text(input["cc"]).Trim() is { Length: > 0 } c ? StepActions.Recipients(c) : (new List<string>(), null);
        if (cc.Error is not null) return StepOutcome.Fail($"Cc: {cc.Error}");
        var subject = OneLine(Tokens.Text(input["subject"]));
        if (subject.Length == 0) return StepOutcome.Fail("The subject is empty.");
        var message = new JsonObject
        {
            ["subject"] = subject,
            ["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = Html(Tokens.Text(input["body"])) },
            ["toRecipients"] = Addresses(to.List),
        };
        if (cc.List.Count > 0) message["ccRecipients"] = Addresses(cc.List);
        await connections.CallAsync(ws, "microsoft", s => s.Provider.SendAsync(HttpMethod.Post, MicrosoftProvider.Graph + "me/sendMail", s.Token,
            new JsonObject { ["message"] = message, ["saveToSentItems"] = true }, ct), ct);
        return StepOutcome.Done(new JsonObject { ["to"] = new JsonArray(to.List.Select(a => (JsonNode)a).ToArray()), ["subject"] = subject, ["sent"] = to.List.Count });
    }

    private async Task<StepOutcome> TeamsPostAsync(Guid ws, string teamId, string channelId, string text, CancellationToken ct)
    {
        if (teamId.Length == 0 || channelId.Length == 0) return StepOutcome.Fail("Choose a team and a channel.");
        if (text.Trim().Length == 0) return StepOutcome.Fail("The message is empty.");
        var res = await connections.CallAsync(ws, "microsoft", s => s.Provider.SendAsync(HttpMethod.Post,
            $"{MicrosoftProvider.Graph}teams/{Uri.EscapeDataString(teamId)}/channels/{Uri.EscapeDataString(channelId)}/messages", s.Token,
            new JsonObject { ["body"] = new JsonObject { ["contentType"] = "html", ["content"] = Html(text.Length > MaxText ? text[..MaxText] : text) } }, ct), ct);
        return StepOutcome.Done(new JsonObject { ["id"] = Tokens.Text(res?["id"]), ["link"] = Tokens.Text(res?["webUrl"]) });
    }

    private async Task<StepOutcome> EventAsync(Guid ws, JsonObject input, string? timeZone, CancellationToken ct)
    {
        var subject = OneLine(Tokens.Text(input["subject"]));
        if (subject.Length == 0) return StepOutcome.Fail("The event needs a title.");
        var zone = Formatter.FindZone(timeZone ?? "UTC") ?? TimeZoneInfo.Utc;
        if (Formatter.ParseDate(Tokens.Text(input["start"]), "auto", zone) is not { } start) return StepOutcome.Fail($"“{Tokens.Text(input["start"])}” isn't a date and time.");
        DateTime end;
        if (Tokens.Text(input["end"]).Trim().Length > 0)
        {
            if (Formatter.ParseDate(Tokens.Text(input["end"]), "auto", zone) is not { } e) return StepOutcome.Fail($"“{Tokens.Text(input["end"])}” isn't a date and time.");
            end = e;
        }
        else end = start.AddMinutes(Formatter.ToNumber(input["minutes"]) is { } m && m >= 5 && m <= 24 * 60 ? m : 30);
        if (end <= start) return StepOutcome.Fail("The event ends before it starts.");
        var ev = new JsonObject
        {
            ["subject"] = subject,
            ["start"] = new JsonObject { ["dateTime"] = start.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), ["timeZone"] = "UTC" },
            ["end"] = new JsonObject { ["dateTime"] = end.ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture), ["timeZone"] = "UTC" },
        };
        if (Tokens.Text(input["body"]).Trim() is { Length: > 0 } body) ev["body"] = new JsonObject { ["contentType"] = "HTML", ["content"] = Html(body) };
        if (Tokens.Text(input["location"]).Trim() is { Length: > 0 } where) ev["location"] = new JsonObject { ["displayName"] = OneLine(where) };
        if (Tokens.Text(input["attendees"]).Trim() is { Length: > 0 } who)
        {
            var list = StepActions.Recipients(who);
            if (list.Error is not null) return StepOutcome.Fail($"Attendees: {list.Error}");
            ev["attendees"] = new JsonArray(list.List.Select(a => (JsonNode)new JsonObject { ["emailAddress"] = new JsonObject { ["address"] = a }, ["type"] = "required" }).ToArray());
        }
        var res = await connections.CallAsync(ws, "microsoft", s => s.Provider.SendAsync(HttpMethod.Post, MicrosoftProvider.Graph + "me/events", s.Token, ev, ct), ct);
        return StepOutcome.Done(new JsonObject
        {
            ["id"] = Tokens.Text(res?["id"]), ["link"] = Tokens.Text(res?["webLink"]), ["subject"] = subject,
            ["start"] = Formatter.IsoUtc(start), ["end"] = Formatter.IsoUtc(end),
        });
    }

    // ── Google ──

    private async Task<StepOutcome> GmailSendAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var to = StepActions.Recipients(Tokens.Text(input["to"]));
        if (to.Error is not null) return StepOutcome.Fail(to.Error);
        var cc = Tokens.Text(input["cc"]).Trim() is { Length: > 0 } c ? StepActions.Recipients(c) : (new List<string>(), null);
        if (cc.Error is not null) return StepOutcome.Fail($"Cc: {cc.Error}");
        var subject = OneLine(Tokens.Text(input["subject"]));
        if (subject.Length == 0) return StepOutcome.Fail("The subject is empty.");
        var raw = Mime(to.List, cc.List, subject, Html(Tokens.Text(input["body"])));
        var res = await connections.CallAsync(ws, "google", s => s.Provider.SendAsync(HttpMethod.Post,
            "https://gmail.googleapis.com/gmail/v1/users/me/messages/send", s.Token, new JsonObject { ["raw"] = raw }, ct), ct);
        return StepOutcome.Done(new JsonObject
        {
            ["id"] = Tokens.Text(res?["id"]), ["threadId"] = Tokens.Text(res?["threadId"]),
            ["to"] = new JsonArray(to.List.Select(a => (JsonNode)a).ToArray()), ["subject"] = subject,
        });
    }

    /// <summary>An email as Gmail takes it: RFC 5322, base64url. Headers can't carry a line break, so a token can't add one.</summary>
    public static string Mime(IReadOnlyList<string> to, IReadOnlyList<string> cc, string subject, string html)
    {
        var sb = new StringBuilder();
        sb.Append("To: ").Append(string.Join(", ", to)).Append("\r\n");
        if (cc.Count > 0) sb.Append("Cc: ").Append(string.Join(", ", cc)).Append("\r\n");
        sb.Append("Subject: =?UTF-8?B?").Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(OneLine(subject)))).Append("?=\r\n");
        sb.Append("MIME-Version: 1.0\r\nContent-Type: text/html; charset=UTF-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        var body = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        for (var i = 0; i < body.Length; i += 76) sb.Append(body, i, Math.Min(76, body.Length - i)).Append("\r\n");
        return System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    [GeneratedRegex(@"/spreadsheets/d/([A-Za-z0-9_-]{10,})")]
    private static partial Regex SheetUrl();

    [GeneratedRegex(@"^[A-Za-z0-9_-]{20,}$")]
    private static partial Regex SheetId();

    /// <summary>A spreadsheet's id from its link or as it is; null when it's neither.</summary>
    public static string? SpreadsheetId(string raw)
    {
        var s = raw.Trim();
        if (SheetUrl().Match(s) is { Success: true } m) return m.Groups[1].Value;
        return SheetId().IsMatch(s) ? s : null;
    }

    /// <summary>A sheet's name in A1 notation: quoted, its quotes doubled.</summary>
    public static string A1(string sheet, string range) => $"'{sheet.Replace("'", "''")}'!{range}";

    /// <summary>Column 1 → A, 27 → AA.</summary>
    public static string Column(int n)
    {
        var s = "";
        for (; n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    private static string Values(string spreadsheet, string range) =>
        $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(spreadsheet)}/values/{Uri.EscapeDataString(range)}";

    private sealed record Sheet(string Id, string Name);

    private static (Sheet? Sheet, string? Error) SheetOf(JsonObject input)
    {
        if (SpreadsheetId(Tokens.Text(input["spreadsheet"])) is not { } id) return (null, "Paste the spreadsheet's link, or its id.");
        var name = Tokens.Text(input["sheet"]).Trim();
        return name.Length == 0 ? (null, "Choose the sheet (the tab).") : (new Sheet(id, name), null);
    }

    private Task<JsonNode?> GetAsync(Guid ws, string url, CancellationToken ct) =>
        connections.CallAsync(ws, "google", s => s.Provider.SendAsync(HttpMethod.Get, url, s.Token, null, ct), ct);

    /// <summary>The sheet's rows as text (the first is the header row), at most <see cref="MaxSheetRows"/>.</summary>
    private async Task<List<List<string>>> RowsAsync(Guid ws, Sheet sheet, string range, CancellationToken ct)
    {
        var res = await GetAsync(ws, Values(sheet.Id, A1(sheet.Name, range)), ct);
        return (res?["values"] as JsonArray ?? []).Select(r => (r as JsonArray ?? []).Select(Tokens.Text).ToList()).ToList();
    }

    private static JsonObject RowObject(IReadOnlyList<string> headers, IReadOnlyList<string> row)
    {
        var o = new JsonObject();
        for (var i = 0; i < headers.Count; i++)
            if (headers[i].Length > 0 && !o.ContainsKey(headers[i])) o[headers[i]] = i < row.Count ? row[i] : "";
        return o;
    }

    /// <summary>The values to write, by the header row: an unknown column is refused, by name.</summary>
    private static (List<string>? Row, string? Error) Align(IReadOnlyList<string> headers, JsonObject? values, IReadOnlyList<string>? existing = null)
    {
        if (headers.Count == 0) return (null, "The sheet needs a header row (column names in row 1).");
        var row = Enumerable.Range(0, headers.Count).Select(i => existing is not null && i < existing.Count ? existing[i] : "").ToList();
        foreach (var (key, value) in values ?? [])
        {
            var i = headers.ToList().FindIndex(h => string.Equals(h.Trim(), key.Trim(), StringComparison.OrdinalIgnoreCase));
            if (i < 0) return (null, $"The sheet has no “{key}” column.");
            row[i] = Tokens.Text(value);
        }
        return (row, null);
    }

    private async Task<StepOutcome> SheetAddAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var (sheet, why) = SheetOf(input);
        if (sheet is null) return StepOutcome.Fail(why!);
        var headers = (await RowsAsync(ws, sheet, "1:1", ct)).FirstOrDefault() ?? [];
        var (row, error) = Align(headers, input["values"] as JsonObject);
        if (row is null) return StepOutcome.Fail(error!);
        if (row.All(v => v.Length == 0)) return StepOutcome.Fail("None of the columns have a value.");
        var url = Values(sheet.Id, A1(sheet.Name, "A1")) + ":append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS";
        var res = await connections.CallAsync(ws, "google", s => s.Provider.SendAsync(HttpMethod.Post, url, s.Token,
            new JsonObject { ["values"] = new JsonArray(new JsonArray(row.Select(v => (JsonNode)v).ToArray())) }, ct), ct);
        var range = Tokens.Text(res?["updates"]?["updatedRange"]);
        var n = Regex.Match(range, @"!\D+(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0;
        return StepOutcome.Done(new JsonObject { ["rowNumber"] = n, ["values"] = RowObject(headers, row) });
    }

    private async Task<StepOutcome> SheetFindAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var (sheet, why) = SheetOf(input);
        if (sheet is null) return StepOutcome.Fail(why!);
        var column = Tokens.Text(input["column"]).Trim();
        if (column.Length == 0) return StepOutcome.Fail("Choose the column to look in.");
        var value = Tokens.Text(input["value"]).Trim();
        var rows = await RowsAsync(ws, sheet, $"A1:ZZ{MaxSheetRows}", ct);
        var headers = rows.FirstOrDefault() ?? [];
        var col = headers.FindIndex(h => string.Equals(h.Trim(), column, StringComparison.OrdinalIgnoreCase));
        if (col < 0) return StepOutcome.Fail($"The sheet has no “{column}” column.");
        var matches = rows.Skip(1).Select((r, i) => (Row: r, Number: i + 2))
            .Where(x => string.Equals((col < x.Row.Count ? x.Row[col] : "").Trim(), value, StringComparison.OrdinalIgnoreCase)).ToList();
        var first = matches.FirstOrDefault();
        return StepOutcome.Done(new JsonObject
        {
            ["found"] = matches.Count > 0,
            ["count"] = matches.Count,
            ["rowNumber"] = matches.Count > 0 ? first.Number : null,
            ["values"] = matches.Count > 0 ? RowObject(headers, first.Row) : new JsonObject(),
            ["rows"] = new JsonArray(matches.Take(100).Select(x => { var o = RowObject(headers, x.Row); o["rowNumber"] = x.Number; return (JsonNode)o; }).ToArray()),
        });
    }

    private async Task<StepOutcome> SheetUpdateAsync(Guid ws, JsonObject input, CancellationToken ct)
    {
        var (sheet, why) = SheetOf(input);
        if (sheet is null) return StepOutcome.Fail(why!);
        if (Formatter.ToNumber(input["rowNumber"]) is not { } rn || rn < 2 || rn != Math.Floor(rn) || rn > 1_000_000)
            return StepOutcome.Fail("The row number must be 2 or more (row 1 holds the column names).");
        var n = (int)rn;
        var headers = (await RowsAsync(ws, sheet, "1:1", ct)).FirstOrDefault() ?? [];
        var existing = (await RowsAsync(ws, sheet, $"{n}:{n}", ct)).FirstOrDefault() ?? [];
        var (row, error) = Align(headers, input["values"] as JsonObject, existing);
        if (row is null) return StepOutcome.Fail(error!);
        var url = Values(sheet.Id, A1(sheet.Name, $"A{n}:{Column(headers.Count)}{n}")) + "?valueInputOption=USER_ENTERED";
        await connections.CallAsync(ws, "google", s => s.Provider.SendAsync(HttpMethod.Put, url, s.Token,
            new JsonObject { ["values"] = new JsonArray(new JsonArray(row.Select(v => (JsonNode)v).ToArray())) }, ct), ct);
        return StepOutcome.Done(new JsonObject { ["rowNumber"] = n, ["values"] = RowObject(headers, row) });
    }

    // ── Pickers ──

    /// <summary>What a picker offers: Slack's channels, Microsoft's teams and a team's channels, a spreadsheet's sheets and a sheet's columns.</summary>
    public async Task<JsonNode> LookupAsync(Guid ws, string provider, string what, IReadOnlyDictionary<string, string?> q, CancellationToken ct)
    {
        switch (provider, what)
        {
            case ("slack", "channels"):
            {
                var list = new JsonArray();
                string? cursor = null;
                for (var page = 0; page < 5; page++)
                {
                    var form = new List<KeyValuePair<string, string>> { new("types", "public_channel,private_channel"), new("exclude_archived", "true"), new("limit", "200") };
                    if (cursor is { Length: > 0 }) form.Add(new("cursor", cursor));
                    var res = await connections.CallAsync(ws, "slack", s => providers.Slack.CallAsync("conversations.list", s.Token, form, ct), ct);
                    foreach (var c in res?["channels"] as JsonArray ?? [])
                        list.Add(new JsonObject { ["id"] = Tokens.Text(c?["id"]), ["name"] = Tokens.Text(c?["name"]), ["private"] = c?["is_private"]?.DeepClone(), ["member"] = c?["is_member"]?.DeepClone() });
                    cursor = Tokens.Text(res?["response_metadata"]?["next_cursor"]);
                    if (cursor.Length == 0) break;
                }
                return list;
            }
            case ("microsoft", "teams"):
            {
                var res = await connections.CallAsync(ws, "microsoft", s => s.Provider.SendAsync(HttpMethod.Get, MicrosoftProvider.Graph + "me/joinedTeams?$select=id,displayName", s.Token, null, ct), ct);
                return new JsonArray((res?["value"] as JsonArray ?? []).Select(t => (JsonNode)new JsonObject { ["id"] = Tokens.Text(t?["id"]), ["name"] = Tokens.Text(t?["displayName"]) }).ToArray());
            }
            case ("microsoft", "channels"):
            {
                var team = q.GetValueOrDefault("team") ?? "";
                if (team.Length == 0) return new JsonArray();
                var res = await connections.CallAsync(ws, "microsoft", s => s.Provider.SendAsync(HttpMethod.Get,
                    $"{MicrosoftProvider.Graph}teams/{Uri.EscapeDataString(team)}/channels?$select=id,displayName", s.Token, null, ct), ct);
                return new JsonArray((res?["value"] as JsonArray ?? []).Select(t => (JsonNode)new JsonObject { ["id"] = Tokens.Text(t?["id"]), ["name"] = Tokens.Text(t?["displayName"]) }).ToArray());
            }
            case ("google", "sheets"):
            {
                if (SpreadsheetId(q.GetValueOrDefault("spreadsheet") ?? "") is not { } id) throw new AppRefusedException(400, "Paste the spreadsheet's link, or its id.", "bad_spreadsheet");
                var res = await GetAsync(ws, $"https://sheets.googleapis.com/v4/spreadsheets/{Uri.EscapeDataString(id)}?fields=properties.title,sheets.properties.title", ct);
                return new JsonObject
                {
                    ["id"] = id, ["title"] = Tokens.Text(res?["properties"]?["title"]),
                    ["sheets"] = new JsonArray((res?["sheets"] as JsonArray ?? []).Select(s => (JsonNode)Tokens.Text(s?["properties"]?["title"])).ToArray()),
                };
            }
            case ("google", "columns"):
            {
                var (sheet, why) = SheetOf(new JsonObject { ["spreadsheet"] = q.GetValueOrDefault("spreadsheet"), ["sheet"] = q.GetValueOrDefault("sheet") });
                if (sheet is null) throw new AppRefusedException(400, why!, "bad_sheet");
                var headers = (await RowsAsync(ws, sheet, "1:1", ct)).FirstOrDefault() ?? [];
                return new JsonArray(headers.Where(h => h.Trim().Length > 0).Select(h => (JsonNode)h).ToArray());
            }
            default:
                throw new AppRefusedException(404, "There's nothing to look up there.", "no_lookup");
        }
    }

    /// <summary>Joins a public Slack channel, so its messages reach Flow (best effort: a private one needs an invite).</summary>
    public async Task JoinSlackChannelAsync(Guid ws, string channel, CancellationToken ct)
    {
        try { await connections.CallAsync(ws, "slack", s => providers.Slack.CallAsync("conversations.join", s.Token, [new("channel", channel)], ct), ct); }
        catch (Exception e) when (e is AppRefusedException or AppUnavailableException or AppRevokedException) { }
    }

    /// <summary>Plain text as an email or Teams body: escaped, its lines kept.</summary>
    public static string Html(string text) =>
        "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55\">" +
        WebUtility.HtmlEncode(text.Replace("\r\n", "\n")).Replace("\n", "<br>") + "</div>";

    public static string OneLine(string s)
    {
        var t = Regex.Replace(s, @"[\r\n\t]+", " ").Trim();
        return t.Length > 250 ? t[..250] : t;
    }
}
