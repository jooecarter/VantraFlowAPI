using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Billing;

namespace VantraFlow.Api.Services.Mail;

/// <summary>
/// The ways an email leaves: Vantra Flow's own mailbox (<see cref="EmailService"/>: Microsoft Graph, else SMTP), the workspace's
/// Microsoft 365 or Google account through the same code as the Outlook and Gmail send actions
/// (<see cref="OAuth.OAuthActions.SendMailAsync"/>), or an SMTP mailbox (<see cref="ISmtpMailer"/>).
/// </summary>
public sealed class MailRoutes(EmailService email, OAuth.OAuthActions oauth, ISmtpMailer smtp) : IMailRoutes
{
    public bool VantraCanSend => email.CanSend();

    public string? VantraAddress => email.Graph.IsUsable ? email.Graph.Sender : email.Global.IsUsable ? email.Global.From : null;

    public Task<bool> SendVantraAsync(string replyTo, string fromName, string to, string subject, string html) =>
        email.SendAsync(replyTo, fromName, to, "", subject, html);

    public Task SendAccountAsync(Guid workspaceId, string provider, string to, string subject, string html, string? replyTo, CancellationToken ct) =>
        oauth.SendMailAsync(workspaceId, provider, [to], [], subject, html, replyTo, ct);

    public Task<SmtpResult> SendSmtpAsync(SmtpRules.Settings settings, OutgoingMail mail, CancellationToken ct) => smtp.SendAsync(settings, mail, ct);
}

/// <summary>The workspace's Microsoft 365 and Google connections (db/004), read as senders.</summary>
public sealed class OAuthSenderAccounts(OAuthRepository repo, FlowSchema schema) : ISenderAccounts
{
    public async Task<bool> ReadyAsync()
    {
        try { return await schema.ConnectorsReadyAsync(); }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    public Task<OAuthConnectionRow?> LiveAsync(Guid workspaceId, string provider) => repo.LiveAsync(workspaceId, provider);

    public Task<OAuthConnectionRow?> LatestAsync(Guid workspaceId, string provider) => repo.LatestAsync(workspaceId, provider);
}

/// <summary>
/// Sending from an own mailbox follows the plan's premium apps (<c>Billing:PremiumApps</c>): Outlook as Microsoft 365, Gmail as
/// Google, and SMTP mailboxes as <c>smtp</c>. So on Free and Starter they can be connected, and a flow that sends from one can't
/// be switched on; a step on "Workspace default" is checked as it sends.
/// </summary>
public sealed class PlanSenderRules(Plans plans, IWorkspaceStore workspaces) : ISenderPlan
{
    public bool IsPremium(string premiumApp) => plans.Catalog.PremiumApps.Contains(premiumApp);

    public string PremiumTier => plans.Catalog.Cheapest(l => l.Premium);

    public async Task<bool> AllowsPremiumAsync(Guid workspaceId)
    {
        if (await workspaces.GetAsync(workspaceId) is not { } ws) return false;
        var (plan, _) = await plans.ForAsync(ws, DateTime.UtcNow);
        return !plan.Enforced || plan.Limits.Premium;
    }

    public async Task<string?> RefusalAsync(Guid workspaceId, string premiumApp)
    {
        if (!IsPremium(premiumApp) || await AllowsPremiumAsync(workspaceId)) return null;
        return $"Sending from your own mailbox is on {Tiers.Name(PremiumTier)} and up. Upgrade, or send from Vantra Flow.";
    }
}
