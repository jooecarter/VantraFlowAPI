using System.Text.Json.Nodes;
using System.Threading.RateLimiting;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Services.Mail;

/// <summary>A workspace's SMTP mailboxes and its default sender (db/013; <see cref="Data.MailboxesRepository"/>, a fake in tests).</summary>
public interface IMailboxStore
{
    /// <summary>Has db/013 run? Until it has there are no SMTP mailboxes and no default sender (every step sends as before).</summary>
    Task<bool> ReadyAsync();
    /// <summary>The workspace's mailboxes, not the removed ones, oldest first.</summary>
    Task<IReadOnlyList<MailboxRow>> ListAsync(Guid workspaceId);
    /// <summary>One mailbox, removed or not (a step that names a removed one says which it was).</summary>
    Task<MailboxRow?> GetAsync(Guid workspaceId, string id);
    /// <summary>False when the workspace already has <paramref name="max"/> mailboxes.</summary>
    Task<bool> InsertAsync(MailboxRow row, int max);
    Task<bool> UpdateAsync(Guid workspaceId, string id, SmtpRules.Settings settings, byte[]? passwordCipher, DateTime now);
    /// <summary>Removed: the password wiped, the row kept to name it.</summary>
    Task RemoveAsync(Guid workspaceId, string id, DateTime now);
    /// <summary>Used just now: working (<paramref name="error"/> null) or not, with why.</summary>
    Task TouchAsync(Guid workspaceId, string id, string? error);
    Task<string?> DefaultAsync(Guid workspaceId);
    Task SetDefaultAsync(Guid workspaceId, string? value);
}

/// <summary>The workspace's Microsoft 365 and Google connections, as senders (<see cref="OAuthSenderAccounts"/>; a fake in tests).</summary>
public interface ISenderAccounts
{
    /// <summary>Has db/004 run (outside apps)?</summary>
    Task<bool> ReadyAsync();
    Task<OAuthConnectionRow?> LiveAsync(Guid workspaceId, string provider);
    /// <summary>The newest connection, live or not, to name a mailbox that was disconnected.</summary>
    Task<OAuthConnectionRow?> LatestAsync(Guid workspaceId, string provider);
}

/// <summary>The ways out (<see cref="MailRoutes"/>; a fake in tests).</summary>
public interface IMailRoutes
{
    bool VantraCanSend { get; }
    /// <summary>The server's own mailbox address (Graph's sender, else SMTP's From), if it has one.</summary>
    string? VantraAddress { get; }
    Task<bool> SendVantraAsync(string replyTo, string fromName, string to, string subject, string html);
    /// <summary>As the workspace's Microsoft 365 or Google account. Throws the app exceptions the engine knows.</summary>
    Task SendAccountAsync(Guid workspaceId, string provider, string to, string subject, string html, string? replyTo, CancellationToken ct);
    Task<SmtpResult> SendSmtpAsync(SmtpRules.Settings settings, OutgoingMail mail, CancellationToken ct);
}

/// <summary>Whether the workspace's plan lets it send from an own mailbox (<see cref="PlanSenderRules"/>; a fake in tests).</summary>
public interface ISenderPlan
{
    /// <summary>Is sending from it premium here (the plan's premium apps)?</summary>
    bool IsPremium(string premiumApp);
    /// <summary>The cheapest tier with premium apps (the upgrade prompt's).</summary>
    string PremiumTier { get; }
    /// <summary>Null when the plan includes it; else why not, in plain words.</summary>
    Task<string?> RefusalAsync(Guid workspaceId, string premiumApp);
    Task<bool> AllowsPremiumAsync(Guid workspaceId);
}

/// <summary>A sender opened for sending: who it is (for the run log), its settings, or why it can't send.</summary>
public sealed record OpenSender(SenderRef Ref, string Email, string Name, SmtpRules.Settings? Smtp = null, string? Error = null, bool Retryable = false)
{
    public bool Ok => Error is null;
    /// <summary>What the run log keeps: who sent it.</summary>
    public JsonObject Output() => new() { ["kind"] = Ref.Kind, ["email"] = Email, ["name"] = Name };
}

/// <summary>One email sent, or why not.</summary>
public sealed record SendResult(bool Ok, string? Error = null, bool Retryable = false)
{
    public static readonly SendResult Done = new(true);
}

/// <summary>
/// Who a Send email step sends from, and sending as them:
/// <list type="bullet">
/// <item><b>Resolve</b>: the step's own choice; "Workspace default" (or none, as every older flow) takes the workspace's default
/// (db/013), and with none, Vantra Flow's own mailbox, as before.</item>
/// <item><b>Open</b>: a Microsoft 365 or Google account must be connected (the same connection the Outlook and Gmail actions
/// use), an SMTP mailbox must still be there with a password that opens, and the plan must include it. Otherwise a clear
/// reason: "The mailbox x@example.com is disconnected. Reconnect it in Connections."</item>
/// <item><b>Send</b>: through the same code as the Outlook and Gmail send actions, or SMTP; at most
/// <see cref="PerMailboxPerMinute"/> a minute from each mailbox. A mailbox that refuses its sign-in is marked as needing a look.</item>
/// </list>
/// </summary>
public sealed class Senders(IMailboxStore store, ISenderAccounts accounts, IMailRoutes routes, ISenderPlan plan, FlowSecrets secrets)
{
    public const int PerMailboxPerMinute = 30;

    public static string Disconnected(string email) => $"The mailbox {email} is disconnected. Reconnect it in Connections.";

    public static string ProviderName(string kind) => kind == SenderKinds.Microsoft ? "Microsoft 365" : kind == SenderKinds.Google ? "Google" : "Email (SMTP)";

    /// <summary>The kind as people know it: "Outlook", "Gmail", "SMTP".</summary>
    public static string KindLabel(string kind) => kind switch
    {
        SenderKinds.Microsoft => "Outlook", SenderKinds.Google => "Gmail", SenderKinds.Smtp => "SMTP", _ => "Vantra Flow",
    };

    /// <summary>The password's sealing, bound to the workspace and the mailbox.</summary>
    public static string SealId(string mailboxId) => mailboxId + ":smtp";

    public async Task<SenderRef> ResolveAsync(Guid workspaceId, SenderRef step)
    {
        if (step.Kind != SenderKinds.Default) return step;
        var fallback = await store.ReadyAsync() ? await store.DefaultAsync(workspaceId) : null;
        return SenderRules.Resolve(step, fallback);
    }

    /// <summary>Vantra Flow's own mailbox, as the run log names it.</summary>
    public OpenSender Vantra() => new(SenderRules.VantraFlow, routes.VantraAddress ?? "", "Vantra Flow");

    /// <summary>The sender ready to send, or why it can't (Vantra Flow always opens: its own step checks the server's settings).</summary>
    public async Task<OpenSender> OpenAsync(Guid workspaceId, SenderRef who)
    {
        if (!who.Own) return Vantra();
        if (who.Kind == SenderKinds.Smtp)
        {
            if (!await store.ReadyAsync()) return new(who, "", "", Error: "SMTP mailboxes arrive with the next server update.");
            var row = await store.GetAsync(workspaceId, who.MailboxId!);
            if (row is null) return new(who, "", "", Error: "That mailbox isn't in this workspace. Choose who it's sent from again.");
            if (row.RemovedAt is not null) return new(who, row.FromEmail, row.FromName ?? row.FromEmail, Error: Disconnected(row.FromEmail));
            if (await plan.RefusalAsync(workspaceId, SenderKinds.Smtp) is { } no) return new(who, row.FromEmail, row.FromName ?? row.FromEmail, Error: no);
            if (secrets.Unprotect(row.PasswordCipher, workspaceId, SealId(row.Id)) is not { } password)
                return new(who, row.FromEmail, row.FromName ?? row.FromEmail, Error: $"The mailbox {row.FromEmail} can't be opened. Reconnect it in Connections.");
            return new(who, row.FromEmail, row.FromName ?? row.FromEmail,
                new SmtpRules.Settings(row.Host, row.Port, row.Security, row.Username, password, row.FromEmail, row.FromName));
        }
        var name = ProviderName(who.Kind);
        if (!await accounts.ReadyAsync()) return new(who, "", name, Error: $"{name} arrives with the next server update.");
        var live = await accounts.LiveAsync(workspaceId, who.Kind);
        if (live is null)
        {
            var was = await accounts.LatestAsync(workspaceId, who.Kind);
            var email = was?.AccountEmail ?? was?.AccountName;
            return new(who, email ?? "", name, Error: email is { Length: > 0 } ? Disconnected(email)
                : $"{name} isn't connected. Connect {KindLabel(who.Kind)} in Connections, or choose who it's sent from.");
        }
        var address = live.AccountEmail ?? live.AccountName;
        if (await plan.RefusalAsync(workspaceId, who.Kind) is { } refused) return new(who, address, live.AccountName, Error: refused);
        return new(who, address, live.AccountName);
    }

    // ── Per mailbox: at most PerMailboxPerMinute a minute (across this instance) ──

    private static readonly PartitionedRateLimiter<string> Mailboxes = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PerMailboxPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    /// <summary>One email from this mailbox this minute; false when it has sent its share.</summary>
    public static bool TakeTurn(Guid workspaceId, SenderRef who)
    {
        using var lease = Mailboxes.AttemptAcquire($"{workspaceId:N}|{who.Value}");
        return lease.IsAcquired;
    }

    /// <summary>Sends one email from an own mailbox (opened by <see cref="OpenAsync"/>).</summary>
    public async Task<SendResult> SendAsync(Guid workspaceId, OpenSender from, OutgoingMail mail, CancellationToken ct)
    {
        if (!from.Ok) return new(false, from.Error, from.Retryable);
        if (!TakeTurn(workspaceId, from.Ref))
            return new(false, $"The mailbox {from.Email} has sent {PerMailboxPerMinute} emails in the last minute. Trying again shortly.", Retryable: true);
        if (from.Ref.Kind == SenderKinds.Smtp)
        {
            var r = await routes.SendSmtpAsync(from.Smtp!, mail, ct);
            if (r.Ok) { await store.TouchAsync(workspaceId, from.Ref.MailboxId!, null); return SendResult.Done; }
            if (r.SignIn)
            {
                await store.TouchAsync(workspaceId, from.Ref.MailboxId!, r.Error);
                return new(false, $"The mailbox {from.Email} couldn't sign in: {r.Error} Reconnect it in Connections.");
            }
            return new(false, $"The mailbox {from.Email} couldn't send it. {r.Error}", r.Retryable);
        }
        try
        {
            await routes.SendAccountAsync(workspaceId, from.Ref.Kind, mail.To, mail.Subject, mail.Html, mail.ReplyTo, ct);
            return SendResult.Done;
        }
        catch (AppRevokedException) { return new(false, Disconnected(from.Email)); }
        catch (OAuth.OAuthUnauthorizedException) { return new(false, Disconnected(from.Email)); }
        catch (AppRefusedException e) when (e.Code is "reconnect" or "not_connected") { return new(false, Disconnected(from.Email)); }
        catch (AppRefusedException e) { return new(false, e.Message, e.Status is 409 or 423 or 429); }
        catch (AppUnavailableException e) { return new(false, $"{ProviderName(from.Ref.Kind)} couldn't send it right now. {e.Message}", Retryable: true); }
        catch (OAuth.RateLimitedException e) { return new(false, e.Message, Retryable: true); }
    }

    // ── The pickers ──

    /// <summary>
    /// The "Send from" choices: Vantra Flow, the connected Microsoft 365 and Google accounts, and the SMTP mailboxes; plus the
    /// workspace's default, as a choice that needs a look when what it names is gone.
    /// </summary>
    public async Task<SendersDto> ListAsync(Guid workspaceId, bool canManage)
    {
        var options = new List<SenderOptionDto>
        {
            new(SenderKinds.VantraFlow, SenderKinds.VantraFlow, "Vantra Flow", routes.VantraAddress, routes.VantraCanSend ? "ok" : "not_set_up", false,
                routes.VantraCanSend ? null : "Not set up yet"),
        };
        if (await accounts.ReadyAsync())
            foreach (var kind in new[] { SenderKinds.Microsoft, SenderKinds.Google })
                if (await accounts.LiveAsync(workspaceId, kind) is { } c)
                    options.Add(new(kind, kind, KindLabel(kind), c.AccountEmail ?? c.AccountName, c.Status == "active" ? "ok" : "error", plan.IsPremium(kind),
                        c.Status == "active" ? null : "Needs reconnecting"));
        var ready = await store.ReadyAsync();
        string? kept = null;
        if (ready)
        {
            foreach (var m in await store.ListAsync(workspaceId))
                options.Add(new($"smtp:{m.Id}", SenderKinds.Smtp, m.FromName ?? m.FromEmail, m.FromEmail, m.Status == "active" ? "ok" : "error", plan.IsPremium(SenderKinds.Smtp),
                    m.Status == "active" ? null : "Needs reconnecting"));
            kept = await store.DefaultAsync(workspaceId);
        }
        var def = SenderRules.Resolve(SenderRules.Default, kept);
        if (!options.Any(o => o.Value == def.Value))
        {
            // The default names a mailbox that's gone: shown, so it can be seen and changed.
            var open = await OpenAsync(workspaceId, def);
            options.Add(new(def.Value, def.Kind, open.Email is { Length: > 0 } e ? e : KindLabel(def.Kind), open.Email, "disconnected", plan.IsPremium(def.Kind), "Disconnected"));
        }
        return new SendersDto(ready, canManage, def.Value, await plan.AllowsPremiumAsync(workspaceId), options);
    }
}
