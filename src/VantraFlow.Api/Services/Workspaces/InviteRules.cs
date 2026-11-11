using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace VantraFlow.Api.Services.Workspaces;

/// <summary>What opening (accepting) an invitation comes to.</summary>
public enum InviteOutcome
{
    /// <summary>Join: add the member, mark the invite accepted.</summary>
    Accept,
    /// <summary>Already in the workspace (a re-opened link): 200, open it.</summary>
    AlreadyMember,
    /// <summary>Signed in as someone other than the invited address: 409 <c>wrong_account</c>.</summary>
    WrongAccount,
    /// <summary>Accepted already, by someone not (or no longer) a member: 410 <c>invite_used</c>.</summary>
    Used,
    /// <summary>Withdrawn by an owner or admin: 410 <c>invite_revoked</c>.</summary>
    Revoked,
    /// <summary>Open but past its 14 days: 410 <c>invite_expired</c>.</summary>
    Expired,
    /// <summary>No such invitation: 404 <c>invite_invalid</c>.</summary>
    Invalid,
}

/// <summary>
/// The pure rules for invitations (db/005's <c>flow.Invites</c>), as Vantra Orbit's: one row per (workspace,
/// lower-cased email), open while <c>pending</c> (not emailed) or <c>sent</c>, for <see cref="ValidDays"/> days from
/// when it was issued or last resent. Unlike Orbit's, the link's token is 32 random bytes that are never stored: the
/// row keeps its SHA-256, so a copy of the database (or a log) can't be used to join. A resend therefore makes a new
/// link and the old one stops working. Tokens travel in request bodies, never in a URL the API sees.
/// </summary>
public static class InviteRules
{
    public const int ValidDays = 14;
    public const int DailyLimitPerWorkspace = 100;
    public static readonly TimeSpan ResendCooldown = TimeSpan.FromSeconds(60);

    public const string Pending = "pending";
    public const string Sent = "sent";
    public const string Accepted = "accepted";
    public const string Revoked = "revoked";
    public const string Expired = "expired"; // reported, never stored

    public static bool IsOpen(string? status) => status is Pending or Sent;

    /// <summary>Same address, ignoring case and surrounding space. An unknown account email never matches.</summary>
    public static bool SameEmail(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The decision for the signed-in account (its identity-DB email) opening this invitation. An invite for someone
    /// else is refused before anything about its state is said; a used link isn't a broken one for whoever is in.
    /// </summary>
    public static InviteOutcome Evaluate(string status, DateTime expiresAt, DateTime nowUtc, string inviteEmail, string? accountEmail, bool isMember)
    {
        if (!SameEmail(inviteEmail, accountEmail)) return InviteOutcome.WrongAccount;
        if (isMember) return InviteOutcome.AlreadyMember;
        return Preview(status, expiresAt, nowUtc);
    }

    /// <summary>The anonymous preview: open and in date is shown; anything else says why not.</summary>
    public static InviteOutcome Preview(string status, DateTime expiresAt, DateTime nowUtc) => status switch
    {
        Accepted => InviteOutcome.Used,
        Revoked => InviteOutcome.Revoked,
        Pending or Sent => expiresAt <= nowUtc ? InviteOutcome.Expired : InviteOutcome.Accept,
        _ => InviteOutcome.Invalid,
    };

    /// <summary>Status as listed to managers: an open invite past its date reads <c>expired</c>.</summary>
    public static string ListStatus(string status, DateTime expiresAt, DateTime nowUtc) =>
        IsOpen(status) && expiresAt <= nowUtc ? Expired : status;

    public static bool CanResend(DateTime? sentAt, DateTime nowUtc) => sentAt is not { } s || nowUtc - s >= ResendCooldown;

    public static int RetryAfterSeconds(DateTime? sentAt, DateTime nowUtc) =>
        sentAt is { } s && nowUtc - s < ResendCooldown ? (int)Math.Ceiling((ResendCooldown - (nowUtc - s)).TotalSeconds) : 0;

    /// <summary>A single, plain email address, trimmed and lower-cased; null when it isn't one.</summary>
    public static string? NormalizeEmail(string? email)
    {
        var e = email?.Trim();
        if (string.IsNullOrEmpty(e) || e.Length > 320) return null;
        if (!MailAddress.TryCreate(e, out var a) || !string.Equals(a.Address, e, StringComparison.Ordinal) || !e.Contains('.', StringComparison.Ordinal)) return null;
        return e.ToLowerInvariant();
    }

    /// <summary>The invited address masked for the public preview: <c>jane@example.com</c> → <c>j•••@example.com</c>.</summary>
    public static string EmailHint(string email)
    {
        var at = email.LastIndexOf('@');
        return at <= 0 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    // ── Tokens ──

    public const int TokenBytes = 32;

    /// <summary>A new link token: 32 random bytes, base64url (43 characters).</summary>
    public static string NewToken() => Base64Url(RandomNumberGenerator.GetBytes(TokenBytes));

    /// <summary>Whether <paramref name="token"/> has a token's shape (43 base64url characters); anything else is never looked up.</summary>
    public static bool IsTokenShape(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>What's stored for a token: its SHA-256 (32 bytes).</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── Links ──

    /// <summary>
    /// <paramref name="origin"/> when it is one of the studio's own origins (<c>Cors:AllowedOrigins</c> or
    /// <c>App:BaseUrl</c>); otherwise null. Anyone can send any Origin header from outside a browser, and an emailed
    /// invitation must never point anywhere else.
    /// </summary>
    public static string? TrustedOrigin(string? origin, IEnumerable<string>? allowedOrigins, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        static string Norm(string s) => s.Trim().TrimEnd('/');
        var o = Norm(origin);
        return (allowedOrigins ?? []).Append(baseUrl ?? "")
            .Any(a => !string.IsNullOrWhiteSpace(a) && string.Equals(Norm(a), o, StringComparison.OrdinalIgnoreCase)) ? o : null;
    }

    /// <summary>The join link: <c>{studio}/invite/{token}</c>.</summary>
    public static string Link(string? origin, string? baseUrl, string token)
    {
        var app = (!string.IsNullOrWhiteSpace(origin) ? origin : !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : "https://flow.vantrasoftware.com").TrimEnd('/');
        return $"{app}/invite/{token}";
    }

    /// <summary>The studio's address for the workspace (the note to someone added straight away).</summary>
    public static string WorkspaceLink(string? origin, string? baseUrl, Guid workspaceId)
    {
        var app = (!string.IsNullOrWhiteSpace(origin) ? origin : !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : "https://flow.vantrasoftware.com").TrimEnd('/');
        return $"{app}/flows?ws={workspaceId}";
    }
}
