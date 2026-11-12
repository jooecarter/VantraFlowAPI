using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace VantraFlow.Api.Services.Mail;

/// <summary>How an SMTP try went: sent (or signed in), or why not; <c>SignIn</c>: the server refused the username or password.</summary>
public sealed record SmtpResult(bool Ok, string? Error = null, bool Retryable = false, bool SignIn = false)
{
    public static readonly SmtpResult Done = new(true);
}

/// <summary>One email to one person, as a Send email step sends it from an own mailbox.</summary>
public sealed record OutgoingMail(string To, string Subject, string Html, string? ReplyTo, string? FromName);

/// <summary>Talks to an SMTP mailbox (<see cref="SmtpMailer"/>; a fake in tests).</summary>
public interface ISmtpMailer
{
    /// <summary>Connects and signs in, then leaves: nothing is sent.</summary>
    Task<SmtpResult> TestAsync(SmtpRules.Settings settings, CancellationToken ct);
    Task<SmtpResult> SendAsync(SmtpRules.Settings settings, OutgoingMail mail, CancellationToken ct);
}

/// <summary>
/// SMTP through MailKit, the way the suite's other mail code does it (Orbit's IMAP). Every connection:
/// <list type="bullet">
/// <item>resolves the host and refuses unless every address is public (<see cref="SmtpRules.CheckAddresses"/>, the same ranges
/// as the HTTP request step's <see cref="NetworkGuard"/>), then dials one of those very addresses, so a name that changes
/// what it points at in between (DNS rebinding) can't reach anything private;</item>
/// <item>only on ports 25, 465, 587 and 2525, with TLS always: SSL/TLS from the start, or STARTTLS before signing in (a server
/// that doesn't offer it is refused, so a password never crosses in the clear); the certificate is checked against the host;</item>
/// <item>with timeouts: 10 seconds to connect, 20 for each command, 45 in all.</item>
/// </list>
/// Never logs a password, a username or a message.
/// </summary>
public sealed partial class SmtpMailer(ILogger<SmtpMailer> log) : ISmtpMailer
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan TotalTimeout = TimeSpan.FromSeconds(45);

    /// <summary>What a host name resolves to (tests swap it).</summary>
    internal Func<string, CancellationToken, Task<IPAddress[]>> Resolve { get; init; } = NetworkGuard.ResolveAsync;

    public Task<SmtpResult> TestAsync(SmtpRules.Settings s, CancellationToken ct) => RunAsync(s, null, ct);

    public Task<SmtpResult> SendAsync(SmtpRules.Settings s, OutgoingMail mail, CancellationToken ct) => RunAsync(s, mail, ct);

    private async Task<SmtpResult> RunAsync(SmtpRules.Settings s, OutgoingMail? mail, CancellationToken ct)
    {
        if (!SmtpRules.Ports.Contains(s.Port)) return new(false, "Use port 587 (STARTTLS) or 465 (SSL/TLS). 25 and 2525 work too.");
        using var total = CancellationTokenSource.CreateLinkedTokenSource(ct);
        total.CancelAfter(TotalTimeout);
        MimeMessage? message = null;
        if (mail is not null)
        {
            if (Message(s, mail) is not { } built) return new(false, "That email can't be sent: an address in it isn't valid.");
            message = built;
        }
        var (socket, refused) = await DialAsync(s, total.Token);
        if (socket is null) return refused!;
        using var client = new SmtpClient { Timeout = (int)CommandTimeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(socket, s.Host, s.Port, s.Security == SmtpRules.Ssl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls, total.Token);
            socket = null; // the client owns it now
            client.AuthenticationMechanisms.Remove("XOAUTH2");
            await client.AuthenticateAsync(s.Username, s.Password, total.Token);
            if (message is not null) await client.SendAsync(message, total.Token);
            await client.DisconnectAsync(true, total.Token);
            return SmtpResult.Done;
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            log.LogInformation("SMTP {Host}:{Port} failed: {Kind}", s.Host, s.Port, e.GetType().Name);
            return Explain(e, s);
        }
        finally
        {
            socket?.Dispose();
        }
    }

    /// <summary>Resolves the host, refuses private and reserved addresses, and connects to the checked ones.</summary>
    private async Task<(Socket? Socket, SmtpResult? Refused)> DialAsync(SmtpRules.Settings s, CancellationToken ct)
    {
        IPAddress[] addresses;
        try { addresses = await Resolve(s.Host, ct); }
        catch (Exception e) when (e is SocketException or ArgumentException) { addresses = []; }
        if (SmtpRules.CheckAddresses(s.Host, addresses) is { } why) return (null, new(false, why));
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
        connect.CancelAfter(ConnectTimeout);
        try
        {
            await socket.ConnectAsync(addresses, s.Port, connect.Token);
            // What was actually dialled, checked once more.
            if (socket.RemoteEndPoint is not IPEndPoint ep || !NetworkGuard.IsPublic(ep.Address))
            {
                socket.Dispose();
                return (null, new(false, $"{s.Host} is on a private or reserved network, so Vantra Flow won't connect to it."));
            }
            return (socket, null);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            socket.Dispose();
            return (null, new(false, $"Couldn't reach {s.Host} on port {s.Port}. Check the server and port.", Retryable: true));
        }
    }

    internal static MimeMessage? Message(SmtpRules.Settings s, OutgoingMail m)
    {
        if (!MailboxAddress.TryParse(m.To, out var to) || !MailboxAddress.TryParse(s.FromEmail, out var from)) return null;
        var message = new MimeMessage();
        from.Name = m.FromName is { Length: > 0 } n ? n : s.FromName ?? "";
        message.From.Add(from);
        message.To.Add(to);
        if (m.ReplyTo is { Length: > 0 } reply)
        {
            if (!MailboxAddress.TryParse(reply, out var r)) return null;
            message.ReplyTo.Add(r);
        }
        message.Subject = m.Subject;
        message.Body = new BodyBuilder { HtmlBody = m.Html }.ToMessageBody();
        return message;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    private static string Clip(string s, int max)
    {
        var t = Space().Replace(s, " ").Trim();
        return t.Length <= max ? t : t[..max] + "…";
    }

    /// <summary>A failure in plain words, with what usually fixes it.</summary>
    internal static SmtpResult Explain(Exception e, SmtpRules.Settings s)
    {
        var where = $"{s.Host}:{s.Port}";
        switch (e)
        {
            case AuthenticationException a:
                if (a.Message.Contains("SmtpClientAuthentication is disabled", StringComparison.OrdinalIgnoreCase) || a.Message.Contains("5.7.139"))
                    return new(false, "Microsoft 365 has SMTP sign-in turned off for this mailbox. Connect Outlook instead, or ask your admin to turn on SMTP AUTH.", SignIn: true);
                if (s.Host.EndsWith("gmail.com") || s.Host.EndsWith("googlemail.com") || a.Message.Contains("Application-specific password", StringComparison.OrdinalIgnoreCase))
                    return new(false, "Gmail refused the sign-in. Use an app password, not your usual one.", SignIn: true);
                return new(false, "The server refused the username or password.", SignIn: true);
            case SslHandshakeException:
                return new(false, $"Couldn't make a secure connection to {where}. Port 465 uses SSL/TLS and 587 uses STARTTLS.");
            case NotSupportedException when s.Security == SmtpRules.StartTls:
                return new(false, $"{where} doesn't offer STARTTLS. Try port 465 with SSL/TLS.");
            case SmtpCommandException c:
                var said = Clip(c.Message, 200);
                return c.ErrorCode switch
                {
                    SmtpErrorCode.SenderNotAccepted => new(false, $"The server won't send as {s.FromEmail}. Use an address this mailbox owns. ({said})"),
                    SmtpErrorCode.RecipientNotAccepted => new(false, $"The server refused the recipient. ({said})"),
                    _ => new(false, $"The server refused it: {said}", Retryable: (int)c.StatusCode is >= 400 and < 500),
                };
            case OperationCanceledException or TimeoutException:
                return new(false, $"{where} didn't answer in time.", Retryable: true);
            case SmtpProtocolException or ServiceNotConnectedException or IOException or SocketException:
                return new(false, $"Lost the connection to {where}.", Retryable: true);
            default:
                return new(false, $"Couldn't use {where}.", Retryable: true);
        }
    }
}
