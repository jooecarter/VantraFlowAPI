using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Carries out each app's actions for the engine: Orbit and Projects through the workspace's connection, email
/// through Orbit's sending approach (<see cref="EmailService"/>: Microsoft Graph, else SMTP) within the daily cap,
/// and HTTP requests through the guarded client (<see cref="NetworkGuard"/>: public addresses only, checked at
/// connect time; no redirects). Never logs a key, a secret or a request's headers.
/// </summary>
public sealed partial class StepActions(Connections connections, EmailService email, EngineRepository engine, IHttpClientFactory http, FlowLimits limits,
    FlowsRepository flows, FlowSignal signal, OAuth.OAuthActions oauth, StepsRepository steps, FlowSecrets secrets, FlowSchema schema, Ai.AiSteps ai,
    Mail.Senders senders)
    : IStepActions
{
    public const string HttpClientName = "flow-http";
    public const int MaxRecipients = 10;
    public const int MaxResponseBytes = 64 * 1024;
    public static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);
    private static readonly HashSet<string> Methods = ["GET", "POST", "PUT", "PATCH", "DELETE"];
    private static readonly HashSet<string> BlockedHeaders = new(StringComparer.OrdinalIgnoreCase)
        { "Host", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "Proxy-Authorization", "Cookie" };

    public async Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct)
    {
        var step = call.Step;
        try
        {
            return step.App switch
            {
                "orbit" or "projects" => StepOutcome.Done(await connections.CallAsync(call.WorkspaceId, step.App,
                    (api, key) => api.ActAsync(key, step.Action ?? "", call.Input, $"flow:{call.RunId}:{step.Id}", ct))),
                "email" => await EmailAsync(call, ct),
                "http" => await HttpAsync(call, ct),
                "flow" => await RunFlowAsync(call),
                "slack" or "microsoft" or "google" or "stripe" or "hubspot" or "xero" => await oauth.RunAsync(call, ct),
                Ai.AiStepRules.App => await ai.RunAsync(call, ct),
                _ => StepOutcome.Fail("This step isn't something Vantra Flow can do."),
            };
        }
        catch (AppUnavailableException e) { return StepOutcome.Fail(e.Message, retryable: true); }
        catch (AppRevokedException e) { return StepOutcome.Fail(e.Message); }
        catch (AppRefusedException e) { return StepOutcome.Fail(e.Message, retryable: e.Status is 409 or 423 && e.Code != "not_connected"); }
        catch (OAuth.OAuthUnauthorizedException e) { return StepOutcome.Fail(e.Message); }
        catch (OAuth.RateLimitedException e) { return StepOutcome.Fail(e.Message, retryable: true); }
    }

    // ── Run another flow ──

    /// <summary>
    /// Queues a run of another flow in this workspace (on, and started by "Called by another flow"), handing it the
    /// step's <c>input</c> and who called. It doesn't wait. Refused past <see cref="SubFlowRules.MaxDepth"/>.
    /// </summary>
    private async Task<StepOutcome> RunFlowAsync(StepCall call)
    {
        var depth = call.Depth + 1;
        if (depth > SubFlowRules.MaxDepth) return StepOutcome.Fail($"Flows can call each other at most {SubFlowRules.MaxDepth} deep.");
        var targetId = Tokens.Text(call.Input["flowId"]).Trim();
        if (await flows.GetAsync(call.WorkspaceId, targetId) is not { } target) return StepOutcome.Fail("That flow doesn't exist any more.");
        if (target.TriggerApp != "flow") return StepOutcome.Fail($"“{target.Name}” doesn't start with Called by another flow.");
        if (!target.Enabled) return StepOutcome.Fail($"“{target.Name}” is off. Turn it on to run it from here.");
        var caller = await flows.GetAsync(call.WorkspaceId, call.FlowId);
        var payload = new JsonObject
        {
            ["input"] = call.Input["input"] is JsonObject input ? input.DeepClone() : new JsonObject(),
            ["caller"] = new JsonObject
            {
                ["flowId"] = call.FlowId, ["flowName"] = caller?.Name ?? "", ["runId"] = call.RunId, ["stepId"] = call.Step.Id, ["depth"] = depth, ["test"] = call.Test,
            },
        };
        var id = FlowSecrets.NewRowId("fev_");
        await engine.EnqueueAsync(call.WorkspaceId, EventSources.Manual, "called", null, target.Id, payload.ToJsonString(), false, DateTime.UtcNow, id: id);
        signal.Poke();
        return StepOutcome.Done(new JsonObject { ["flowId"] = target.Id, ["flowName"] = target.Name, ["queued"] = true, ["eventId"] = id });
    }

    // ── Email ──

    /// <summary>
    /// The Send email step: from Vantra Flow's own mailbox, as before, or — with <c>from</c> — the workspace's Microsoft 365 or
    /// Google account or an SMTP mailbox (<see cref="Mail.Senders"/>); "Workspace default" (or no <c>from</c>, as every older
    /// flow) takes the workspace's default, and with none, Vantra Flow. Each person gets their own email, within the
    /// workspace's daily cap. The output names who sent it (<c>sentFrom</c>).
    /// </summary>
    private async Task<StepOutcome> EmailAsync(StepCall call, CancellationToken ct)
    {
        var to = Recipients(Tokens.Text(call.Input["to"]));
        if (to.Error is not null) return StepOutcome.Fail(to.Error);
        var subject = Tokens.Text(call.Input["subject"]).Trim();
        if (subject.Length == 0) return StepOutcome.Fail("The subject is empty.");
        if (subject.Length > 200) subject = subject[..200];
        if (Mail.SenderRules.Parse(call.Input["from"]) is not { } chosen) return StepOutcome.Fail("Choose who the email is sent from.");
        var who = await senders.ResolveAsync(call.WorkspaceId, chosen);
        var body = Tokens.Text(call.Input["body"]);
        var replyTo = Tokens.Text(call.Input["replyTo"]).Trim();
        var named = Tokens.Text(call.Input["fromName"]).Trim();
        var sent = new JsonArray();
        if (!who.Own)
        {
            if (!email.CanSend()) return StepOutcome.Fail("Email isn't set up on this server yet.");
            var fromName = named is { Length: > 0 } n ? n : "Vantra Flow";
            var html = Html(body);
            foreach (var address in to.List)
            {
                ct.ThrowIfCancellationRequested();
                if (!await engine.CountEmailAsync(call.WorkspaceId, DateTime.UtcNow, limits.EmailsPerDay))
                    return StepOutcome.Fail($"This workspace has sent its {limits.EmailsPerDay} emails for today.");
                if (!await email.SendAsync(replyTo, fromName, address, "", subject, html))
                    return sent.Count == 0
                        ? StepOutcome.Fail("The email couldn't be sent.", retryable: true)
                        : StepOutcome.Fail($"Sent to {sent.Count}, then the email couldn't be sent to {address}.");
                sent.Add(address);
            }
            return StepOutcome.Done(new JsonObject { ["to"] = sent, ["subject"] = subject, ["sent"] = sent.Count, ["sentFrom"] = senders.Vantra().Output() });
        }

        // An own mailbox: as the person's Outlook or Gmail (the same code as those apps' send actions), or SMTP.
        var from = await senders.OpenAsync(call.WorkspaceId, who);
        if (!from.Ok) return StepOutcome.Fail(from.Error!, retryable: from.Retryable);
        if (replyTo.Length > 0 && Recipients(replyTo) is { Error: null, List.Count: 1 } r) replyTo = r.List[0];
        else if (replyTo.Length > 0) return StepOutcome.Fail($"The reply-to “{replyTo}” isn't one email address.");
        var own = OAuth.OAuthActions.Html(body);
        foreach (var address in to.List)
        {
            ct.ThrowIfCancellationRequested();
            if (!await engine.CountEmailAsync(call.WorkspaceId, DateTime.UtcNow, limits.EmailsPerDay))
                return StepOutcome.Fail($"This workspace has sent its {limits.EmailsPerDay} emails for today.");
            var result = await senders.SendAsync(call.WorkspaceId, from, new Mail.OutgoingMail(address, OAuth.OAuthActions.OneLine(subject), own, replyTo.Length > 0 ? replyTo : null,
                named.Length > 0 ? named : null), ct);
            if (!result.Ok)
                return sent.Count == 0
                    ? StepOutcome.Fail(result.Error!, retryable: result.Retryable)
                    : StepOutcome.Fail($"Sent to {sent.Count}, then: {result.Error}");
            sent.Add(address);
        }
        return StepOutcome.Done(new JsonObject { ["to"] = sent, ["subject"] = subject, ["sent"] = sent.Count, ["sentFrom"] = from.Output() });
    }

    public static (List<string> List, string? Error) Recipients(string raw)
    {
        var list = new List<string>();
        foreach (var part in raw.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!MailAddress.TryCreate(part, out var a) || a.Address.Contains(' ')) return (list, $"“{part}” isn't an email address.");
            if (!list.Contains(a.Address, StringComparer.OrdinalIgnoreCase)) list.Add(a.Address);
        }
        if (list.Count == 0) return (list, "There's no one to send it to.");
        if (list.Count > MaxRecipients) return (list, $"An email step sends to at most {MaxRecipients} people.");
        return (list, null);
    }

    /// <summary>The body as written (plain text, tokens filled in), escaped, its lines kept.</summary>
    public static string Html(string text)
    {
        var escaped = WebUtility.HtmlEncode(text.Replace("\r\n", "\n")).Replace("\n", "<br>");
        return "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55;color:#18222f\">" + escaped +
               "<p style=\"margin-top:24px;font-size:12px;color:#8b95a3\">Sent by Vantra Flow</p></div>";
    }

    // ── HTTP ──

    public static string? CheckUrl(string raw, out Uri? uri)
    {
        uri = null;
        if (!Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return "The URL must start with https:// or http://.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Put credentials in a header, not the URL.";
        if (u.IsLoopback || u.HostNameType is UriHostNameType.Basic) return "That address isn't on the public internet.";
        if (IPAddress.TryParse(u.Host.Trim('[', ']'), out var ip) && !NetworkGuard.IsPublic(ip)) return "That address is on a private or reserved network, so Vantra Flow won't call it.";
        uri = u;
        return null;
    }

    /// <summary>The most redirects followed, each one's address checked again (public addresses only).</summary>
    public const int MaxRedirects = 3;
    public const int MaxResponseHeaders = 30;
    private static readonly HashSet<string> HiddenResponseHeaders = new(StringComparer.OrdinalIgnoreCase) { "Set-Cookie", "Set-Cookie2" };

    /// <summary>
    /// The HTTP request step: <c>{ method, url, query, headers, bodyType: json | form | raw, body, form, auth, errors }</c>.
    /// <c>query</c> and <c>form</c> are name → value maps (tokens filled in); <c>auth</c> is <c>{ type: none | bearer | basic |
    /// header, secretId, username, header }</c>, the secret read from the workspace's sealed secrets (db/007) and sent over
    /// https only. Redirects are followed (at most <see cref="MaxRedirects"/>), each new address checked as the first was, and
    /// credentials dropped when one leaves the first host. Its output: <c>{ status, ok, url, headers, contentType, body,
    /// truncated }</c>, the body parsed when it's JSON and cut at <see cref="MaxResponseBytes"/>. A 4xx or 5xx fails the
    /// step (a 5xx, 408 or 429 worth retrying) unless <c>errors: continue</c>, when the step hands on the response as it is.
    /// </summary>
    private async Task<StepOutcome> HttpAsync(StepCall call, CancellationToken ct)
    {
        var input = call.Input;
        var method = (Tokens.Text(input["method"]) is { Length: > 0 } m ? m : "POST").ToUpperInvariant();
        if (!Methods.Contains(method)) return StepOutcome.Fail("Choose GET, POST, PUT, PATCH or DELETE.");
        if (CheckUrl(Tokens.Text(input["url"]), out var first) is { } bad) return StepOutcome.Fail(bad);
        var uri = WithQuery(first!, input["query"] as JsonObject);

        // The body, made again for each try (a redirect that keeps the method sends it again).
        var bodyType = Tokens.Text(input["bodyType"]);
        Func<HttpContent?> content = () => null;
        if (method != "GET")
        {
            switch (bodyType)
            {
                case "form":
                    var pairs = (input["form"] as JsonObject ?? []).Where(kv => kv.Key.Trim().Length > 0)
                        .Select(kv => new KeyValuePair<string, string>(kv.Key.Trim(), Tokens.Text(kv.Value))).ToList();
                    if (pairs.Count > 0) content = () => new FormUrlEncodedContent(pairs);
                    break;
                case "json":
                    var json = Tokens.Text(input["body"]);
                    if (json.Trim().Length > 0 && !IsJson(json)) return StepOutcome.Fail("The body isn't valid JSON.");
                    if (json.Trim().Length > 0) content = () => new StringContent(json, Encoding.UTF8, "application/json");
                    break;
                default:
                    var text = Tokens.Text(input["body"]);
                    if (text.Length > 0)
                    {
                        var type = bodyType == "raw" ? "text/plain" : IsJson(text) ? "application/json" : "text/plain";
                        content = () => new StringContent(text, Encoding.UTF8, type);
                    }
                    break;
            }
        }

        // Credentials: a saved secret, never written in the step itself.
        (string Name, string Value)? auth = null;
        var authCfg = input["auth"] as JsonObject;
        var authType = Tokens.Text(authCfg?["type"]);
        if (authType is "bearer" or "basic" or "header")
        {
            if (uri.Scheme != "https") return StepOutcome.Fail("Secrets are only sent over https://.");
            var (secret, why) = await SecretAsync(call.WorkspaceId, Tokens.Text(authCfg?["secretId"]).Trim());
            if (secret is null) return StepOutcome.Fail(why!);
            switch (authType)
            {
                case "bearer": auth = ("Authorization", "Bearer " + secret); break;
                case "basic":
                    var user = Tokens.Text(authCfg?["username"]);
                    if (user.Contains(':')) return StepOutcome.Fail("The username can't contain a colon.");
                    auth = ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{secret}")));
                    break;
                default:
                    var name = Tokens.Text(authCfg?["header"]).Trim();
                    if (!HeaderName().IsMatch(name) || BlockedHeaders.Contains(name)) return StepOutcome.Fail("Name the header the key goes in, like X-Api-Key.");
                    auth = (name, secret);
                    break;
            }
        }

        var headers = new List<(string Name, string Value)>();
        string? contentType = null;
        if (input["headers"] is JsonObject hs)
            foreach (var (name, value) in hs)
            {
                if (string.IsNullOrWhiteSpace(name) || BlockedHeaders.Contains(name)) continue;
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) { contentType = Tokens.Text(value); continue; }
                headers.Add((name.Trim(), Tokens.Text(value)));
            }

        // The step's own "On error" timeout when it has one (the engine holds it to its caps), else 15 seconds.
        var wait = call.Step.TimeoutSeconds is not null && call.TimeoutSeconds is { } own ? TimeSpan.FromSeconds(own) : HttpTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        var client = http.CreateClient(HttpClientName);
        var origin = uri;
        var sendBody = true;
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(new HttpMethod(method), uri);
            if (sendBody && content() is { } c)
            {
                if (contentType is not null && MediaTypeHeaderValue.TryParse(contentType, out var ctype)) c.Headers.ContentType = ctype;
                req.Content = c;
            }
            var sameOrigin = uri.Scheme == origin.Scheme && string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) && uri.Port == origin.Port;
            foreach (var (name, value) in headers)
                if (sameOrigin || !name.Equals("Authorization", StringComparison.OrdinalIgnoreCase)) req.Headers.TryAddWithoutValidation(name, value);
            if (auth is { } a && sameOrigin) req.Headers.TryAddWithoutValidation(a.Name, a.Value);
            req.Headers.UserAgent.ParseAdd("VantraFlow/1.0");

            HttpResponseMessage res;
            try { res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StepOutcome.Fail($"No answer within {wait.TotalSeconds:0} seconds.", retryable: true); }
            catch (HttpRequestException e) when (Innermost(e) is WebhookBlockedException blocked) { return StepOutcome.Fail(blocked.Message); }
            catch (HttpRequestException) { return StepOutcome.Fail($"Couldn't reach {uri.Host}.", retryable: true); }
            using (res)
            {
                var code = (int)res.StatusCode;
                if (code is 301 or 302 or 303 or 307 or 308 && res.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects) return StepOutcome.Fail($"HTTP {code}: more than {MaxRedirects} redirects.");
                    var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    if (CheckUrl(next.ToString(), out var checkedNext) is { } why) return StepOutcome.Fail($"It redirected somewhere Vantra Flow won't go: {why}");
                    // 303, and a 301 / 302 after a POST, carry on as a GET without the body (as browsers do).
                    if (code == 303 || (code is 301 or 302 && method == "POST")) { method = "GET"; sendBody = false; }
                    uri = checkedNext!;
                    continue;
                }
                var (text, truncated) = await ReadCappedAsync(res, timeout.Token);
                JsonNode? parsed = null;
                if (text.Length > 0 && !truncated && IsJson(text)) parsed = JsonNode.Parse(text);
                var outHeaders = new JsonObject();
                foreach (var h in res.Headers.Concat(res.Content.Headers))
                {
                    if (outHeaders.Count >= MaxResponseHeaders || HiddenResponseHeaders.Contains(h.Key)) continue;
                    var v = string.Join(", ", h.Value);
                    outHeaders[h.Key.ToLowerInvariant()] = v.Length > 500 ? v[..500] : v;
                }
                var output = new JsonObject
                {
                    ["status"] = code,
                    ["ok"] = code is >= 200 and < 300,
                    ["url"] = uri.ToString(),
                    ["headers"] = outHeaders,
                    ["contentType"] = res.Content.Headers.ContentType?.MediaType,
                    ["body"] = parsed ?? JsonValue.Create(text),
                };
                if (truncated) output["truncated"] = true;
                if (code is >= 200 and < 300 || Tokens.Text(input["errors"]) == "continue") return StepOutcome.Done(output);
                var snippet = text.Trim().Length > 0 ? $": {OneLine(text, 200)}" : "";
                return StepOutcome.Fail($"HTTP {code}{snippet}", retryable: code >= 500 || code is 408 or 425 or 429);
            }
        }
    }

    /// <summary>The URL with the step's query parameters added (after any it has).</summary>
    public static Uri WithQuery(Uri uri, JsonObject? query)
    {
        var pairs = (query ?? []).Where(kv => kv.Key.Trim().Length > 0)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key.Trim())}={Uri.EscapeDataString(Tokens.Text(kv.Value))}").ToList();
        if (pairs.Count == 0) return uri;
        var b = new UriBuilder(uri);
        var existing = b.Query.TrimStart('?');
        b.Query = (existing.Length > 0 ? existing + "&" : "") + string.Join("&", pairs);
        return b.Uri;
    }

    private async Task<(string? Value, string? Error)> SecretAsync(Guid workspaceId, string id)
    {
        if (id.Length == 0) return (null, "Choose the secret to send.");
        if (!await schema.StepsReadyAsync()) return (null, "Secrets arrive with the next server update.");
        if (await steps.SecretAsync(workspaceId, id) is not { } row) return (null, "That secret was deleted. Choose another.");
        if (secrets.Unprotect(row.Cipher, workspaceId, row.Id) is not { } value) return (null, "That secret can't be opened. Save it again in Settings.");
        await steps.TouchSecretAsync(workspaceId, row.Id, DateTime.UtcNow);
        return (value, null);
    }

    private static string OneLine(string s, int max)
    {
        var t = System.Text.RegularExpressions.Regex.Replace(s, @"\s+", " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Za-z0-9!#$%&'*+.^_`|~-]{1,64}$")]
    private static partial System.Text.RegularExpressions.Regex HeaderName();

    /// <summary>The body as text, at most <see cref="MaxResponseBytes"/>; whether there was more.</summary>
    private static async Task<(string Text, bool Truncated)> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        var truncated = read > MaxResponseBytes;
        return (Encoding.UTF8.GetString(buffer, 0, Math.Min(read, MaxResponseBytes)), truncated);
    }

    private static bool IsJson(string s)
    {
        var t = s.TrimStart();
        if (t.Length == 0 || (t[0] != '{' && t[0] != '[')) return false;
        try { using var _ = JsonDocument.Parse(s); return true; }
        catch (JsonException) { return false; }
    }

    private static Exception Innermost(Exception e)
    {
        while (e.InnerException is not null) e = e.InnerException;
        return e;
    }
}
