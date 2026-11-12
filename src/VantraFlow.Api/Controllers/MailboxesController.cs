using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Mail;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Send from your own mailbox: the "Send from" choices, the workspace's default sender, and SMTP mailboxes (db/013; 503
/// <c>not_ready</c> before, where it needs it).
/// <code>
/// GET    api/w/{ws}/senders                       → the choices and the default (members)
/// PUT    api/w/{ws}/senders/default               { from } → the default for steps on "Workspace default" (owners and admins)
/// GET    api/w/{ws}/mailboxes                     → SMTP mailboxes: everyone sees the address, owners and admins the server too
/// POST   api/w/{ws}/mailboxes                     { host, port, security, username, password, fromEmail, fromName } → 201,
///                                                 once it connects and signs in (nothing is sent) (owners and admins)
/// PUT    api/w/{ws}/mailboxes/{id}                the same; no password keeps the saved one; checked again first
/// DELETE api/w/{ws}/mailboxes/{id}                → 204: the password wiped; steps sending from it fail until they're changed
/// POST   api/w/{ws}/mailboxes/{id}/test-email     → a test email to the person asking (owners and admins)
/// </code>
/// The password is sealed (AES-256-GCM, bound to the workspace and the mailbox), never logged and never sent back.
/// </summary>
public sealed class MailboxesController(IWorkspaceStore workspaces, IMailboxStore store, Senders senders, ISmtpMailer smtp, ISenderPlan plan,
    FlowSecrets secrets, ILogger<MailboxesController> log) : FlowControllerBase(workspaces)
{
    public const string RateLimitPolicy = "mailbox-checks";

    private ObjectResult NotReady() =>
        StatusCode(503, new { error = "SMTP mailboxes arrive with the next server update.", code = "not_ready" });

    [HttpGet("api/w/{workspaceId:guid}/senders")]
    public async Task<IActionResult> ListSenders(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return Ok(await senders.ListAsync(workspaceId, FlowRoles.CanManage(role)));
    }

    [HttpPut("api/w/{workspaceId:guid}/senders/default")]
    public async Task<IActionResult> SetDefault(Guid workspaceId, [FromBody] DefaultSenderBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        if (SenderRules.Parse(body.From) is not { } who || who.Kind == SenderKinds.Default)
            return BadRequest(new { error = "Choose who emails are sent from.", code = "bad_sender" });
        if (who.Own)
        {
            var open = await senders.OpenAsync(workspaceId, who);
            if (!open.Ok)
            {
                if (await plan.RefusalAsync(workspaceId, who.Kind) is { } no)
                    return StatusCode(402, new { error = no, code = "plan_premium", upgradeTo = plan.PremiumTier });
                return BadRequest(new { error = open.Error, code = "bad_sender" });
            }
        }
        await store.SetDefaultAsync(workspaceId, who.Kind == SenderKinds.VantraFlow ? null : who.Value);
        var list = await senders.ListAsync(workspaceId, true);
        var label = list.Options.FirstOrDefault(o => o.Value == who.Value) is { } o ? o.Email ?? o.Label : who.Value;
        await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: $"Emails now send from {label} by default");
        return Ok(list);
    }

    [HttpGet("api/w/{workspaceId:guid}/mailboxes")]
    public async Task<IActionResult> List(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        var full = FlowRoles.CanManage(role);
        return Ok((await store.ListAsync(workspaceId)).Select(m => Dto(m, full)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/mailboxes")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] MailboxBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        var (settings, why, field) = SmtpRules.Check(body.Host, body.Port, body.Security, body.Username, body.Password, body.FromEmail, body.FromName);
        if (settings is null) return BadRequest(new { error = why, code = "bad_mailbox", field });
        if ((await store.ListAsync(workspaceId)).Count >= SmtpRules.MaxMailboxes)
            return BadRequest(new { error = $"A workspace can connect {SmtpRules.MaxMailboxes} mailboxes.", code = "too_many" });
        var test = await smtp.TestAsync(settings, ct);
        if (!test.Ok) return BadRequest(new { error = test.Error, code = "smtp_failed", signIn = test.SignIn });

        var id = FlowSecrets.NewRowId("mbx_");
        var now = DateTime.UtcNow;
        var row = new MailboxRow(id, workspaceId, settings.Host, settings.Port, settings.Security, settings.Username,
            secrets.Protect(settings.Password, workspaceId, Services.Mail.Senders.SealId(id), 2048), settings.FromEmail, settings.FromName, "active", null, Sub, DisplayName,
            now, now, null, null);
        if (!await store.InsertAsync(row, SmtpRules.MaxMailboxes))
            return BadRequest(new { error = $"A workspace can connect {SmtpRules.MaxMailboxes} mailboxes.", code = "too_many" });
        log.LogInformation("Workspace {Workspace} connected SMTP mailbox {Mailbox}", workspaceId, id);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.ConnectionAdded, detail: $"Email (SMTP): {settings.FromEmail}");
        return StatusCode(201, Dto(row, true));
    }

    [HttpPut("api/w/{workspaceId:guid}/mailboxes/{id}")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Update(Guid workspaceId, string id, [FromBody] MailboxBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        if (!SenderRules.IsMailboxId(id) || await store.GetAsync(workspaceId, id) is not { RemovedAt: null } row)
            return NotFound(new { error = "That mailbox isn't connected any more." });
        var keep = string.IsNullOrEmpty(body.Password);
        var (settings, why, field) = SmtpRules.Check(body.Host, body.Port, body.Security, body.Username, body.Password, body.FromEmail, body.FromName, passwordOptional: keep);
        if (settings is null) return BadRequest(new { error = why, code = "bad_mailbox", field });
        if (keep)
        {
            // The saved password, unless the server changed: a new server needs it typed again.
            if (settings.Host != row.Host || secrets.Unprotect(row.PasswordCipher, workspaceId, Services.Mail.Senders.SealId(id)) is not { } saved)
                return BadRequest(new { error = "Fill in the password again.", code = "bad_mailbox", field = "password" });
            settings = settings with { Password = saved };
        }
        var test = await smtp.TestAsync(settings, ct);
        if (!test.Ok) return BadRequest(new { error = test.Error, code = "smtp_failed", signIn = test.SignIn });
        var cipher = keep ? null : secrets.Protect(settings.Password, workspaceId, Services.Mail.Senders.SealId(id), 2048);
        var now = DateTime.UtcNow;
        await store.UpdateAsync(workspaceId, id, settings, cipher, now);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: $"Updated the mailbox {settings.FromEmail}");
        return Ok(Dto(row with
        {
            Host = settings.Host, Port = settings.Port, Security = settings.Security, Username = settings.Username, FromEmail = settings.FromEmail, FromName = settings.FromName,
            Status = "active", LastError = null, UpdatedAt = now,
        }, true));
    }

    [HttpDelete("api/w/{workspaceId:guid}/mailboxes/{id}")]
    public async Task<IActionResult> Delete(Guid workspaceId, string id)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        if (!SenderRules.IsMailboxId(id) || await store.GetAsync(workspaceId, id) is not { RemovedAt: null } row) return NoContent();
        await store.RemoveAsync(workspaceId, id, DateTime.UtcNow);
        log.LogInformation("Workspace {Workspace} removed SMTP mailbox {Mailbox}", workspaceId, id);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.ConnectionRemoved, detail: $"Email (SMTP): {row.FromEmail}");
        return NoContent();
    }

    /// <summary>A test email from the mailbox to the person asking, so they see it arrive.</summary>
    [HttpPost("api/w/{workspaceId:guid}/mailboxes/{id}/test-email")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> TestEmail(Guid workspaceId, string id, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        if (!SenderRules.IsMailboxId(id) || await store.GetAsync(workspaceId, id) is not { RemovedAt: null } row)
            return NotFound(new { error = "That mailbox isn't connected any more." });
        if (Email is not { Length: > 0 } me || StepActions.Recipients(me).Error is not null)
            return BadRequest(new { error = "Your account has no email address to send the test to.", code = "no_email" });
        if (secrets.Unprotect(row.PasswordCipher, workspaceId, Services.Mail.Senders.SealId(id)) is not { } password)
            return BadRequest(new { error = $"The mailbox {row.FromEmail} can't be opened. Reconnect it.", code = "smtp_failed" });
        var who = new SenderRef(SenderKinds.Smtp, id);
        if (!Services.Mail.Senders.TakeTurn(workspaceId, who))
            return StatusCode(429, new { error = "That mailbox has sent enough for a minute. Try again shortly.", code = "rate_limited" });
        var settings = new SmtpRules.Settings(row.Host, row.Port, row.Security, row.Username, password, row.FromEmail, row.FromName);
        var html = Services.OAuth.OAuthActions.Html(
            $"This is a test from Vantra Flow.\n\nYour flows can now send email from {row.FromEmail}. Choose it in a Send email step, or make it the workspace's default in Settings.");
        var sent = await smtp.SendAsync(settings, new OutgoingMail(me, "Test email from Vantra Flow", html, null, null), ct);
        await store.TouchAsync(workspaceId, id, sent.Ok ? null : sent.SignIn ? sent.Error : null);
        if (!sent.Ok) return BadRequest(new { error = sent.Error, code = "smtp_failed", signIn = sent.SignIn });
        return Ok(new { sentTo = me });
    }

    /// <summary>Everyone sees the address; owners and admins the server, the username and who connected it. Never the password.</summary>
    public static MailboxDto Dto(MailboxRow m, bool full) => full
        ? new MailboxDto(m.Id, m.FromEmail, m.Status, m.FromName, m.Host, m.Port, m.Security, m.Username, m.LastError, m.CreatedByName, Iso(m.CreatedAt), Iso(m.LastUsedAt))
        : new MailboxDto(m.Id, m.FromEmail, m.Status);
}
