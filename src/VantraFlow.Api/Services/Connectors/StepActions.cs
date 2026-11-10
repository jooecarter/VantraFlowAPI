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
public sealed class StepActions(Connections connections, EmailService email, EngineRepository engine, IHttpClientFactory http, FlowLimits limits)
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
                "http" => await HttpAsync(call.Input, ct),
                _ => StepOutcome.Fail("This step isn't something Vantra Flow can do."),
            };
        }
        catch (AppUnavailableException e) { return StepOutcome.Fail(e.Message, retryable: true); }
        catch (AppRevokedException e) { return StepOutcome.Fail(e.Message); }
        catch (AppRefusedException e) { return StepOutcome.Fail(e.Message, retryable: e.Status is 409 or 423); }
    }

    // ── Email ──

    private async Task<StepOutcome> EmailAsync(StepCall call, CancellationToken ct)
    {
        var to = Recipients(Tokens.Text(call.Input["to"]));
        if (to.Error is not null) return StepOutcome.Fail(to.Error);
        var subject = Tokens.Text(call.Input["subject"]).Trim();
        if (subject.Length == 0) return StepOutcome.Fail("The subject is empty.");
        if (subject.Length > 200) subject = subject[..200];
        if (!email.CanSend()) return StepOutcome.Fail("Email isn't set up on this server yet.");
        var body = Tokens.Text(call.Input["body"]);
        var replyTo = Tokens.Text(call.Input["replyTo"]).Trim();
        var fromName = Tokens.Text(call.Input["fromName"]).Trim() is { Length: > 0 } n ? n : "Vantra Flow";
        var html = Html(body);
        var sent = new JsonArray();
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
        return StepOutcome.Done(new JsonObject { ["to"] = sent, ["subject"] = subject, ["sent"] = sent.Count });
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

    private async Task<StepOutcome> HttpAsync(JsonObject input, CancellationToken ct)
    {
        var method = (Tokens.Text(input["method"]) is { Length: > 0 } m ? m : "POST").ToUpperInvariant();
        if (!Methods.Contains(method)) return StepOutcome.Fail("Choose GET, POST, PUT, PATCH or DELETE.");
        if (CheckUrl(Tokens.Text(input["url"]), out var uri) is { } bad) return StepOutcome.Fail(bad);
        using var req = new HttpRequestMessage(new HttpMethod(method), uri);
        var body = Tokens.Text(input["body"]);
        if (method != "GET" && body.Length > 0)
        {
            var json = IsJson(body);
            req.Content = new StringContent(body, Encoding.UTF8, json ? "application/json" : "text/plain");
        }
        if (input["headers"] is JsonObject headers)
            foreach (var (name, value) in headers)
            {
                if (string.IsNullOrWhiteSpace(name) || BlockedHeaders.Contains(name)) continue;
                var v = Tokens.Text(value);
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                {
                    if (req.Content is not null && MediaTypeHeaderValue.TryParse(v, out var ctype)) req.Content.Headers.ContentType = ctype;
                    continue;
                }
                req.Headers.TryAddWithoutValidation(name.Trim(), v);
            }
        req.Headers.UserAgent.ParseAdd("VantraFlow/1.0");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HttpTimeout);
        HttpResponseMessage res;
        try { res = await http.CreateClient(HttpClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return StepOutcome.Fail($"No answer within {HttpTimeout.TotalSeconds:0} seconds.", retryable: true); }
        catch (HttpRequestException e) when (Innermost(e) is WebhookBlockedException blocked) { return StepOutcome.Fail(blocked.Message); }
        catch (HttpRequestException) { return StepOutcome.Fail($"Couldn't reach {uri!.Host}.", retryable: true); }
        using (res)
        {
            var text = await ReadCappedAsync(res, timeout.Token);
            JsonNode? parsed = null;
            if (text.Length > 0 && IsJson(text)) parsed = JsonNode.Parse(text);
            var output = new JsonObject
            {
                ["status"] = (int)res.StatusCode,
                ["contentType"] = res.Content.Headers.ContentType?.MediaType,
                ["body"] = parsed ?? JsonValue.Create(text),
            };
            var code = (int)res.StatusCode;
            if (code is >= 200 and < 300) return StepOutcome.Done(output);
            if (code is >= 300 and < 400) return StepOutcome.Fail($"HTTP {code}: redirects aren't followed.");
            return StepOutcome.Fail($"HTTP {code}", retryable: code >= 500 || code is 408 or 425 or 429);
        }
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxResponseBytes];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), ct);
            if (n == 0) break;
            read += n;
        }
        return Encoding.UTF8.GetString(buffer, 0, read);
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
