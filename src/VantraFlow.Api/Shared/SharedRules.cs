using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;

namespace VantraFlow.Api.Shared;

/// <summary>
/// Who may change a shared workspace's members, roles and app access. Pure: the store reads the state under a lock
/// and applies the plan. Every app's endpoints go through these, so the rules are the same wherever a change is made.
/// <list type="bullet">
/// <item>Owners and admins manage; only an owner makes someone an owner or changes, removes or edits an owner's access.</item>
/// <item>The main owner (<c>vantra.Workspaces.OwnerVantraId</c>) is always an owner, keeps every app the workspace
/// has, and can't be removed or leave: they hand the workspace over first. So there is always an owner, in every app.</item>
/// <item>Guests stay guests: no role change moves anyone across the guest line, either way.</item>
/// <item>Guests get no access to Flow.</item>
/// <item>Access is only to apps the workspace has. Nobody's own role or access is changed by themselves (except
/// giving yourself an app as a manager), so nobody locks themselves out.</item>
/// </list>
/// </summary>
public static class SharedRules
{
    /// <summary>Why an invitation can't be made, or null when it can.</summary>
    public static string? CheckInvite(SharedState s, string callerId, string? role, string? guestType, IReadOnlyList<string> apps)
    {
        var caller = s.Find(callerId);
        if (caller is null) return SharedCodes.NotMember;
        if (!SharedRoles.CanManage(caller.Role)) return SharedCodes.NotManager;
        if (!SharedRoles.IsInvitable(role)) return SharedCodes.InvalidRole;
        if (guestType is not null && (role != SharedRoles.Guest || !SharedRoles.IsGuestType(guestType))) return SharedCodes.InvalidRole;
        if (apps.Count == 0) return SharedCodes.NoApps;
        foreach (var app in apps)
        {
            if (!SharedApps.IsValid(app)) return SharedCodes.InvalidApp;
            if (!s.HasApp(app)) return SharedCodes.AppOff;
            if (app == SharedApps.Flow && role == SharedRoles.Guest) return SharedCodes.GuestFlow;
        }
        return null;
    }

    public static SharedPlan SetRole(SharedState s, string callerId, string targetId, string? role, string? guestType)
    {
        var (caller, target, error) = Manage(s, callerId, targetId);
        if (error is not null) return SharedPlan.Fail(error, s);
        if (string.Equals(caller!.VantraId, target!.VantraId, StringComparison.OrdinalIgnoreCase)) return SharedPlan.Fail(SharedCodes.SelfChange, s);
        if (!SharedRoles.IsValid(role)) return SharedPlan.Fail(SharedCodes.InvalidRole, s);
        if (guestType is not null && !SharedRoles.IsGuestType(guestType)) return SharedPlan.Fail(SharedCodes.InvalidRole, s);
        if (caller.Role != SharedRoles.Owner && (role == SharedRoles.Owner || target.Role == SharedRoles.Owner))
            return SharedPlan.Fail(SharedCodes.OwnerOnly, s);
        if ((target.Role == SharedRoles.Guest) != (role == SharedRoles.Guest)) return SharedPlan.Fail(SharedCodes.GuestChange, s);
        if (s.IsPrimary(target.VantraId) && role != SharedRoles.Owner) return SharedPlan.Fail(SharedCodes.PrimaryOwner, s);
        if (target.Role == SharedRoles.Owner && role != SharedRoles.Owner && s.OwnerCount <= 1) return SharedPlan.Fail(SharedCodes.LastOwner, s);
        var type = role == SharedRoles.Guest ? guestType ?? target.GuestType : null;
        if (target.Role == role && target.GuestType == type) return SharedPlan.None(s, target.VantraId);
        return new SharedPlan(null, [], [new SharedChange(target.VantraId, role!, type)], [], [], [], s.PrimaryOwner, target.VantraId);
    }

    public static SharedPlan Remove(SharedState s, string callerId, string targetId)
    {
        var (caller, target, error) = Manage(s, callerId, targetId);
        if (error is not null) return SharedPlan.Fail(error, s);
        if (string.Equals(caller!.VantraId, target!.VantraId, StringComparison.OrdinalIgnoreCase)) return SharedPlan.Fail(SharedCodes.Self, s);
        if (target.Role == SharedRoles.Owner && caller.Role != SharedRoles.Owner) return SharedPlan.Fail(SharedCodes.OwnerOnly, s);
        if (s.IsPrimary(target.VantraId)) return SharedPlan.Fail(SharedCodes.PrimaryOwner, s);
        if (target.Role == SharedRoles.Owner && s.OwnerCount <= 1) return SharedPlan.Fail(SharedCodes.LastOwner, s);
        return new SharedPlan(null, [], [], [target.VantraId], [], [], s.PrimaryOwner, target.VantraId);
    }

    public static SharedPlan Leave(SharedState s, string callerId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return SharedPlan.Fail(SharedCodes.NotMember, s);
        if (s.IsPrimary(caller.VantraId)) return SharedPlan.Fail(SharedCodes.PrimaryOwner, s);
        if (caller.Role == SharedRoles.Owner && s.OwnerCount <= 1) return SharedPlan.Fail(SharedCodes.LastOwner, s);
        return new SharedPlan(null, [], [], [caller.VantraId], [], [], s.PrimaryOwner, caller.VantraId);
    }

    /// <summary>
    /// Hand the workspace over: the main owner names a teammate who already uses every app the workspace has (so
    /// nobody gains an app, or a seat, by it). They become an owner and the main owner; the caller becomes an admin.
    /// </summary>
    public static SharedPlan Transfer(SharedState s, string callerId, string? targetId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return SharedPlan.Fail(SharedCodes.NotMember, s);
        if (!s.IsPrimary(caller.VantraId)) return SharedPlan.Fail(SharedCodes.NotPrimary, s);
        if (string.Equals(targetId?.Trim(), caller.VantraId, StringComparison.OrdinalIgnoreCase)) return SharedPlan.Fail(SharedCodes.Self, s);
        var target = s.Find(targetId);
        if (target is null) return SharedPlan.Fail(SharedCodes.NotMember, s);
        if (target.Role == SharedRoles.Guest) return SharedPlan.Fail(SharedCodes.TargetGuest, s);
        if (s.Apps.Any(a => !target.Has(a))) return SharedPlan.Fail(SharedCodes.TargetApps, s);
        var changes = new List<SharedChange> { new(caller.VantraId, SharedRoles.Admin, null) };
        if (target.Role != SharedRoles.Owner) changes.Add(new SharedChange(target.VantraId, SharedRoles.Owner, null));
        return new SharedPlan(null, [], changes, [], [], [], target.VantraId, target.VantraId);
    }

    public static SharedPlan Grant(SharedState s, string callerId, string targetId, string? app)
    {
        var (caller, target, error) = Manage(s, callerId, targetId);
        if (error is not null) return SharedPlan.Fail(error, s);
        if (!SharedApps.IsValid(app)) return SharedPlan.Fail(SharedCodes.InvalidApp, s);
        if (!s.HasApp(app!)) return SharedPlan.Fail(SharedCodes.AppOff, s);
        if (target!.Role == SharedRoles.Owner && caller!.Role != SharedRoles.Owner) return SharedPlan.Fail(SharedCodes.OwnerOnly, s);
        if (app == SharedApps.Flow && target.Role == SharedRoles.Guest) return SharedPlan.Fail(SharedCodes.GuestFlow, s);
        if (target.Has(app!)) return SharedPlan.None(s, target.VantraId);
        return new SharedPlan(null, [], [], [], [new SharedAccess(target.VantraId, app!)], [], s.PrimaryOwner, target.VantraId);
    }

    public static SharedPlan Revoke(SharedState s, string callerId, string targetId, string? app)
    {
        var (caller, target, error) = Manage(s, callerId, targetId);
        if (error is not null) return SharedPlan.Fail(error, s);
        if (!SharedApps.IsValid(app)) return SharedPlan.Fail(SharedCodes.InvalidApp, s);
        if (string.Equals(caller!.VantraId, target!.VantraId, StringComparison.OrdinalIgnoreCase)) return SharedPlan.Fail(SharedCodes.SelfChange, s);
        if (target.Role == SharedRoles.Owner && caller.Role != SharedRoles.Owner) return SharedPlan.Fail(SharedCodes.OwnerOnly, s);
        if (s.IsPrimary(target.VantraId)) return SharedPlan.Fail(SharedCodes.PrimaryOwner, s);
        if (!target.Has(app!)) return SharedPlan.None(s, target.VantraId);
        return new SharedPlan(null, [], [], [], [], [new SharedAccess(target.VantraId, app!)], s.PrimaryOwner, target.VantraId);
    }

    /// <summary>
    /// Accepting an invitation: someone new joins with its role, and gets the apps it gives that the workspace still
    /// has (never Flow for a guest). Someone already in keeps their role and only gains the apps they were invited to.
    /// </summary>
    public static SharedPlan Join(SharedState s, string vantraId, string role, string? guestType, IReadOnlyList<string> apps)
    {
        var existing = s.Find(vantraId);
        var effectiveRole = existing?.Role ?? role;
        var grants = apps.Where(s.HasApp)
            .Where(a => !(a == SharedApps.Flow && effectiveRole == SharedRoles.Guest))
            .Where(a => existing is null || !existing.Has(a))
            .Select(a => new SharedAccess(existing?.VantraId ?? vantraId, a)).ToList();
        IReadOnlyList<SharedChange> adds = existing is null
            ? [new SharedChange(vantraId, role, role == SharedRoles.Guest ? guestType : null)]
            : [];
        return new SharedPlan(null, adds, [], [], grants, [], s.PrimaryOwner, existing?.VantraId ?? vantraId);
    }

    /// <summary>Taking access back that an app couldn't give (Projects found no seat): no checks, the app decided.</summary>
    public static SharedPlan Withdraw(SharedState s, IEnumerable<string> vantraIds, string app) =>
        new(null, [], [], [], [], vantraIds.Where(id => s.Find(id)?.Has(app) == true && !s.IsPrimary(id))
            .Select(id => new SharedAccess(s.Find(id)!.VantraId, app)).ToList(), s.PrimaryOwner);

    private static (SharedMember? Caller, SharedMember? Target, string? Error) Manage(SharedState s, string callerId, string targetId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return (null, null, SharedCodes.NotMember);
        if (!SharedRoles.CanManage(caller.Role)) return (caller, null, SharedCodes.NotManager);
        var target = s.Find(targetId);
        return target is null ? (caller, null, SharedCodes.NotMember) : (caller, target, null);
    }
}

/// <summary>What opening a shared invitation comes to (as Flow's invitations).</summary>
public enum SharedInviteOutcome { Accept, AlreadyMember, WrongAccount, Used, Revoked, Expired, Invalid }

/// <summary>
/// Shared invitations, modelled on Vantra Flow's: one row per (workspace, lower-cased email), open while
/// <c>pending</c> or <c>sent</c> for <see cref="ValidDays"/> days from when it was issued or last resent. The link's
/// token is 32 random bytes that are never stored or logged: the row keeps its SHA-256, so a copy of the database
/// can't be used to join, and a resend makes a new link. The token travels in request bodies, never in an API URL.
/// </summary>
public static class SharedInviteRules
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

    public static bool SameEmail(string? a, string? b) =>
        !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
        && string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static SharedInviteOutcome Evaluate(string status, DateTime expiresAt, DateTime nowUtc, string inviteEmail, string? accountEmail, bool isMember)
    {
        if (!SameEmail(inviteEmail, accountEmail)) return SharedInviteOutcome.WrongAccount;
        if (isMember) return SharedInviteOutcome.AlreadyMember;
        return Preview(status, expiresAt, nowUtc);
    }

    public static SharedInviteOutcome Preview(string status, DateTime expiresAt, DateTime nowUtc) => status switch
    {
        Accepted => SharedInviteOutcome.Used,
        Revoked => SharedInviteOutcome.Revoked,
        Pending or Sent => expiresAt <= nowUtc ? SharedInviteOutcome.Expired : SharedInviteOutcome.Accept,
        _ => SharedInviteOutcome.Invalid,
    };

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

    public static string EmailHint(string email)
    {
        var at = email.LastIndexOf('@');
        return at <= 0 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    public const int TokenBytes = 32;

    /// <summary>A new link token: 32 random bytes, base64url (43 characters).</summary>
    public static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(TokenBytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Whether it has a token's shape (43 base64url characters); anything else is never looked up.</summary>
    public static bool IsTokenShape(string? token) =>
        token is { Length: 43 } && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    /// <summary>What's stored for a token: its SHA-256 (32 bytes).</summary>
    public static byte[] Hash(string token) => SHA256.HashData(Encoding.ASCII.GetBytes(token));

    /// <summary>
    /// <paramref name="origin"/> when it is one of the app's own origins (<c>Cors:AllowedOrigins</c> or
    /// <c>App:BaseUrl</c>), else null: anyone can send any Origin header, and an emailed link must never point elsewhere.
    /// </summary>
    public static string? TrustedOrigin(string? origin, IEnumerable<string>? allowedOrigins, string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(origin)) return null;
        static string Norm(string s) => s.Trim().TrimEnd('/');
        var o = Norm(origin);
        return (allowedOrigins ?? []).Append(baseUrl ?? "")
            .Any(a => !string.IsNullOrWhiteSpace(a) && string.Equals(Norm(a), o, StringComparison.OrdinalIgnoreCase)) ? o : null;
    }

    /// <summary>The join link in the inviting app: <c>{app}/invite/{token}</c>.</summary>
    public static string Link(string? trustedOrigin, string? baseUrl, string fallback, string token) =>
        $"{(!string.IsNullOrWhiteSpace(trustedOrigin) ? trustedOrigin : !string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : fallback).TrimEnd('/')}/invite/{token}";
}
