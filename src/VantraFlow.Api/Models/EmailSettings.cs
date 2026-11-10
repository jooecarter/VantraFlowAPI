namespace VantraFlow.Api.Models;

/// <summary>
/// SMTP connection settings used to actually send — the server-wide ones from
/// the <c>Smtp</c> config section (copied from Vantra Folio, which also lets a
/// user save their own; Orbit and Flow have only the server-wide sender).
/// </summary>
public sealed record SmtpSettings(string Host, int Port, string? User, string? Password, bool UseSsl, string? From)
{
    /// <summary>Usable once a host is present.</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Host);
}

/// <summary>
/// App-only Microsoft Graph settings for sending as an Office 365 mailbox —
/// typically a shared mailbox. Used as the server-wide default sender when
/// present, in place of SMTP: Office 365 has disabled basic SMTP AUTH, so
/// Graph's app-only <c>Mail.Send</c> is the supported way to send from a
/// shared mailbox. <see cref="Sender"/> is the mailbox's address (UPN) and
/// must be covered by the app registration's Mail.Send grant (optionally
/// scoped via an application access policy).
/// </summary>
public sealed record GraphMailSettings(string TenantId, string ClientId, string ClientSecret, string Sender)
{
    /// <summary>Usable once the app credentials and a sender mailbox are all present.</summary>
    public bool IsUsable =>
        !string.IsNullOrWhiteSpace(TenantId)
        && !string.IsNullOrWhiteSpace(ClientId)
        && !string.IsNullOrWhiteSpace(ClientSecret)
        && !string.IsNullOrWhiteSpace(Sender);
}
