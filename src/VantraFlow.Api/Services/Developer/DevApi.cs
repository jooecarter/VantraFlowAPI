using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services.Developer;

/// <summary>Fixed values of the developer API (<c>/api/v1</c>) and its keys (db/012). Orbit's, for Flow.</summary>
public static class DevConstants
{
    /// <summary>Every key starts with this, then 43 base64url characters (32 random bytes).</summary>
    public const string KeyPrefix = "vfk_";
    public const string KeyPattern = "^vfk_[A-Za-z0-9_-]{43}$";
    /// <summary>How much of a key is kept in the clear, to find it by and to show in lists (<c>vfk_</c> and 8 more).</summary>
    public const int PrefixLength = 12;
    public const int MaxName = 80;
    public const int MaxExpiresInDays = 365;
    /// <summary>Active keys (not revoked, not expired) a workspace may hold.</summary>
    public const int MaxActiveKeys = 50;
    public const int RateLimitPerMinute = 120;
    public const int DefaultLimit = 25;
    public const int MaxLimit = 100;
    public const int MaxBodyBytes = 256 * 1024;
    public const int MaxIdempotencyKey = 255;
    public static readonly TimeSpan IdempotencyWindow = TimeSpan.FromHours(24);
    /// <summary>A key's last use is written at most this often.</summary>
    public static readonly TimeSpan TouchEvery = TimeSpan.FromMinutes(1);
}

/// <summary>What a key may do. A key holds one or more; none implies another.</summary>
public static class DevScopes
{
    public const string FlowsRead = "flows:read";
    public const string FlowsWrite = "flows:write";
    public const string RunsRead = "runs:read";
    /// <summary>Run a flow now, and replay a run.</summary>
    public const string RunsWrite = "runs:write";
    public const string WebhooksManage = "webhooks:manage";

    public static readonly IReadOnlyList<string> All = [FlowsRead, FlowsWrite, RunsRead, RunsWrite, WebhooksManage];

    public static bool IsKnown(string? s) => s is not null && All.Contains(s, StringComparer.Ordinal);

    /// <summary>Stored scopes (space-separated) as a set; anything unknown is dropped (fails closed).</summary>
    public static IReadOnlySet<string> Parse(string? stored) =>
        new HashSet<string>((stored ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(IsKnown), StringComparer.Ordinal);

    /// <summary>A set as stored: known scopes only, in <see cref="All"/>'s order.</summary>
    public static string Format(IEnumerable<string> scopes)
    {
        var set = scopes.ToHashSet(StringComparer.Ordinal);
        return string.Join(' ', All.Where(set.Contains));
    }

    /// <summary>A create request's scopes: the error, or the set (at least one, each known).</summary>
    public static (IReadOnlyList<string>? Scopes, string? Error) Validate(IEnumerable<string?>? raw)
    {
        var list = new List<string>();
        foreach (var s in raw ?? [])
        {
            var v = s?.Trim() ?? "";
            if (!IsKnown(v)) return (null, $"Unknown scope \"{Clip(v)}\". Use {string.Join(", ", All)}.");
            if (!list.Contains(v)) list.Add(v);
        }
        if (list.Count == 0) return (null, "Choose at least one scope.");
        return (All.Where(list.Contains).ToList(), null);
    }

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "…";
}

public static class DevKeyStatuses
{
    public const string Active = "active";
    public const string Expired = "expired";
    public const string Revoked = "revoked";

    public static string Of(DateTime? revokedAt, DateTime? expiresAt, DateTime now) =>
        revokedAt is not null ? Revoked : expiresAt is { } e && e <= now ? Expired : Active;
}

/// <summary>
/// Making, hashing and recognising keys. Pure. What's stored: <see cref="Prefix"/> (to look the key up by) and
/// <see cref="Hash"/> (SHA-256 of the whole key); <see cref="Matches"/> compares in constant time.
/// </summary>
public static partial class DevKeys
{
    [GeneratedRegex(DevConstants.KeyPattern, RegexOptions.CultureInvariant)]
    private static partial Regex KeyRx();

    /// <summary>A new key: <c>vfk_</c> + 32 random bytes as base64url (43 characters).</summary>
    public static string NewKey() => DevConstants.KeyPrefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    /// <summary>A new key id: <c>key_</c> + 24 hex characters.</summary>
    public static string NewId() => "key_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(12));

    public static bool IsKey(string? s) => s is not null && KeyRx().IsMatch(s);

    /// <summary>SHA-256 of the key's UTF-8.</summary>
    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>The lookup prefix: the key's first <see cref="DevConstants.PrefixLength"/> characters (<c>vfk_Ab3dE9xQ</c>).</summary>
    public static string Prefix(string key) => key[..Math.Min(DevConstants.PrefixLength, key.Length)];

    /// <summary>Constant time: does <paramref name="presented"/> hash to <paramref name="stored"/>?</summary>
    public static bool Matches(string presented, byte[]? stored) =>
        stored is { Length: 32 } && CryptographicOperations.FixedTimeEquals(Hash(presented), stored);

    /// <summary>The key in <c>Authorization: Bearer vfk_…</c>, or null when there's none or it isn't one.</summary>
    public static string? FromAuthorization(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var h = header.Trim();
        if (!h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return null;
        var key = h[7..].Trim();
        return IsKey(key) ? key : null;
    }

    /// <summary>A key's name: trimmed, one line, 1–80 characters. Null when it isn't one.</summary>
    public static string? Name(string? raw)
    {
        if (raw is null) return null;
        var s = string.Join(' ', raw.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length is >= 1 and <= DevConstants.MaxName && !s.Any(char.IsControl) ? s : null;
    }
}

/// <summary>
/// The error codes of <c>/api/v1</c> (<c>{ error: { code, message } }</c>). Where the studio's API has a code for the same
/// case, it's the same one (<c>no_flow</c>, <c>not_ready</c>, <c>plan_premium</c>, <c>plan_flows</c>, <c>plan_runs</c>,
/// <c>subscribe_failed</c>).
/// </summary>
public static class DevCodes
{
    public const string BadRequest = "bad_request";
    public const string Unauthorized = "unauthorized";
    public const string KeyRevoked = "key_revoked";
    public const string KeyExpired = "key_expired";
    public const string KeyInactive = "key_inactive";
    public const string PlanApi = "plan_api";
    public const string MissingScope = "missing_scope";
    public const string NoFlow = "no_flow";
    public const string NoRun = "no_run";
    public const string NotFound = "not_found";
    public const string IdempotencyConflict = "idempotency_conflict";
    public const string TooLarge = "too_large";
    public const string InvalidFlow = "invalid_flow";
    public const string RateLimited = "rate_limited";
    public const string SubscribeFailed = "subscribe_failed";
    public const string NotReady = "not_ready";
    public const string ServerError = "server_error";
}

/// <summary>
/// <see cref="DevConstants.RateLimitPerMinute"/> requests a minute per key, in fixed windows on the minute, on this
/// instance (each App Service instance counts its own). Thread-safe; old windows are swept as it goes.
/// </summary>
public sealed class DevRateLimiter(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, Window> windows = new(StringComparer.Ordinal);
    private long lastSweep;

    private sealed class Window(long minute)
    {
        public readonly long Minute = minute;
        public int Count;
    }

    /// <summary>One request's answer: allowed or not, what's left, when the window resets (Unix seconds), and the wait.</summary>
    public readonly record struct Decision(bool Allowed, int Limit, int Remaining, long ResetUnix, int RetryAfterSeconds);

    public Decision Take(string keyId, int limit = DevConstants.RateLimitPerMinute)
    {
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var minute = now / 60;
        var reset = (minute + 1) * 60;
        Sweep(minute);
        var w = windows.AddOrUpdate(keyId, _ => new Window(minute), (_, old) => old.Minute == minute ? old : new Window(minute));
        var n = Interlocked.Increment(ref w.Count);
        if (n > limit)
        {
            Interlocked.Decrement(ref w.Count);
            return new Decision(false, limit, 0, reset, (int)Math.Max(1, reset - now));
        }
        return new Decision(true, limit, limit - n, reset, 0);
    }

    private void Sweep(long minute)
    {
        if (Interlocked.Exchange(ref lastSweep, minute) == minute) return;
        foreach (var (k, w) in windows)
            if (w.Minute < minute) windows.TryRemove(new KeyValuePair<string, Window>(k, w));
    }
}

/// <summary>An Idempotency-Key's row: the request's hash, and the answer once there is one (<c>Status</c> 0: still running).</summary>
public sealed record IdempotencyRow(string KeyId, string IdemKey, byte[] RequestHash, int Status, string? Response, DateTime CreatedAt);

public enum IdempotencyVerdict
{
    /// <summary>First time: go ahead (the key is now claimed).</summary>
    Proceed,
    /// <summary>The same request again: answer as before.</summary>
    Replay,
    /// <summary>The key was used for a different request.</summary>
    Mismatch,
    /// <summary>The first request with this key hasn't finished.</summary>
    InProgress,
}

/// <summary>Idempotency-Key, as Stripe's: the same key and request within a day answers as the first did. Pure.</summary>
public static class Idempotency
{
    /// <summary>The key as sent: 1–255 printable characters, or the error.</summary>
    public static (string? Key, string? Error) Read(string? header)
    {
        if (header is null) return (null, null);
        var k = header.Trim();
        if (k.Length == 0) return (null, null);
        if (k.Length > DevConstants.MaxIdempotencyKey || k.Any(char.IsControl))
            return (null, $"Idempotency-Key must be 1–{DevConstants.MaxIdempotencyKey} printable characters.");
        return (k, null);
    }

    /// <summary>What identifies a request: what it acts on, then its body.</summary>
    public static byte[] RequestHash(string target, ReadOnlySpan<byte> body)
    {
        var head = Encoding.UTF8.GetBytes(target + "\n");
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all.AsSpan(head.Length));
        return SHA256.HashData(all);
    }

    /// <summary>What to do given the row already kept for this key (null: none, so the claim was ours).</summary>
    public static IdempotencyVerdict Decide(IdempotencyRow? existing, byte[] hash, DateTime now)
    {
        if (existing is null || existing.CreatedAt < now - DevConstants.IdempotencyWindow) return IdempotencyVerdict.Proceed;
        if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, hash)) return IdempotencyVerdict.Mismatch;
        return existing.Status == 0 || existing.Response is null ? IdempotencyVerdict.InProgress : IdempotencyVerdict.Replay;
    }
}

/// <summary>
/// Opaque list cursors: the position of the last item handed out, bound to the query that made it (a cursor from
/// another query is refused). Base64url of <c>"{signature}|{a}|{b}"</c>.
/// </summary>
public static class DevCursor
{
    public static string Encode(string signature, string a, string b) =>
        Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{Sig(signature)}|{a}|{b}"));

    public static (string A, string B)? Decode(string? cursor, string signature)
    {
        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 400) return null;
        try
        {
            var text = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(cursor.Trim()));
            var parts = text.Split('|');
            return parts.Length == 3 && parts[0] == Sig(signature) ? (parts[1], parts[2]) : null;
        }
        catch (FormatException) { return null; }
    }

    private static string Sig(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..12];
}
