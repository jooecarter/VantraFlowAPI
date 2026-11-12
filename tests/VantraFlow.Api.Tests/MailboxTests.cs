using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Billing;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Mail;
using Xunit;

namespace VantraFlow.Api.Tests;

// ── Fakes ──

public sealed class FakeMailboxes : IMailboxStore
{
    public bool Ready = true;
    public readonly List<MailboxRow> Rows = [];
    public readonly Dictionary<Guid, string?> Defaults = [];
    public readonly List<(string Id, string? Error)> Touched = [];

    public Task<bool> ReadyAsync() => Task.FromResult(Ready);
    public Task<IReadOnlyList<MailboxRow>> ListAsync(Guid ws) => Task.FromResult<IReadOnlyList<MailboxRow>>(Rows.Where(r => r.WorkspaceId == ws && r.RemovedAt is null).ToList());
    public Task<MailboxRow?> GetAsync(Guid ws, string id) => Task.FromResult(Rows.FirstOrDefault(r => r.WorkspaceId == ws && r.Id == id));
    public Task<bool> InsertAsync(MailboxRow row, int max)
    {
        if (Rows.Count(r => r.WorkspaceId == row.WorkspaceId && r.RemovedAt is null) >= max) return Task.FromResult(false);
        Rows.Add(row);
        return Task.FromResult(true);
    }
    public Task<bool> UpdateAsync(Guid ws, string id, SmtpRules.Settings s, byte[]? cipher, DateTime now)
    {
        var i = Rows.FindIndex(r => r.WorkspaceId == ws && r.Id == id);
        Rows[i] = Rows[i] with { Host = s.Host, Port = s.Port, Security = s.Security, Username = s.Username, PasswordCipher = cipher ?? Rows[i].PasswordCipher, FromEmail = s.FromEmail };
        return Task.FromResult(true);
    }
    public Task RemoveAsync(Guid ws, string id, DateTime now)
    {
        var i = Rows.FindIndex(r => r.WorkspaceId == ws && r.Id == id);
        Rows[i] = Rows[i] with { PasswordCipher = null, RemovedAt = now };
        return Task.CompletedTask;
    }
    public Task TouchAsync(Guid ws, string id, string? error)
    {
        Touched.Add((id, error));
        var i = Rows.FindIndex(r => r.WorkspaceId == ws && r.Id == id);
        if (i >= 0) Rows[i] = Rows[i] with { Status = error is null ? "active" : "error", LastError = error };
        return Task.CompletedTask;
    }
    public Task<string?> DefaultAsync(Guid ws) => Task.FromResult(Defaults.GetValueOrDefault(ws));
    public Task SetDefaultAsync(Guid ws, string? value) { Defaults[ws] = value; return Task.CompletedTask; }
}

public sealed class FakeAccounts : ISenderAccounts
{
    public readonly List<OAuthConnectionRow> Rows = [];
    public bool Ready = true;
    public Task<bool> ReadyAsync() => Task.FromResult(Ready);
    public Task<OAuthConnectionRow?> LiveAsync(Guid ws, string provider) =>
        Task.FromResult(Rows.LastOrDefault(r => r.WorkspaceId == ws && r.Provider == provider && r.Status != "revoked"));
    public Task<OAuthConnectionRow?> LatestAsync(Guid ws, string provider) => Task.FromResult(Rows.LastOrDefault(r => r.WorkspaceId == ws && r.Provider == provider));

    public void Add(Guid ws, string provider, string email, string status = "active") =>
        Rows.Add(new OAuthConnectionRow(Guid.NewGuid(), ws, provider, "acc", "Alex Morgan", email, null, null, "Mail.Send", [1], null, null, status, null, "alex", "Alex", DateTime.UtcNow, null));
}

public sealed class FakeRoutes : IMailRoutes
{
    public bool VantraCanSend { get; set; } = true;
    public string? VantraAddress => "flow@example.com";
    public readonly List<string> Sent = [];
    public Exception? AccountThrows;
    public SmtpResult SmtpAnswers = SmtpResult.Done;

    public Task<bool> SendVantraAsync(string replyTo, string fromName, string to, string subject, string html) { Sent.Add($"vantra:{to}"); return Task.FromResult(true); }
    public Task SendAccountAsync(Guid ws, string provider, string to, string subject, string html, string? replyTo, CancellationToken ct)
    {
        if (AccountThrows is not null) throw AccountThrows;
        Sent.Add($"{provider}:{to}:{replyTo}");
        return Task.CompletedTask;
    }
    public Task<SmtpResult> SendSmtpAsync(SmtpRules.Settings s, OutgoingMail m, CancellationToken ct)
    {
        if (SmtpAnswers.Ok) Sent.Add($"smtp:{s.FromEmail}:{m.To}");
        return Task.FromResult(SmtpAnswers);
    }
}

public sealed class FakeSenderPlan : ISenderPlan
{
    public bool Premium = true;
    public bool IsPremium(string app) => app is "microsoft" or "google" or "smtp";
    public string PremiumTier => Tiers.Pro;
    public Task<bool> AllowsPremiumAsync(Guid ws) => Task.FromResult(Premium);
    public Task<string?> RefusalAsync(Guid ws, string app) =>
        Task.FromResult(Premium || !IsPremium(app) ? null : "Sending from your own mailbox is on Pro and up. Upgrade, or send from Vantra Flow.");
}

public sealed class FakeSmtp : ISmtpMailer
{
    public SmtpResult Answer = SmtpResult.Done;
    public readonly List<string> Calls = [];
    public Task<SmtpResult> TestAsync(SmtpRules.Settings s, CancellationToken ct) { Calls.Add($"test:{s.Host}:{s.Port}"); return Task.FromResult(Answer); }
    public Task<SmtpResult> SendAsync(SmtpRules.Settings s, OutgoingMail m, CancellationToken ct) { Calls.Add($"send:{m.To}"); return Task.FromResult(Answer); }
}

/// <summary>Send from your own mailbox: who sends, what happens when that mailbox is gone, SMTP's checks, sealing and who may do what.</summary>
public sealed class MailboxTests
{
    private const string Password = "app-password-9f3K2q";
    private static readonly FlowSecrets Secrets = new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) }).Build());

    private readonly FakeWorkspaces db = new();
    private readonly FakeMailboxes store = new();
    private readonly FakeAccounts accounts = new();
    private readonly FakeRoutes routes = new();
    private readonly FakeSenderPlan plan = new();
    private readonly FakeSmtp smtp = new();
    private readonly FakeActivity log = new();
    private readonly Senders senders;
    private readonly Guid ws;

    public MailboxTests()
    {
        senders = new Senders(store, accounts, routes, plan, Secrets);
        ws = db.Add("Northwind", ("alex", FlowRoles.Owner), ("priya", FlowRoles.Admin), ("tom", FlowRoles.Member));
    }

    private MailboxesController Api(string? sub) =>
        new(db, store, senders, smtp, plan, Secrets, NullLogger<MailboxesController>.Instance) { ControllerContext = TeamHttp.As(sub, log) };

    private static MailboxBody Body(string host = "smtp.example.com", int port = 587, string? security = "starttls", string? password = Password) =>
        new(host, port, security, "sales@example.com", password, "sales@example.com", "Northwind Sales");

    private MailboxRow Mailbox(string email = "sales@example.com", DateTime? removed = null)
    {
        var id = FlowSecrets.NewRowId("mbx_");
        var row = new MailboxRow(id, ws, "smtp.example.com", 587, "starttls", email, Secrets.Protect(Password, ws, Senders.SealId(id), 2048), email, "Sales", "active", null,
            "alex", "Alex", DateTime.UtcNow, DateTime.UtcNow, null, removed);
        store.Rows.Add(row);
        return row;
    }

    // ── Who sends ──

    [Theory]
    [InlineData(null, "default")]
    [InlineData("", "default")]
    [InlineData("default", "default")]
    [InlineData("vantraflow", "vantraflow")]
    [InlineData("microsoft", "microsoft")]
    [InlineData("google", "google")]
    [InlineData("smtp:mbx_0123456789abcdef0123456789abcdef", "smtp")]
    public void A_steps_from_is_read_as_one_sender(string? raw, string kind) => Assert.Equal(kind, SenderRules.Parse(raw)!.Kind);

    [Theory]
    [InlineData("outlook")]
    [InlineData("smtp:")]
    [InlineData("smtp:mbx_nothex")]
    [InlineData("{{trigger.email}}")]
    public void Anything_else_is_refused_on_save(string raw)
    {
        Assert.Null(SenderRules.Parse(raw));
        var step = Build.Email("s1");
        step.Config["from"] = raw;
        Assert.Contains(DefinitionRules.Validate(Build.Flow(step), new FlowLimits()), e => e.Message == "Choose who the email is sent from.");
        Assert.DoesNotContain(DefinitionRules.Validate(Build.Flow(Build.Email("s1")), new FlowLimits()), e => e.Message == "Choose who the email is sent from.");
    }

    [Fact]
    public void The_step_wins_then_the_workspace_default_then_vantra_flow()
    {
        Assert.Equal("google", SenderRules.Resolve(new SenderRef("google"), "microsoft").Value);
        Assert.Equal("vantraflow", SenderRules.Resolve(SenderRules.VantraFlow, "microsoft").Value);
        Assert.Equal("microsoft", SenderRules.Resolve(SenderRules.Default, "microsoft").Value);
        Assert.Equal("smtp:mbx_0123456789abcdef0123456789abcdef", SenderRules.Resolve(SenderRules.Default, "smtp:mbx_0123456789abcdef0123456789abcdef").Value);
        Assert.Equal("vantraflow", SenderRules.Resolve(SenderRules.Default, null).Value);
        Assert.Equal("vantraflow", SenderRules.Resolve(SenderRules.Default, "something old").Value);
    }

    [Fact]
    public async Task Older_flows_without_from_keep_sending_from_vantra_flow_and_follow_a_default_once_set()
    {
        Assert.Equal("vantraflow", (await senders.ResolveAsync(ws, SenderRules.Parse((string?)null)!)).Value);
        store.Defaults[ws] = "microsoft";
        Assert.Equal("microsoft", (await senders.ResolveAsync(ws, SenderRules.Default)).Value);
        Assert.Equal("vantraflow", (await senders.ResolveAsync(ws, SenderRules.VantraFlow)).Value);
        // Before db/013 there's no default to read: Vantra Flow, as before.
        store.Ready = false;
        Assert.Equal("vantraflow", (await senders.ResolveAsync(ws, SenderRules.Default)).Value);
    }

    [Fact]
    public async Task A_connected_account_sends_through_the_outlook_and_gmail_code_with_the_reply_to()
    {
        accounts.Add(ws, "microsoft", "alex@example.com");
        var from = await senders.OpenAsync(ws, new SenderRef("microsoft"));
        Assert.True(from.Ok);
        Assert.Equal("alex@example.com", from.Email);
        Assert.True((await senders.SendAsync(ws, from, new OutgoingMail("ada@example.com", "Hi", "<p>Hi</p>", "me@example.com", null), default)).Ok);
        Assert.Equal("microsoft:ada@example.com:me@example.com", Assert.Single(routes.Sent));
        Assert.Equal("alex@example.com", from.Output()["email"]!.GetValue<string>());
    }

    // ── Gone or broken ──

    [Fact]
    public async Task A_disconnected_account_fails_clearly_naming_the_mailbox()
    {
        accounts.Add(ws, "google", "alex@example.com", status: "revoked");
        var from = await senders.OpenAsync(ws, new SenderRef("google"));
        Assert.Equal("The mailbox alex@example.com is disconnected. Reconnect it in Connections.", from.Error);
        Assert.False(from.Retryable);
        var never = await senders.OpenAsync(ws, new SenderRef("microsoft"));
        Assert.Equal("Microsoft 365 isn't connected. Connect Outlook in Connections, or choose who it's sent from.", never.Error);
    }

    [Fact]
    public async Task An_account_whose_sign_in_is_refused_while_sending_reads_as_disconnected()
    {
        accounts.Add(ws, "microsoft", "alex@example.com");
        var from = await senders.OpenAsync(ws, new SenderRef("microsoft"));
        routes.AccountThrows = new AppRevokedException("Microsoft 365");
        var r = await senders.SendAsync(ws, from, new OutgoingMail("ada@example.com", "Hi", "x", null, null), default);
        Assert.Equal("The mailbox alex@example.com is disconnected. Reconnect it in Connections.", r.Error);
        routes.AccountThrows = new AppRefusedException(401, "Microsoft 365 refused the connection's sign-in.", "reconnect");
        Assert.Equal("The mailbox alex@example.com is disconnected. Reconnect it in Connections.",
            (await senders.SendAsync(ws, from, new OutgoingMail("ada@example.com", "Hi", "x", null, null), default)).Error);
        routes.AccountThrows = new AppUnavailableException("Graph is down.");
        Assert.True((await senders.SendAsync(ws, from, new OutgoingMail("ada@example.com", "Hi", "x", null, null), default)).Retryable);
    }

    [Fact]
    public async Task A_removed_smtp_mailbox_fails_clearly_and_one_that_refuses_its_sign_in_is_marked()
    {
        var gone = Mailbox("old@example.com", removed: DateTime.UtcNow);
        Assert.Equal("The mailbox old@example.com is disconnected. Reconnect it in Connections.", (await senders.OpenAsync(ws, new SenderRef("smtp", gone.Id))).Error);
        Assert.Equal("That mailbox isn't in this workspace. Choose who it's sent from again.",
            (await senders.OpenAsync(ws, new SenderRef("smtp", "mbx_0123456789abcdef0123456789abcdef"))).Error);

        var box = Mailbox();
        var from = await senders.OpenAsync(ws, new SenderRef("smtp", box.Id));
        Assert.True(from.Ok);
        Assert.Equal(Password, from.Smtp!.Password);
        routes.SmtpAnswers = new SmtpResult(false, "The server refused the username or password.", SignIn: true);
        var r = await senders.SendAsync(ws, from, new OutgoingMail("ada@example.com", "Hi", "x", null, null), default);
        Assert.Equal("The mailbox sales@example.com couldn't sign in: The server refused the username or password. Reconnect it in Connections.", r.Error);
        Assert.False(r.Retryable);
        Assert.Equal("error", store.Rows.Single(x => x.Id == box.Id).Status);
    }

    [Fact]
    public async Task Own_mailboxes_need_a_plan_with_premium_apps_and_count_as_their_app()
    {
        accounts.Add(ws, "microsoft", "alex@example.com");
        plan.Premium = false;
        Assert.Equal("Sending from your own mailbox is on Pro and up. Upgrade, or send from Vantra Flow.", (await senders.OpenAsync(ws, new SenderRef("microsoft"))).Error);
        Assert.True((await senders.OpenAsync(ws, SenderRules.VantraFlow)).Ok);

        var step = Build.Email("s1");
        step.Config["from"] = "microsoft";
        var smtpStep = Build.Email("s2");
        smtpStep.Config["from"] = "smtp:mbx_0123456789abcdef0123456789abcdef";
        var uses = PlanRules.PremiumUses(Build.Flow(step, smtpStep, Build.Email("s3")), new PlanCatalog());
        Assert.Equal(["Microsoft 365", "SMTP mailboxes"], uses.Select(u => u.Label));
    }

    [Fact]
    public void Each_mailbox_sends_at_most_its_share_a_minute()
    {
        var who = new SenderRef("smtp", "mbx_0123456789abcdef0123456789abcdef");
        var w = Guid.NewGuid();
        for (var i = 0; i < Senders.PerMailboxPerMinute; i++) Assert.True(Senders.TakeTurn(w, who));
        Assert.False(Senders.TakeTurn(w, who));
        Assert.True(Senders.TakeTurn(w, new SenderRef("google")));
    }

    // ── SMTP's checks ──

    [Theory]
    [InlineData(25)]
    [InlineData(465)]
    [InlineData(587)]
    [InlineData(2525)]
    public void Only_the_mail_ports_are_allowed(int port) => Assert.NotNull(SmtpRules.Check("smtp.example.com", port, null, "u", "p", "a@example.com", null).Ok);

    [Theory]
    [InlineData(22)]
    [InlineData(80)]
    [InlineData(443)]
    [InlineData(3306)]
    [InlineData(0)]
    public void Other_ports_are_refused(int port) => Assert.Equal("port", SmtpRules.Check("smtp.example.com", port, null, "u", "p", "a@example.com", null).Field);

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("192.168.0.10")]
    [InlineData("169.254.169.254")]
    [InlineData("[::1]")]
    [InlineData("smtp.example.com:587")]
    [InlineData("https://smtp.example.com")]
    [InlineData("mail.internal")]
    [InlineData("intranet")]
    public void Hosts_that_arent_public_mail_servers_are_refused(string host) =>
        Assert.Equal("host", SmtpRules.Check(host, 587, null, "u", "p", "a@example.com", null).Field);

    [Fact]
    public void Settings_are_trimmed_and_465_defaults_to_ssl()
    {
        var (s, _, _) = SmtpRules.Check("  SMTP.Example.com. ", 465, "", " sales@example.com ", "pw", "sales@example.com", "  ");
        Assert.Equal(("smtp.example.com", "ssl", "sales@example.com", (string?)null), (s!.Host, s.Security, s.Username, s.FromName));
        Assert.Equal("starttls", SmtpRules.Check("smtp.example.com", 587, null, "u", "p", "a@example.com", null).Ok!.Security);
        Assert.Equal("fromEmail", SmtpRules.Check("smtp.example.com", 587, null, "u", "p", "not an email", null).Field);
        Assert.Equal("password", SmtpRules.Check("smtp.example.com", 587, null, "u", "pa\r\nss", "a@example.com", null).Field);
        Assert.Equal("fromName", SmtpRules.Check("smtp.example.com", 587, null, "u", "p", "a@example.com", "Sales\r\nBcc: x@example.com").Field);
    }

    [Fact]
    public void The_guard_checks_every_address_the_host_resolves_to()
    {
        Assert.Null(SmtpRules.CheckAddresses("smtp.example.com", [IPAddress.Parse("93.184.216.34")]));
        Assert.Contains("private or reserved", SmtpRules.CheckAddresses("smtp.example.com", [IPAddress.Parse("93.184.216.34"), IPAddress.Parse("10.0.0.7")]));
        Assert.Contains("private or reserved", SmtpRules.CheckAddresses("smtp.example.com", [IPAddress.Parse("169.254.169.254")]));
        Assert.Contains("private or reserved", SmtpRules.CheckAddresses("smtp.example.com", [IPAddress.Parse("::ffff:127.0.0.1")]));
        Assert.Contains("Couldn't find", SmtpRules.CheckAddresses("smtp.example.com", []));
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_never_dialled()
    {
        var mailer = new SmtpMailer(NullLogger<SmtpMailer>.Instance) { Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Loopback }) };
        var s = SmtpRules.Check("smtp.example.com", 587, null, "u", "p", "a@example.com", null).Ok!;
        var r = await mailer.TestAsync(s, default);
        Assert.False(r.Ok);
        Assert.Contains("private or reserved", r.Error);
        var meta = new SmtpMailer(NullLogger<SmtpMailer>.Instance) { Resolve = (_, _) => Task.FromResult(new[] { IPAddress.Parse("169.254.169.254") }) };
        Assert.Contains("private or reserved", (await meta.SendAsync(s, new OutgoingMail("a@example.com", "x", "x", null, null), default)).Error);
    }

    [Fact]
    public void Failures_read_in_plain_words_and_microsofts_smtp_auth_switch_is_named()
    {
        var s = SmtpRules.Check("smtp.office365.com", 587, null, "u", "p", "a@example.com", null).Ok!;
        var off = SmtpMailer.Explain(new MailKit.Security.AuthenticationException("5.7.139 Authentication unsuccessful, SmtpClientAuthentication is disabled for the Tenant."), s);
        Assert.True(off.SignIn);
        Assert.Contains("SMTP AUTH", off.Error);
        var gmail = SmtpRules.Check("smtp.gmail.com", 587, null, "u", "p", "a@example.com", null).Ok!;
        Assert.Contains("app password", SmtpMailer.Explain(new MailKit.Security.AuthenticationException("535 bad"), gmail).Error);
        Assert.True(SmtpMailer.Explain(new TimeoutException(), s).Retryable);
    }

    [Fact]
    public void Headers_cant_be_injected_through_the_message()
    {
        var s = SmtpRules.Check("smtp.example.com", 587, null, "u", "p", "sales@example.com", "Sales").Ok!;
        var m = SmtpMailer.Message(s, new OutgoingMail("ada@example.com", "Hello\r\nBcc: evil@example.com", "<p>Hi</p>", "reply@example.com", null))!;
        using var buffer = new MemoryStream();
        m.WriteTo(buffer);
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        Assert.DoesNotMatch(new Regex(@"^Bcc:", RegexOptions.Multiline), text);
        Assert.Contains("Reply-To: reply@example.com", text);
        Assert.Null(SmtpMailer.Message(s, new OutgoingMail("not an address", "x", "x", null, null)));
    }

    // ── Sealing, redaction, permissions ──

    [Fact]
    public async Task The_password_is_sealed_to_the_mailbox_and_never_returned()
    {
        var made = await Api("priya").Create(ws, Body(), default);
        Assert.Equal(201, Http.Status(made));
        Assert.Equal("test:smtp.example.com:587", Assert.Single(smtp.Calls));
        var row = Assert.Single(store.Rows);
        Assert.DoesNotContain(Password, Encoding.UTF8.GetString(row.PasswordCipher!));
        Assert.Equal(Password, Secrets.Unprotect(row.PasswordCipher, ws, Senders.SealId(row.Id)));
        Assert.Null(Secrets.Unprotect(row.PasswordCipher, Guid.NewGuid(), Senders.SealId(row.Id)));
        Assert.Null(Secrets.Unprotect(row.PasswordCipher, ws, row.Id));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(Http.Body<MailboxDto>(made)));
        Assert.DoesNotContain(Password, JsonSerializer.Serialize(Http.Body<List<MailboxDto>>(await Api("alex").List(ws))));

        // Removing it wipes the password and keeps the name, so steps can say which mailbox it was.
        Assert.Equal(204, Http.Status(await Api("alex").Delete(ws, row.Id)));
        Assert.Null(store.Rows.Single().PasswordCipher);
        Assert.Empty(Http.Body<List<MailboxDto>>(await Api("alex").List(ws)));
    }

    [Fact]
    public async Task A_mailbox_that_wont_sign_in_isnt_saved()
    {
        smtp.Answer = new SmtpResult(false, "The server refused the username or password.", SignIn: true);
        var r = await Api("alex").Create(ws, Body(), default);
        Assert.Equal(400, Http.Status(r));
        Assert.Equal("smtp_failed", Http.Code(r));
        Assert.Empty(store.Rows);
        Assert.Equal("bad_mailbox", Http.Code(await Api("alex").Create(ws, Body(host: "127.0.0.1"), default)));
        Assert.Equal("bad_mailbox", Http.Code(await Api("alex").Create(ws, Body(port: 22), default)));
    }

    [Fact]
    public async Task Owners_and_admins_manage_mailboxes_and_everyone_else_sees_the_address_only()
    {
        Assert.Equal(403, Http.Status(await Api("tom").Create(ws, Body(), default)));
        Assert.Equal(404, Http.Status(await Api("stranger").List(ws)));
        var box = Mailbox();
        var seen = Assert.Single(Http.Body<List<MailboxDto>>(await Api("tom").List(ws)));
        Assert.Equal(("sales@example.com", (string?)null, (string?)null, (int?)null), (seen.FromEmail, seen.Host, seen.Username, seen.Port));
        var full = Assert.Single(Http.Body<List<MailboxDto>>(await Api("priya").List(ws)));
        Assert.Equal(("smtp.example.com", 587), (full.Host, full.Port));
        Assert.Equal(403, Http.Status(await Api("tom").Delete(ws, box.Id)));
        Assert.Equal(403, Http.Status(await Api("tom").TestEmail(ws, box.Id, default)));
        Assert.Equal(403, Http.Status(await Api("tom").SetDefault(ws, new DefaultSenderBody("vantraflow"))));
        Assert.Equal(403, Http.Status(await Api("tom").Update(ws, box.Id, Body(), default)));
        Assert.Single(store.Rows, r => r.RemovedAt is null);
    }

    [Fact]
    public async Task The_test_email_goes_to_the_person_asking()
    {
        var box = Mailbox();
        var r = await Api("priya").TestEmail(ws, box.Id, default);
        Assert.Equal(200, Http.Status(r));
        Assert.Equal("send:priya@example.com", Assert.Single(smtp.Calls));
    }

    [Fact]
    public async Task Admins_set_the_default_within_the_plan_and_everyone_sees_the_choices()
    {
        accounts.Add(ws, "microsoft", "alex@example.com");
        var box = Mailbox();
        var set = Http.Body<SendersDto>(await Api("priya").SetDefault(ws, new DefaultSenderBody($"smtp:{box.Id}")));
        Assert.Equal($"smtp:{box.Id}", set.Default);
        Assert.Equal($"smtp:{box.Id}", store.Defaults[ws]);
        var seen = Http.Body<SendersDto>(await Api("tom").ListSenders(ws));
        Assert.False(seen.CanManage);
        Assert.Equal(["vantraflow", "microsoft", $"smtp:{box.Id}"], seen.Options.Select(o => o.Value));

        // Back to Vantra Flow: kept as no default at all.
        await Api("alex").SetDefault(ws, new DefaultSenderBody("vantraflow"));
        Assert.Null(store.Defaults[ws]);

        Assert.Equal("bad_sender", Http.Code(await Api("alex").SetDefault(ws, new DefaultSenderBody("google"))));
        plan.Premium = false;
        var refused = await Api("alex").SetDefault(ws, new DefaultSenderBody("microsoft"));
        Assert.Equal((402, "plan_premium"), (Http.Status(refused), Http.Code(refused)));

        store.Ready = false;
        Assert.Equal(503, Http.Status(await Api("alex").SetDefault(ws, new DefaultSenderBody("vantraflow"))));
        Assert.Equal(503, Http.Status(await Api("alex").Create(ws, Body(), default)));
        // Without db/013 the choices still list Vantra Flow and connected accounts, with no SMTP and no default kept.
        var before = Http.Body<SendersDto>(await Api("tom").ListSenders(ws));
        Assert.Equal((false, "vantraflow"), (before.Ready, before.Default));
    }

    [Fact]
    public async Task A_default_whose_mailbox_was_removed_stays_visible_as_disconnected()
    {
        var box = Mailbox();
        store.Defaults[ws] = $"smtp:{box.Id}";
        await Api("alex").Delete(ws, box.Id);
        var list = Http.Body<SendersDto>(await Api("alex").ListSenders(ws));
        var gone = Assert.Single(list.Options, o => o.Value == $"smtp:{box.Id}");
        Assert.Equal(("disconnected", "sales@example.com"), (gone.Status, gone.Email));
    }

    // ── db/013 ──

    [Fact]
    public void The_migration_is_azure_ready_stops_without_workspaces_and_adds_the_probes_column_last()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "013_mailboxes.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(ALTER|CREATE)\s+(TABLE|INDEX)\b", RegexOptions.IgnoreCase), outside);
        // The guard comes before any change.
        var code = Regex.Replace(sql, @"/\*.*?\*/", "", RegexOptions.Singleline);
        Assert.True(code.IndexOf("THROW 51013", StringComparison.Ordinal) < code.IndexOf("EXEC(", StringComparison.Ordinal));
        Assert.Contains("OBJECT_ID(N'flow.Workspaces', N'U') IS NULL", code);
        var last = Regex.Matches(sql, @"ADD\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("DefaultSender", last);
        Assert.True(code.LastIndexOf("DefaultSender", StringComparison.Ordinal) > code.LastIndexOf("IX_Mailboxes_Workspace", StringComparison.Ordinal));
        Assert.Contains("DefaultSender", VantraFlow.Api.Data.MailboxesRepository.ProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.MailboxesRepository.ProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_ai_builder_is_told_about_from()
    {
        var hint = VantraFlow.Api.Services.Ai.PlannerCatalog.Hints["email/send"];
        Assert.Contains(hint.Optional, o => o.StartsWith("from:", StringComparison.Ordinal) && o.Contains("never write an smtp: value"));
    }
}
