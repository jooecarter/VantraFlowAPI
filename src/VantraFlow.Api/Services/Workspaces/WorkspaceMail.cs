using System.Net;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Workspaces;

/// <summary>The workspace emails: an invitation, and the note to someone added straight away (<see cref="WorkspaceMail"/>; a fake in tests).</summary>
public interface IWorkspaceMail
{
    /// <summary>Whether a sender is set up at all. When it isn't, an invitation stays <c>pending</c> and its link is shown to copy.</summary>
    bool CanSend { get; }
    /// <summary>The join link for a token, on the studio's origin.</summary>
    string InviteLink(string? origin, string token);
    /// <summary>The studio's address for a workspace.</summary>
    string WorkspaceLink(string? origin, Guid workspaceId);
    Task<bool> SendInviteAsync(string toEmail, string workspaceName, string inviterName, string? inviterEmail, string role, string link);
    Task<bool> SendAddedAsync(string toEmail, string? toName, string workspaceName, string inviterName, string? inviterEmail, string role, string link);
}

/// <summary>
/// Composes and sends the workspace emails through Flow's <see cref="EmailService"/> (Microsoft Graph, else SMTP), as
/// the inviter (their name on the shared mailbox, Reply-To them), as Vantra Orbit's InviteEmailSender. They aren't a
/// flow's emails and don't count against the workspace's daily email cap; the invitation endpoints have their own
/// limits. Best effort: false when it didn't send, never throws. Links are on <c>App:BaseUrl</c>, or the inviting
/// browser's origin when that is one of the studio's own (<c>Cors:AllowedOrigins</c>). The link is never logged.
/// </summary>
public sealed class WorkspaceMail(EmailService email, IConfiguration config, ILogger<WorkspaceMail> log) : IWorkspaceMail
{
    public bool CanSend => email.CanSend();

    private string? Trusted(string? origin) =>
        InviteRules.TrustedOrigin(origin, config.GetSection("Cors:AllowedOrigins").Get<string[]>(), config["App:BaseUrl"]);

    public string InviteLink(string? origin, string token) => InviteRules.Link(Trusted(origin), config["App:BaseUrl"], token);

    public string WorkspaceLink(string? origin, Guid workspaceId) => InviteRules.WorkspaceLink(Trusted(origin), config["App:BaseUrl"], workspaceId);

    public Task<bool> SendInviteAsync(string toEmail, string workspaceName, string inviterName, string? inviterEmail, string role, string link)
    {
        var (subject, html) = ComposeInvite(workspaceName, inviterName, role, link);
        return SendAsync(toEmail, toEmail, inviterName, inviterEmail, subject, html);
    }

    public Task<bool> SendAddedAsync(string toEmail, string? toName, string workspaceName, string inviterName, string? inviterEmail, string role, string link)
    {
        var (subject, html) = ComposeAdded(workspaceName, inviterName, role, link);
        return SendAsync(toEmail, toName ?? toEmail, inviterName, inviterEmail, subject, html);
    }

    private async Task<bool> SendAsync(string to, string toName, string fromName, string? fromEmail, string subject, string html)
    {
        if (!email.CanSend()) return false;
        try { return await email.SendAsync(fromEmail ?? "", string.IsNullOrWhiteSpace(fromName) ? "Vantra Flow" : fromName, to, toName, subject, html); }
        catch (Exception e)
        {
            log.LogWarning(e, "A workspace email didn't send");
            return false;
        }
    }

    private static string RoleWords(string role) => role == FlowRoles.Admin ? "an admin" : role == FlowRoles.Owner ? "an owner" : "a member";

    /// <summary>The invitation: subject (one line) and HTML. Every name and the link are HTML-encoded.</summary>
    public static (string Subject, string Html) ComposeInvite(string workspaceName, string inviterName, string role, string link)
    {
        var hasName = !string.IsNullOrWhiteSpace(inviterName);
        var who = hasName ? inviterName.Trim() : "A teammate";
        var subject = OneLine(hasName ? $"{who} invited you to {workspaceName} on Vantra Flow" : $"You're invited to {workspaceName} on Vantra Flow");
        var body = $"{E(who)} invited you to <strong>{E(workspaceName)}</strong> on Vantra Flow as {RoleWords(role)}, to build the flows that connect "
                   + "the team's apps together.";
        var foot = $"The link works for {InviteRules.ValidDays} days. If you weren't expecting this, you can ignore it.";
        return (subject, Frame($"Join {E(workspaceName)}", body, "Accept invite", link, foot, showLink: true));
    }

    /// <summary>The note to an existing Vantra account added straight away.</summary>
    public static (string Subject, string Html) ComposeAdded(string workspaceName, string inviterName, string role, string link)
    {
        var who = string.IsNullOrWhiteSpace(inviterName) ? "A teammate" : inviterName.Trim();
        var subject = OneLine($"{who} added you to {workspaceName} on Vantra Flow");
        var body = $"{E(who)} added you to <strong>{E(workspaceName)}</strong> on Vantra Flow as {RoleWords(role)}. Sign in with your Vantra account "
                   + "to find it in the workspace switcher.";
        return (subject, Frame($"You're in {E(workspaceName)}", body, "Open Vantra Flow", link, "You can leave a workspace from its settings.", showLink: false));
    }

    private static string Frame(string heading, string body, string button, string link, string foot, bool showLink)
    {
        var url = E(link);
        var plain = showLink
            ? $@"<p style=""margin:16px 0 0;font-size:12.5px;line-height:1.5;color:#8b95a3;"">Or paste this into your browser:<br><a href=""{url}"" style=""color:#12855d;word-break:break-all;"">{url}</a></p>"
            : "";
        return $@"<!DOCTYPE html>
<html>
<body style=""margin:0;background:#f4f6f5;font-family:-apple-system,Segoe UI,Inter,Roboto,Helvetica,Arial,sans-serif;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f6f5;padding:32px 16px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""480"" cellpadding=""0"" cellspacing=""0"" style=""max-width:480px;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e3e8e5;"">
        <tr><td style=""background:#0f3d2e;padding:20px 28px;"">
          <span style=""color:#ffffff;font-size:18px;font-weight:700;"">Vantra <span style=""font-weight:400;"">Flow</span></span>
        </td></tr>
        <tr><td style=""padding:30px 28px 8px;"">
          <h1 style=""margin:0 0 12px;font-size:20px;color:#18222f;"">{heading}</h1>
          <p style=""margin:0 0 18px;font-size:15px;line-height:1.6;color:#43505c;"">{body}</p>
        </td></tr>
        <tr><td style=""padding:0 28px 28px;"">
          <a href=""{url}"" style=""display:inline-block;background:#12855d;color:#ffffff;text-decoration:none;font-size:15px;font-weight:600;padding:12px 22px;border-radius:8px;"">{button}</a>
          {plain}
        </td></tr>
        <tr><td style=""padding:16px 28px;border-top:1px solid #eef1ef;font-size:12px;color:#9aa3ad;"">{E(foot)}</td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);

    // A subject is one header line: no CR/LF or other control characters.
    private static string OneLine(string s) => new(s.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
}
