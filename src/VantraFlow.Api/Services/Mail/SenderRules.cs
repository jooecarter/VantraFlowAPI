using System.Net;
using System.Net.Mail;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Mail;

/// <summary>Who an email is sent from: Vantra Flow's own mailbox, the workspace's Microsoft 365 or Google account, or an SMTP mailbox.</summary>
public sealed record SenderRef(string Kind, string? MailboxId = null)
{
    /// <summary>What a step or the workspace keeps (<c>vantraflow</c>, <c>microsoft</c>, <c>google</c>, <c>smtp:&lt;id&gt;</c>).</summary>
    public string Value => Kind == SenderKinds.Smtp ? $"smtp:{MailboxId}" : Kind;
    /// <summary>Sending from it is the person's own mailbox (premium, per the plan's premium apps).</summary>
    public bool Own => Kind != SenderKinds.VantraFlow && Kind != SenderKinds.Default;
}

public static class SenderKinds
{
    /// <summary>The step follows the workspace's default (a step with no <c>from</c> at all is the same).</summary>
    public const string Default = "default";
    public const string VantraFlow = "vantraflow";
    public const string Microsoft = "microsoft";
    public const string Google = "google";
    public const string Smtp = "smtp";
}

/// <summary>
/// The Send email step's <c>from</c>, without a database: what it says, what it resolves to (the step's own choice, else the
/// workspace's default, else Vantra Flow), and which premium app it counts as.
/// </summary>
public static partial class SenderRules
{
    public static readonly SenderRef Default = new(SenderKinds.Default);
    public static readonly SenderRef VantraFlow = new(SenderKinds.VantraFlow);

    [GeneratedRegex("^mbx_[0-9a-f]{32}$")]
    private static partial Regex MailboxId();

    public static bool IsMailboxId(string? id) => id is not null && MailboxId().IsMatch(id);

    /// <summary>A kept value; null when it isn't one (a typo, or a token: the sender is chosen, not computed).</summary>
    public static SenderRef? Parse(string? raw)
    {
        var v = (raw ?? "").Trim();
        if (v.Length == 0 || v == SenderKinds.Default) return Default;
        if (v is SenderKinds.VantraFlow or SenderKinds.Microsoft or SenderKinds.Google) return new SenderRef(v);
        if (v.StartsWith("smtp:", StringComparison.Ordinal) && IsMailboxId(v[5..])) return new SenderRef(SenderKinds.Smtp, v[5..]);
        return null;
    }

    public static SenderRef? Parse(JsonNode? node) => node is null ? Default : node is JsonValue ? Parse(Tokens.Text(node)) : null;

    /// <summary>Why a step's <c>from</c> can't be saved; null when it can.</summary>
    public static string? Check(JsonNode? node) => Parse(node) is null ? "Choose who the email is sent from." : null;

    /// <summary>
    /// Who sends: the step's own choice; for "Workspace default" (or none), the workspace's default; with no default (or one
    /// that isn't a sender), Vantra Flow, as every flow did before.
    /// </summary>
    public static SenderRef Resolve(SenderRef step, string? workspaceDefault)
    {
        if (step.Kind != SenderKinds.Default) return step;
        var d = Parse(workspaceDefault);
        return d is null || d.Kind == SenderKinds.Default ? VantraFlow : d;
    }

    /// <summary>The premium app sending from it counts as (<c>microsoft</c>, <c>google</c>, <c>smtp</c>); null for Vantra Flow and the default.</summary>
    public static string? PremiumApp(SenderRef? s) => s is { Own: true } ? s.Kind : null;

    /// <summary>The premium app a step's own <c>from</c> counts as (a step on "Workspace default" is checked when it sends).</summary>
    public static string? PremiumApp(JsonNode? from) => PremiumApp(Parse(from));
}

/// <summary>
/// An SMTP mailbox's settings, checked before anything connects: the host (a name or a public address), the port (25, 465, 587
/// or 2525), STARTTLS or SSL/TLS, the sign-in and the From address. Then <see cref="CheckAddresses"/> on what the host resolves
/// to, as <see cref="NetworkGuard"/> does for the HTTP request step: public addresses only.
/// </summary>
public static partial class SmtpRules
{
    public static readonly IReadOnlySet<int> Ports = new HashSet<int> { 25, 465, 587, 2525 };
    public const string StartTls = "starttls";
    public const string Ssl = "ssl";
    public const int MaxPassword = 1000;
    public const int MaxMailboxes = 20;

    /// <summary>Settings that passed <see cref="Check"/>: trimmed, the host lower-cased.</summary>
    public sealed record Settings(string Host, int Port, string Security, string Username, string Password, string FromEmail, string? FromName);

    [GeneratedRegex(@"^(?=.{1,253}$)([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)*$")]
    private static partial Regex HostName();

    private static bool OneLine(string s) => !s.Any(c => c is '\r' or '\n' or '\0');

    /// <summary>The settings ready to try, or why not. <paramref name="password"/> null: keep the one already saved (an edit).</summary>
    public static (Settings? Ok, string? Error, string? Field) Check(string? host, int? port, string? security, string? username, string? password, string? fromEmail, string? fromName,
        bool passwordOptional = false)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return (null, "Fill in the server, like smtp.example.com.", "host");
        if (h.Contains("://") || h.Contains('/') || h.Contains(':') && !IPAddress.TryParse(h.Trim('[', ']'), out _))
            return (null, "Just the server's name, like smtp.example.com, without https:// or a port.", "host");
        if (IPAddress.TryParse(h.Trim('[', ']'), out var literal))
        {
            if (!NetworkGuard.IsPublic(literal)) return (null, "That address is on a private or reserved network, so Vantra Flow won't connect to it.", "host");
        }
        else if (!HostName().IsMatch(h) || !h.Contains('.') || h is "localhost" || h.EndsWith(".localhost") || h.EndsWith(".local") || h.EndsWith(".internal"))
            return (null, "That isn't a mail server's name. It looks like smtp.example.com.", "host");

        if (port is not { } p || !Ports.Contains(p)) return (null, "Use port 587 (STARTTLS) or 465 (SSL/TLS). 25 and 2525 work too.", "port");
        var sec = (security ?? "").Trim().ToLowerInvariant() switch { "" => p == 465 ? Ssl : StartTls, var x => x };
        if (sec is not (StartTls or Ssl)) return (null, "Choose STARTTLS or SSL/TLS.", "security");

        var user = (username ?? "").Trim();
        if (user.Length is 0 or > 256 || !OneLine(user)) return (null, "Fill in the username, usually the email address.", "username");
        var pass = password ?? "";
        if (pass.Length == 0 && !passwordOptional) return (null, "Fill in the password. For Gmail and Microsoft 365, an app password.", "password");
        if (pass.Length > MaxPassword || !OneLine(pass)) return (null, "That password can't be used here.", "password");

        var from = (fromEmail ?? "").Trim();
        if (from.Length is 0 or > 256 || !MailAddress.TryCreate(from, out var addr) || addr.Address != from || from.Contains(' ') || !OneLine(from))
            return (null, "The From address isn't an email address.", "fromEmail");
        var name = (fromName ?? "").Trim();
        if (name.Length > 100 || !OneLine(name)) return (null, "The From name is one line of up to 100 characters.", "fromName");
        return (new Settings(h, p, sec, user, pass, from, name.Length == 0 ? null : name), null, null);
    }

    /// <summary>
    /// Whether the server may be dialled at these addresses (what its name resolved to): every one public, as the HTTP request
    /// step's guard requires (no loopback, private, link-local or metadata ranges). Null when it may.
    /// </summary>
    public static string? CheckAddresses(string host, IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0) return $"Couldn't find the server {host}. Check its name.";
        if (addresses.Any(a => !NetworkGuard.IsPublic(a))) return $"{host} is on a private or reserved network, so Vantra Flow won't connect to it.";
        return null;
    }

    /// <summary>"smtp.example.com:587 · STARTTLS".</summary>
    public static string Describe(string host, int port, string security) => $"{host}:{port} · {(security == Ssl ? "SSL/TLS" : "STARTTLS")}";
}
