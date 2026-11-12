using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Developer;

/// <summary>The events an outgoing webhook can subscribe to (db/012), and <c>ping</c>, the test.</summary>
public static class DevEventTypes
{
    public const string RunSucceeded = "run.succeeded";
    public const string RunFailed = "run.failed";
    public const string FlowTurnedOn = "flow.turned_on";
    public const string FlowTurnedOff = "flow.turned_off";
    public const string ApprovalRequested = "approval.requested";
    /// <summary>"Send test": not subscribed to, sent on request only.</summary>
    public const string Ping = "ping";

    public static readonly IReadOnlyList<string> All = [RunSucceeded, RunFailed, FlowTurnedOn, FlowTurnedOff, ApprovalRequested];

    public static bool IsKnown(string? s) => s is not null && All.Contains(s, StringComparer.Ordinal);

    public static IReadOnlySet<string> Parse(string? stored) =>
        new HashSet<string>((stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsKnown), StringComparer.Ordinal);

    public static string Format(IEnumerable<string> events)
    {
        var set = events.ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', All.Where(set.Contains));
    }

    /// <summary>A request's events: at least one, each known.</summary>
    public static (IReadOnlyList<string>? Events, string? Error) Validate(IEnumerable<string?>? raw)
    {
        var list = new List<string>();
        foreach (var s in raw ?? [])
        {
            var v = s?.Trim() ?? "";
            if (!IsKnown(v)) return (null, $"Unknown event \"{(v.Length <= 40 ? v : v[..40] + "…")}\". Use {string.Join(", ", All)}.");
            if (!list.Contains(v)) list.Add(v);
        }
        if (list.Count == 0) return (null, "Choose at least one event.");
        return (All.Where(list.Contains).ToList(), null);
    }
}

/// <summary>
/// Signing outgoing webhooks, as Stripe does: <c>X-Vantra-Signature: t={unix seconds},v1={hex HMAC-SHA256}</c> over
/// <c>"{t}.{raw body}"</c> with the endpoint's secret. A receiver checks the HMAC in constant time and refuses a
/// timestamp more than <see cref="Tolerance"/> away from its clock, so a replayed delivery is refused. Pure.
/// </summary>
public static class WebhookSigning
{
    public const string SignatureHeader = "X-Vantra-Signature";
    public const string EventHeader = "X-Vantra-Event";
    public const string EventIdHeader = "X-Vantra-Event-Id";
    public const string DeliveryHeader = "X-Vantra-Delivery";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    /// <summary>A new signing secret: <c>whsec_</c> + 24 random bytes as hex.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string Hint(string secret) => secret.Length <= 4 ? secret : secret[^4..];

    public static byte[] Mac(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var head = Encoding.ASCII.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + ".");
        var message = new byte[head.Length + body.Length];
        head.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(head.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }

    /// <summary>The header's value: <c>t=…,v1=…</c>.</summary>
    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Convert.ToHexStringLower(Mac(secret, timestamp, body))}";

    /// <summary>
    /// What a receiver does: the header's <c>t</c> within <paramref name="tolerance"/> of now, and any <c>v1</c> matching the
    /// HMAC (compared in constant time). Several <c>v1</c> values are allowed, as while a secret is rolled.
    /// </summary>
    public static bool Verify(string secret, string? header, ReadOnlySpan<byte> body, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        long? t = null;
        var sigs = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            var (k, v) = (kv[0].Trim(), kv[1].Trim());
            if (k == "t" && long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) t = ts;
            else if (k == "v1")
            {
                try { sigs.Add(Convert.FromHexString(v)); } catch (FormatException) { /* ignored */ }
            }
        }
        if (t is null || sigs.Count == 0) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - t.Value) > (tolerance ?? Tolerance).TotalSeconds) return false;
        var expected = Mac(secret, t.Value, body);
        var ok = false;
        foreach (var s in sigs) ok |= s.Length == expected.Length && CryptographicOperations.FixedTimeEquals(s, expected);
        return ok;
    }
}

/// <summary>What became of one attempt.</summary>
public enum SendVerdict
{
    /// <summary>2xx.</summary>
    Succeeded,
    /// <summary>408 / 425 / 429 / 5xx, a timeout or a dropped connection: try again later, if attempts remain.</summary>
    Retry,
    /// <summary>3xx, other 4xx, a refused address: no point retrying.</summary>
    Fail,
    /// <summary>410 Gone: fail, and switch the endpoint off.</summary>
    Gone,
}

public sealed record SendResult(SendVerdict Verdict, int? StatusCode, string? Error, int DurationMs, string? Snippet, TimeSpan? RetryAfter);

/// <summary>Delivery rules: the target check, the retry schedule, and when an endpoint is switched off. Pure.</summary>
public static class WebhookRules
{
    /// <summary>Tries per delivery: the first and five retries, over about ten hours.</summary>
    public const int MaxAttempts = 6;
    public static readonly TimeSpan[] RetryDelays = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2), TimeSpan.FromHours(8)];
    public const double RetryJitter = 0.2;
    public static readonly TimeSpan MaxRetryAfter = TimeSpan.FromHours(1);
    /// <summary>An endpoint is switched off when this many deliveries in a row failed every attempt.</summary>
    public const int DisableAfter = 5;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public const int ResponseReadCap = 64 * 1024;
    public const int SnippetMax = 500;
    public const int MaxUrl = 2000;
    public const int MaxEndpoints = 20;

    /// <summary>2xx succeeds; 410 is gone; 408 / 425 / 429 / 5xx retry; everything else fails for good.</summary>
    public static SendVerdict Classify(int status) => status switch
    {
        >= 200 and < 300 => SendVerdict.Succeeded,
        410 => SendVerdict.Gone,
        408 or 425 or 429 or >= 500 => SendVerdict.Retry,
        _ => SendVerdict.Fail,
    };

    /// <summary>
    /// The wait after attempt <paramref name="attempts"/> (1-based) failed: <see cref="RetryDelays"/>[n − 1] ± 20 %
    /// (<paramref name="jitter"/> in [0, 1) picks where), or the receiver's Retry-After (1 s to an hour). Null: none left.
    /// </summary>
    public static TimeSpan? NextDelay(int attempts, TimeSpan? retryAfter, double jitter)
    {
        if (attempts < 1 || attempts >= MaxAttempts) return null;
        if (retryAfter is { } ra) return ra < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : ra > MaxRetryAfter ? MaxRetryAfter : ra;
        var factor = 1 + RetryJitter * (2 * Math.Clamp(jitter, 0, 1) - 1);
        return TimeSpan.FromMilliseconds(RetryDelays[attempts - 1].TotalMilliseconds * factor);
    }

    /// <summary>Switch the endpoint off? After a 410, or <see cref="DisableAfter"/> deliveries in a row that failed every attempt.</summary>
    public static bool ShouldDisable(int failuresInARow, bool gone) => gone || failuresInARow >= DisableAfter;

    public static string DisabledReason(bool gone) => gone
        ? "Switched off: the endpoint answered 410 Gone."
        : $"Switched off: {DisableAfter} deliveries in a row failed every attempt.";

    /// <summary>
    /// A delivery URL, checked up front (the SSRF guard checks the address actually dialled again at connect time):
    /// https only, no credentials in it, a public host name or address. Null and the URI when it's fine.
    /// </summary>
    public static string? CheckUrl(string? raw, out Uri? uri)
    {
        uri = null;
        var text = raw?.Trim() ?? "";
        if (text.Length == 0) return "Enter the URL to send to.";
        if (text.Length > MaxUrl) return $"The URL is over {MaxUrl} characters.";
        if (!Uri.TryCreate(text, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return "The URL must start with https://.";
        if (!string.IsNullOrEmpty(u.UserInfo)) return "Put credentials in your endpoint's own check, not the URL.";
        var host = u.Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (u.IsLoopback || u.HostNameType is UriHostNameType.Basic || host is "localhost" || !host.Contains('.') && !IPAddress.TryParse(host, out _)
            || host.EndsWith(".localhost", StringComparison.Ordinal) || host.EndsWith(".local", StringComparison.Ordinal) || host.EndsWith(".internal", StringComparison.Ordinal))
            return "That address isn't on the public internet.";
        if (IPAddress.TryParse(host, out var ip) && !NetworkGuard.IsPublic(ip)) return "That address is on a private or reserved network, so Vantra Flow won't send to it.";
        uri = u;
        return null;
    }

    /// <summary>At most <see cref="SnippetMax"/> characters of an answer, control characters as spaces; null when empty.</summary>
    public static string? Snippet(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var sb = new StringBuilder(Math.Min(text.Length, SnippetMax));
        foreach (var ch in text)
        {
            if (sb.Length >= SnippetMax) break;
            sb.Append(char.IsControl(ch) ? ' ' : ch);
        }
        var s = sb.ToString().Trim();
        return s.Length == 0 ? null : s;
    }

    public static TimeSpan? RetryAfter(RetryConditionHeaderValue? h, DateTimeOffset now) => h switch
    {
        { Delta: { } d } => d < TimeSpan.Zero ? TimeSpan.Zero : d,
        { Date: { } at } => at > now ? at - now : TimeSpan.Zero,
        _ => null,
    };
}

/// <summary>
/// Sends one attempt: re-checks the URL, signs the body with this attempt's timestamp, POSTs it through the guarded
/// client (NetworkGuard at connect time: public addresses only; no redirects, proxy or cookies; 10 s), and keeps the
/// status and a short snippet of the answer. Never throws for the receiver's sake, never logs a secret or a URL's path.
/// </summary>
public sealed class WebhookSender(IHttpClientFactory http, ILogger<WebhookSender> log, TimeProvider? clock = null)
{
    public const string ClientName = "flow-webhooks";
    private readonly TimeProvider time = clock ?? TimeProvider.System;

    public async Task<SendResult> SendAsync(string url, string? secret, string type, string deliveryId, string eventId, string payload, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        int Ms() => (int)Math.Min(int.MaxValue, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (secret is null) return new SendResult(SendVerdict.Fail, null, "The signing secret can't be read. Roll it.", 0, null, null);
        if (WebhookRules.CheckUrl(url, out var uri) is { } refused) return new SendResult(SendVerdict.Fail, null, refused, 0, null, null);

        var body = Encoding.UTF8.GetBytes(payload);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        req.Headers.TryAddWithoutValidation("User-Agent", "VantraFlow-Webhooks/1.0");
        req.Headers.TryAddWithoutValidation(WebhookSigning.EventHeader, type);
        req.Headers.TryAddWithoutValidation(WebhookSigning.EventIdHeader, eventId);
        req.Headers.TryAddWithoutValidation(WebhookSigning.DeliveryHeader, deliveryId);
        req.Headers.TryAddWithoutValidation(WebhookSigning.SignatureHeader, WebhookSigning.Sign(secret, time.GetUtcNow().ToUnixTimeSeconds(), body));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(WebhookRules.Timeout);
        try
        {
            using var resp = await http.CreateClient(ClientName).SendAsync(req, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var text = await ReadCappedAsync(resp, timeout.Token);
            var status = (int)resp.StatusCode;
            var verdict = WebhookRules.Classify(status);
            var error = verdict == SendVerdict.Succeeded ? null : status is >= 300 and < 400 ? $"HTTP {status}: redirects aren't followed." : $"HTTP {status}";
            return new SendResult(verdict, status, error, Ms(), WebhookRules.Snippet(text), WebhookRules.RetryAfter(resp.Headers.RetryAfter, time.GetUtcNow()));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(SendVerdict.Retry, null, $"No answer within {WebhookRules.Timeout.TotalSeconds:0} seconds.", Ms(), null, null);
        }
        catch (HttpRequestException ex) when (Blocked(ex) is { } b)
        {
            return new SendResult(SendVerdict.Fail, null, b.Message, Ms(), null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            log.LogInformation("Webhook delivery {Delivery} to {Host} couldn't connect", deliveryId, uri!.Host);
            return new SendResult(SendVerdict.Retry, null, $"Couldn't reach {uri!.Host}.", Ms(), null, null);
        }
    }

    private static async Task<string?> ReadCappedAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        int n;
        while (ms.Length < WebhookRules.ResponseReadCap && (n = await stream.ReadAsync(buffer, ct)) > 0) ms.Write(buffer, 0, n);
        return ms.Length == 0 ? null : Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)Math.Min(ms.Length, WebhookRules.SnippetMax * 4));
    }

    private static WebhookBlockedException? Blocked(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException)
            if (ex is WebhookBlockedException b) return b;
        return null;
    }
}

/// <summary>The JSON the API answers with and webhooks carry: one shape for a flow, a run and an approval.</summary>
public static class DevJson
{
    public static string Iso(DateTime d) => Controllers.FlowControllerBase.Iso(d);
    public static string? Iso(DateTime? d) => Controllers.FlowControllerBase.Iso(d);

    public static JsonObject Run(RunRow r) => new()
    {
        ["id"] = r.Id,
        ["flowId"] = r.FlowId,
        ["flowName"] = r.FlowName,
        ["flowVersion"] = r.FlowVersion,
        ["status"] = r.Status,
        ["test"] = r.Test,
        ["replayOf"] = r.ReplayOf,
        ["startedAt"] = Iso(r.StartedAt),
        ["finishedAt"] = Iso(r.FinishedAt),
        ["durationMs"] = r.DurationMs,
        ["stepCount"] = r.StepCount,
        ["error"] = r.Error,
    };

    public static JsonObject Flow(FlowRow f, Engine.FlowDefinition? def)
    {
        var apps = new List<string>();
        if (def is not null)
        {
            if (def.Trigger.App.Length > 0) apps.Add(def.Trigger.App);
            apps.AddRange(def.AllSteps().Where(x => x.Type == Engine.StepTypes.Action && x.App is { Length: > 0 }).Select(x => x.App!));
        }
        return new JsonObject
        {
            ["id"] = f.Id,
            ["name"] = f.Name,
            ["description"] = f.Description,
            ["enabled"] = f.Enabled,
            ["trigger"] = new JsonObject { ["app"] = f.TriggerApp, ["event"] = f.TriggerEvent },
            ["apps"] = new JsonArray(apps.Distinct().Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
            ["stepCount"] = def?.AllSteps().Count() ?? 0,
            ["version"] = f.Version,
            ["pausedReason"] = f.PausedReason,
            ["lastRunAt"] = Iso(f.LastRunAt),
            ["lastRunStatus"] = f.LastRunStatus,
            ["createdAt"] = Iso(f.CreatedAt),
            ["updatedAt"] = Iso(f.UpdatedAt),
        };
    }

    public static JsonObject Approval(ApprovalRow a) => new()
    {
        ["id"] = a.Id,
        ["flowId"] = a.FlowId,
        ["flowName"] = a.FlowName,
        ["runId"] = a.RunId,
        ["stepId"] = a.StepId,
        ["title"] = a.Title,
        ["details"] = a.Details,
        ["approvers"] = new JsonArray(Connectors.ApprovalGate.Approvers(a.Approvers)
            .Select(x => (JsonNode)new JsonObject { ["name"] = x.Name ?? x.Email ?? "Someone" }).ToArray()),
        ["requestedAt"] = Iso(a.RequestedAt),
        ["expiresAt"] = Iso(a.ExpiresAt),
    };

    /// <summary>An event's body: <c>{ id, type, createdAt, workspaceId, test?, data }</c>.</summary>
    public static JsonObject Event(string id, string type, DateTime at, Guid workspaceId, JsonObject data, bool test = false)
    {
        var o = new JsonObject { ["id"] = id, ["type"] = type, ["createdAt"] = Iso(at), ["workspaceId"] = workspaceId.ToString("D") };
        if (test) o["test"] = true;
        o["data"] = data;
        return o;
    }

    public static string NewEventId() => Connectors.FlowSecrets.NewRowId("evt_");
    public static string NewDeliveryId() => Connectors.FlowSecrets.NewRowId("whd_");

    /// <summary>"Send test": a ping, or an example of one of the events (marked <c>test</c>).</summary>
    public static JsonObject Sample(string type, Guid workspaceId, string endpointId, string url, DateTime now)
    {
        var flow = new JsonObject
        {
            ["id"] = "flw_example", ["name"] = "New deal to Slack", ["description"] = null, ["enabled"] = type != DevEventTypes.FlowTurnedOff,
            ["trigger"] = new JsonObject { ["app"] = "orbit", ["event"] = "record.created" }, ["apps"] = new JsonArray("orbit", "slack"),
            ["stepCount"] = 2, ["version"] = 4, ["pausedReason"] = null, ["lastRunAt"] = Iso(now.AddMinutes(-12)), ["lastRunStatus"] = "succeeded",
            ["createdAt"] = Iso(now.AddDays(-20)), ["updatedAt"] = Iso(now.AddMinutes(-1)),
        };
        var run = new JsonObject
        {
            ["id"] = "frn_example", ["flowId"] = "flw_example", ["flowName"] = "New deal to Slack", ["flowVersion"] = 4,
            ["status"] = type == DevEventTypes.RunFailed ? "failed" : "succeeded", ["test"] = false, ["replayOf"] = null,
            ["startedAt"] = Iso(now.AddSeconds(-3)), ["finishedAt"] = Iso(now), ["durationMs"] = 2840, ["stepCount"] = 2,
            ["error"] = type == DevEventTypes.RunFailed ? "Slack said the channel wasn't found." : null,
        };
        JsonObject data = type switch
        {
            DevEventTypes.RunSucceeded or DevEventTypes.RunFailed => new JsonObject { ["run"] = run },
            DevEventTypes.FlowTurnedOn or DevEventTypes.FlowTurnedOff => new JsonObject { ["flow"] = flow },
            DevEventTypes.ApprovalRequested => new JsonObject
            {
                ["approval"] = new JsonObject
                {
                    ["id"] = "apr_example", ["flowId"] = "flw_example", ["flowName"] = "Refunds over £500", ["runId"] = "frn_example", ["stepId"] = "s3",
                    ["title"] = "Refund £640 to Hannah Lee?", ["details"] = "Order 10482 · damaged in transit", ["approvers"] = new JsonArray(new JsonObject { ["name"] = "Alex Morgan" }),
                    ["requestedAt"] = Iso(now), ["expiresAt"] = Iso(now.AddDays(3)),
                },
            },
            _ => new JsonObject { ["endpoint"] = new JsonObject { ["id"] = endpointId, ["url"] = url } },
        };
        return Event(NewEventId(), type, now, workspaceId, data, test: true);
    }
}
