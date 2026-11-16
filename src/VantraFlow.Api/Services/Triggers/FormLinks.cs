using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>A form's link as the studio shows it.</summary>
public sealed record FormLinkDto(string Url, string Slug, string Access, bool ShowInApps);

/// <summary>
/// A form trigger's row in <c>flow.FlowForms</c> (db/014), kept in step with its flow: made on the first save with a form
/// trigger (a new unguessable token, the slug made unique in the workspace), updated on each save and switch, removed
/// when the flow stops being a form or is deleted. The link is <c>{App:BaseUrl}/f/{token}/{slug}</c>: the token finds the
/// form, the slug is for people to read.
/// </summary>
public sealed class FormLinks(TriggersRepository repo, IConfiguration config)
{
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://flow.vantrasoftware.com").TrimEnd('/');

    public string Url(FlowFormRow f) => $"{AppBase}/f/{f.Token}/{f.Slug}";

    public FormLinkDto Dto(FlowFormRow f) => new(Url(f), f.Slug, f.Access, f.ShowInApps);

    /// <summary>A form's token: 12 characters of [a-z0-9] (about 62 bits), so a public link can't be guessed.</summary>
    public static string NewToken()
    {
        const string alphabet = "abcdefghijkmnpqrstuvwxyz23456789";
        Span<byte> bytes = stackalloc byte[12];
        RandomNumberGenerator.Fill(bytes);
        var sb = new StringBuilder(12);
        foreach (var b in bytes) sb.Append(alphabet[b % alphabet.Length]);
        return sb.ToString();
    }

    /// <summary>Brings the flow's form row in line with its definition; the row (null when it isn't a form, or db/014 hasn't run).</summary>
    public async Task<FlowFormRow?> SyncAsync(FlowRow flow, FlowDefinition def)
    {
        if (!await repo.ReadyAsync()) return null;
        var existing = await repo.FormByFlowAsync(flow.Id);
        if (!FormRules.IsForm(def.Trigger))
        {
            if (existing is not null) await repo.DeleteFormAsync(flow.Id);
            return null;
        }
        var spec = FormRules.Read(def.Trigger.Config);
        var title = spec.Title.Length > 0 ? spec.Title : flow.Name;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var slug = attempt == 0 ? spec.Slug : $"{(spec.Slug.Length > 54 ? spec.Slug[..54] : spec.Slug)}-{attempt + 1}";
            if (await repo.SlugTakenAsync(flow.WorkspaceId, slug, flow.Id)) continue;
            var row = new FlowFormRow(existing?.Id ?? FlowSecrets.NewRowId("frm_"), flow.WorkspaceId, flow.Id, slug, existing?.Token ?? NewToken(),
                title.Length > 160 ? title[..160] : title, spec.Access, flow.Enabled, spec.ShowInApps, existing?.CreatedAt ?? DateTime.UtcNow, DateTime.UtcNow);
            if (await repo.SaveFormAsync(row)) return await repo.FormByFlowAsync(flow.Id);
        }
        return existing;
    }

    public async Task<FormLinkDto?> LinkAsync(string flowId) =>
        await repo.ReadyAsync() && await repo.FormByFlowAsync(flowId) is { } f ? Dto(f) : null;

    public async Task PublishedAsync(string flowId, bool on)
    {
        if (await repo.ReadyAsync()) await repo.SetPublishedAsync(flowId, on);
    }

    public async Task ForgetAsync(string flowId)
    {
        if (await repo.ReadyAsync()) await repo.DeleteFormAsync(flowId);
    }
}

/// <summary>
/// The public form page's guards, as Orbit's public forms: a stamp signed when the page loads (an answer sent under two
/// seconds later is a bot's: it gets a pretend success; over a day old, the page reloads), an address kept only as an
/// HMAC prefix (never the address), and Cloudflare Turnstile when <c>Forms:Turnstile:SiteKey</c> and <c>SecretKey</c> are
/// set — the same settings Orbit's public forms use.
/// </summary>
public sealed class FormGuard(IConfiguration config, IHttpClientFactory? http = null)
{
    public static readonly TimeSpan MinAge = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const string TurnstileVerifyUrl = "https://challenges.cloudflare.com/turnstile/v0/siteverify";

    private byte[] KeyFor(string purpose) =>
        HMACSHA256.HashData(Encoding.UTF8.GetBytes(config["Jwt:Key"] ?? "vantra-flow-forms"), Encoding.UTF8.GetBytes("vantra-flow-forms:" + purpose));

    public string IssueStamp(string token, DateTime now)
    {
        var at = new DateTimeOffset(now, TimeSpan.Zero).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"{at}.{Convert.ToHexStringLower(HMACSHA256.HashData(KeyFor("stamp"), Encoding.UTF8.GetBytes($"{token}|{at}")))[..32]}";
    }

    /// <summary><c>ok</c>, <c>fast</c> (under two seconds), <c>stale</c> (over a day) or <c>bad</c>.</summary>
    public string CheckStamp(string token, string? stamp, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(stamp) || stamp.Length > 80 || stamp.Split('.') is not [var at, var mac]) return "bad";
        if (!long.TryParse(at, NumberStyles.None, CultureInfo.InvariantCulture, out var secs)) return "bad";
        var want = Convert.ToHexStringLower(HMACSHA256.HashData(KeyFor("stamp"), Encoding.UTF8.GetBytes($"{token}|{at}")))[..32];
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(want), Encoding.ASCII.GetBytes(mac))) return "bad";
        var age = now - DateTimeOffset.FromUnixTimeSeconds(secs).UtcDateTime;
        if (age < MinAge) return "fast";
        if (age > MaxAge) return "stale";
        return "ok";
    }

    /// <summary>The address as a 16-character HMAC prefix: enough to count by, never the address.</summary>
    public string? IpHash(IPAddress? ip) =>
        ip is null ? null : Convert.ToHexStringLower(HMACSHA256.HashData(KeyFor("ip"), ip.GetAddressBytes()))[..16];

    private string? SiteKey => config["Forms:Turnstile:SiteKey"] is { Length: > 0 } k ? k : null;
    private string? SecretKey => config["Forms:Turnstile:SecretKey"] is { Length: > 0 } k ? k : null;
    public bool CaptchaEnabled => SiteKey is not null && SecretKey is not null;
    public object? Captcha => CaptchaEnabled ? new { kind = "turnstile", siteKey = SiteKey } : null;

    public async Task<bool> VerifyCaptchaAsync(string? token, IPAddress? ip, CancellationToken ct)
    {
        if (!CaptchaEnabled) return true;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || http is null) return false;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var form = new Dictionary<string, string> { ["secret"] = SecretKey!, ["response"] = token };
            if (ip is not null) form["remoteip"] = ip.ToString();
            using var res = await http.CreateClient().PostAsync(TurnstileVerifyUrl, new FormUrlEncodedContent(form), timeout.Token);
            if (!res.IsSuccessStatusCode) return false;
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
            return doc.RootElement.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException) { return false; }
    }
}

/// <summary>Assertion nonces spent in <c>flow.FiredKeys</c> (db/001; pruned after a week, long after any assertion expires).</summary>
public sealed class FiredKeyNonces(EngineRepository engine) : INonceStore
{
    public Task<bool> SpendAsync(string audience, string nonce) => engine.ClaimKeyAsync(Guid.Empty, "", $"aj:{audience}:{nonce}");
}
