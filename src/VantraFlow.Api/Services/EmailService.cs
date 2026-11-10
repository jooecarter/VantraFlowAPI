using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Mail;
using System.Text.Json;
using System.Text.Json.Serialization;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services;

/// <summary>
/// Sends email — copied from Vantra Orbit (itself from Vantra Folio, minus Folio's per-user SMTP tier)
/// (Flow has no per-user mail settings). Each send uses the server-wide
/// default: Microsoft Graph (an Office 365 shared mailbox) when the <c>Graph</c>
/// config section is filled in, otherwise the server-wide SMTP config
/// (<c>Smtp</c> section). Best-effort: a failure is logged and returns false,
/// never throws.
/// </summary>
public sealed class EmailService(IConfiguration config, IHttpClientFactory httpFactory, ILogger<EmailService> logger)
{
    /// <summary>The server-wide SMTP settings from the <c>Smtp</c> config section
    /// (App Service config) — the fallback when Microsoft Graph isn't configured.</summary>
    public SmtpSettings Global => new(
        config["Smtp:Host"] ?? "",
        int.TryParse(config["Smtp:Port"], out var p) ? p : 587,
        config["Smtp:User"],
        config["Smtp:Password"],
        !bool.TryParse(config["Smtp:UseSsl"], out var s) || s, // default true
        config["Smtp:From"]);

    /// <summary>App-only Microsoft Graph settings from the <c>Graph</c> config
    /// section — the server-wide default sender (an Office 365 shared mailbox)
    /// when present.</summary>
    public GraphMailSettings Graph => new(
        config["Graph:TenantId"] ?? "",
        config["Graph:ClientId"] ?? "",
        config["Graph:ClientSecret"] ?? "",
        config["Graph:Sender"] ?? "");

    // Cached app-only token (Graph). EmailService is a singleton, so one token is
    // shared across requests until it nears expiry; the lock guards the refresh.
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    /// <summary>
    /// True when a send is possible at all: Microsoft Graph, else the server-wide
    /// SMTP. When neither is configured, a "Send email" step fails with "Email isn't set up on this server".
    /// </summary>
    public bool CanSend() => Graph.IsUsable || Global.IsUsable;

    /// <summary>
    /// Send one HTML email via the server default — Microsoft Graph (the shared
    /// mailbox) when configured, else the server-wide SMTP. Returns whether it sent.
    /// </summary>
    public async Task<bool> SendAsync(string fromEmail, string fromName, string toEmail, string toName, string subject, string htmlBody)
    {
        if (Graph.IsUsable)
            return await TrySendGraphAsync(fromEmail, fromName, toEmail, toName, subject, htmlBody);
        if (Global.IsUsable)
            return await TrySendAsync(Global, fromEmail, fromName, toEmail, toName, subject, htmlBody);
        return false;
    }

    /// <summary>
    /// Send one HTML email using <paramref name="smtp"/>, as <paramref name="fromEmail"/>
    /// (or the settings' forced From, with the author in Reply-To). Returns whether it sent.
    /// </summary>
    public async Task<bool> TrySendAsync(SmtpSettings smtp, string fromEmail, string fromName, string toEmail, string toName, string subject, string htmlBody)
    {
        if (!smtp.IsUsable) return false;
        var sender = !string.IsNullOrWhiteSpace(smtp.From) ? smtp.From! : fromEmail;
        if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(toEmail)) return false;

        try
        {
            using var msg = new MailMessage
            {
                From = new MailAddress(sender, string.IsNullOrWhiteSpace(fromName) ? sender : fromName),
                Subject = subject,
                Body = htmlBody,
                IsBodyHtml = true,
            };
            if (!string.IsNullOrWhiteSpace(fromEmail)) msg.ReplyToList.Add(new MailAddress(fromEmail, fromName));
            msg.To.Add(new MailAddress(toEmail, string.IsNullOrWhiteSpace(toName) ? toEmail : toName));

            using var client = new SmtpClient(smtp.Host, smtp.Port)
            {
                EnableSsl = smtp.UseSsl,
                Credentials = string.IsNullOrWhiteSpace(smtp.User)
                    ? CredentialCache.DefaultNetworkCredentials
                    : new NetworkCredential(smtp.User, smtp.Password),
            };
            await client.SendMailAsync(msg);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP send to {To} failed", toEmail);
            return false;
        }
    }

    /// <summary>
    /// Send one HTML email via Microsoft Graph, as the configured Office 365
    /// mailbox (From: the author's name over the mailbox address; Reply-To: the
    /// author, so replies reach them directly). Uses app-only client-credentials
    /// auth — no user is signed in. Returns whether Graph accepted it.
    /// </summary>
    public async Task<bool> TrySendGraphAsync(string fromEmail, string fromName, string toEmail, string toName, string subject, string htmlBody)
    {
        var g = Graph;
        if (!g.IsUsable || string.IsNullOrWhiteSpace(toEmail)) return false;

        try
        {
            var token = await GetGraphTokenAsync(g);
            if (token is null) return false;

            var senderName = string.IsNullOrWhiteSpace(fromName) ? g.Sender : fromName;
            var payload = new GraphSendMail(
                new GraphMessage(
                    subject,
                    new GraphBody("HTML", htmlBody),
                    new GraphRecipient(new GraphAddress(g.Sender, senderName)),
                    new[] { new GraphRecipient(new GraphAddress(toEmail, string.IsNullOrWhiteSpace(toName) ? toEmail : toName)) },
                    string.IsNullOrWhiteSpace(fromEmail)
                        ? null
                        : new[] { new GraphRecipient(new GraphAddress(fromEmail, senderName)) }),
                SaveToSentItems: true);

            var http = httpFactory.CreateClient();
            using var req = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(g.Sender)}/sendMail");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            req.Content = JsonContent.Create(payload, options: GraphJson);

            using var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode) return true; // sendMail returns 202 Accepted

            var body = await resp.Content.ReadAsStringAsync();
            logger.LogWarning("Graph sendMail to {To} failed: {Status} {Body}", toEmail, (int)resp.StatusCode, body);
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Graph send to {To} failed", toEmail);
            return false;
        }
    }

    /// <summary>Get an app-only Graph token, cached until ~2 minutes before it expires.</summary>
    private async Task<string?> GetGraphTokenAsync(GraphMailSettings g)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;

        await _tokenLock.WaitAsync();
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _tokenExpiresAt) return _token;

            var http = httpFactory.CreateClient();
            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = g.ClientId,
                ["client_secret"] = g.ClientSecret,
                ["scope"] = "https://graph.microsoft.com/.default",
                ["grant_type"] = "client_credentials",
            });
            using var resp = await http.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(g.TenantId)}/oauth2/v2.0/token", form);
            if (!resp.IsSuccessStatusCode)
            {
                logger.LogWarning("Graph token request failed: {Status}", (int)resp.StatusCode);
                return null;
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            var token = root.TryGetProperty("access_token", out var t) ? t.GetString() : null;
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var ei) ? ei : 3600;
            if (string.IsNullOrEmpty(token)) return null;

            _token = token;
            _tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, expiresIn - 120));
            return _token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    // Graph /sendMail payload (camelCase, nulls omitted so an absent Reply-To
    // isn't sent as replyTo:null).
    private static readonly JsonSerializerOptions GraphJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record GraphSendMail(GraphMessage Message, bool SaveToSentItems);
    private sealed record GraphMessage(
        string Subject,
        GraphBody Body,
        GraphRecipient From,
        GraphRecipient[] ToRecipients,
        GraphRecipient[]? ReplyTo);
    private sealed record GraphBody(string ContentType, string Content);
    private sealed record GraphRecipient(GraphAddress EmailAddress);
    private sealed record GraphAddress(string Address, string Name);
}
