using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.OAuth;

/// <summary>What a provider's token endpoint and account lookup gave: the tokens and whose account they act as.</summary>
public sealed record OAuthGrant(
    string AccessToken, string? RefreshToken, DateTime? ExpiresAt, string Scopes, string AccountId, string AccountName, string? AccountEmail,
    string? TeamId = null, string? BotUserId = null);

/// <summary>The provider refused the token itself (revoked, expired past refreshing, or a refresh refused): sign in again.</summary>
public sealed class OAuthUnauthorizedException(string message, bool grantGone = false) : Exception(message)
{
    /// <summary>The refresh token was refused too (invalid_grant): the connection is gone.</summary>
    public bool GrantGone { get; } = grantGone;
}

/// <summary>
/// One outside app Flow connects to with OAuth 2.0 (authorization code, with PKCE where the provider has it, and a
/// client secret, the token exchange done here). Its client id and secret come from <c>Connectors:{Name}:ClientId</c>
/// and <c>…:ClientSecret</c>; without both it isn't offered. Calls go through <see cref="SendAsync"/>, which turns the
/// provider's answers into Flow's: a refused token (<see cref="OAuthUnauthorizedException"/>), a 429 or 5xx (worth
/// retrying: <see cref="AppUnavailableException"/>, with the wait the provider asked for), or a refusal in its words
/// (<see cref="AppRefusedException"/>). Never logs a token.
/// </summary>
public abstract class OAuthProvider(IHttpClientFactory http, IConfiguration config)
{
    public const string ClientName = "flow-oauth";

    public abstract string Key { get; }
    public abstract string Name { get; }
    protected abstract string ConfigName { get; }
    /// <summary>What the person is asked to grant.</summary>
    public abstract IReadOnlyList<string> Scopes { get; }
    public virtual bool UsesPkce => true;

    public string? ClientId => config[$"Connectors:{ConfigName}:ClientId"] is { Length: > 0 } v ? v.Trim() : null;
    protected string? ClientSecret => config[$"Connectors:{ConfigName}:ClientSecret"] is { Length: > 0 } v ? v.Trim() : null;
    public virtual bool Configured => ClientId is not null && ClientSecret is not null;
    protected IConfiguration Config => config;

    public abstract string AuthorizeUrl(string state, string redirectUri, string? codeChallenge);
    public abstract Task<OAuthGrant> ExchangeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct);
    /// <summary>New tokens from a refresh token; null for a provider whose tokens don't expire.</summary>
    public abstract Task<OAuthGrant?> RefreshAsync(string refreshToken, OAuthGrant current, CancellationToken ct);
    /// <summary>Revokes the grant at the provider, where it can be (best effort).</summary>
    public virtual Task RevokeAsync(string accessToken, string? refreshToken, CancellationToken ct) => Task.CompletedTask;

    protected HttpClient Client() => http.CreateClient(ClientName);

    public static string Query(params (string Key, string? Value)[] parts) =>
        string.Join("&", parts.Where(p => p.Value is not null).Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value!)}"));

    /// <summary>A form post to a token endpoint; the JSON answer. A refused grant throws <see cref="OAuthUnauthorizedException"/>.</summary>
    protected async Task<JsonObject> TokenAsync(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct, AuthenticationHeaderValue? auth = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        if (auth is not null) req.Headers.Authorization = auth;
        HttpResponseMessage res;
        try { res = await Client().SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new AppUnavailableException($"{Name} couldn't be reached.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new AppUnavailableException($"{Name} didn't answer in time.", e); }
        using (res)
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            JsonObject? json = null;
            try { json = JsonNode.Parse(body) as JsonObject; } catch (JsonException) { }
            var error = Tokens.Text(json?["error"]);
            if ((int)res.StatusCode >= 500) throw new AppUnavailableException($"{Name} is unavailable.", status: (int)res.StatusCode);
            if (!res.IsSuccessStatusCode || error.Length > 0 || json?["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && !b)
            {
                if (error is "invalid_grant" or "invalid_refresh_token" or "token_revoked" or "invalid_code")
                    throw new OAuthUnauthorizedException($"{Name} needs you to sign in again.", grantGone: true);
                throw new AppRefusedException((int)res.StatusCode, $"{Name} refused the sign-in ({(error.Length > 0 ? error : ((int)res.StatusCode).ToString())}).", "oauth_refused");
            }
            return json ?? new JsonObject();
        }
    }

    /// <summary>
    /// A call to the provider's API with the access token. JSON in and out; <paramref name="raw"/> sends a body as it is.
    /// </summary>
    public async Task<JsonNode?> SendAsync(HttpMethod method, string url, string token, JsonNode? body, CancellationToken ct, HttpContent? raw = null)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.ParseAdd("application/json");
        if (raw is not null) req.Content = raw;
        else if (body is not null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        HttpResponseMessage res;
        try { res = await Client().SendAsync(req, ct); }
        catch (HttpRequestException e) { throw new AppUnavailableException($"{Name} couldn't be reached.", e); }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested) { throw new AppUnavailableException($"{Name} didn't answer in time.", e); }
        using (res)
        {
            var text = await res.Content.ReadAsStringAsync(ct);
            JsonNode? json = null;
            if (text.Length > 0)
                try { json = JsonNode.Parse(text); } catch (JsonException) { json = JsonValue.Create(text.Length > 500 ? text[..500] : text); }
            var code = (int)res.StatusCode;
            if (code == 429)
            {
                var wait = res.Headers.RetryAfter?.Delta ?? (res.Headers.RetryAfter?.Date is { } d ? d - DateTimeOffset.UtcNow : null);
                throw new RateLimitedException($"{Name} asked Vantra Flow to slow down.", wait);
            }
            if (code >= 500) throw new AppUnavailableException($"{Name} is unavailable ({code}).", status: code);
            if (code == 401) throw new OAuthUnauthorizedException($"{Name} refused the connection's sign-in.");
            Check(json, code);
            if (!res.IsSuccessStatusCode) throw new AppRefusedException(code, ErrorText(json) ?? $"{Name} refused the request ({code}).", "refused");
            return json;
        }
    }

    /// <summary>A provider that answers 200 with its own error (Slack's <c>ok: false</c>) turns it into Flow's here.</summary>
    protected virtual void Check(JsonNode? json, int status) { }

    /// <summary>The provider's own words for an error, from its JSON.</summary>
    protected virtual string? ErrorText(JsonNode? json) =>
        Tokens.Text(json?["error"]?["message"]) is { Length: > 0 } m ? $"{Name}: {m}" : Tokens.Text(json?["error"]) is { Length: > 0 } e && json?["error"] is JsonValue ? $"{Name}: {e}" : null;

    /// <summary>A PKCE verifier and its S256 challenge.</summary>
    public static (string Verifier, string Challenge) Pkce()
    {
        var verifier = System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var challenge = System.Buffers.Text.Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    protected static DateTime? Expiry(JsonObject json) =>
        json["expires_in"] is JsonValue v && double.TryParse(Tokens.Text(v), System.Globalization.CultureInfo.InvariantCulture, out var s) && s > 0
            ? DateTime.UtcNow.AddSeconds(s) : null;
}

/// <summary>The provider said to slow down (429), and maybe for how long.</summary>
public sealed class RateLimitedException(string message, TimeSpan? retryAfter) : Exception(message)
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>
/// Slack (OAuth v2, a bot token): posts to channels and people as the Vantra Flow app, reads the channels it's in, and
/// hears new messages through the Events API (<c>Connectors:Slack:SigningSecret</c> checks each delivery). Token
/// rotation is followed when it's on for the app. Disconnecting revokes the token (<c>auth.revoke</c>).
/// </summary>
public sealed class SlackProvider(IHttpClientFactory http, IConfiguration config) : OAuthProvider(http, config)
{
    public override string Key => "slack";
    public override string Name => "Slack";
    protected override string ConfigName => "Slack";
    public override bool UsesPkce => false;
    public override IReadOnlyList<string> Scopes { get; } =
        ["chat:write", "chat:write.public", "channels:read", "groups:read", "channels:history", "groups:history", "channels:join", "im:write", "users:read", "users:read.email"];
    public string? SigningSecret => Config["Connectors:Slack:SigningSecret"] is { Length: > 0 } v ? v.Trim() : null;
    public override bool Configured => base.Configured && SigningSecret is not null;
    public const string Api = "https://slack.com/api/";

    public override string AuthorizeUrl(string state, string redirectUri, string? codeChallenge) =>
        "https://slack.com/oauth/v2/authorize?" + Query(("client_id", ClientId), ("scope", string.Join(",", Scopes)), ("redirect_uri", redirectUri), ("state", state));

    public override async Task<OAuthGrant> ExchangeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct)
    {
        var json = await TokenAsync(Api + "oauth.v2.access",
            [new("client_id", ClientId!), new("client_secret", ClientSecret!), new("code", code), new("redirect_uri", redirectUri)], ct);
        var team = json["team"];
        // The bot user and the app: the messages Flow posts come back as events from them (the loop protection).
        var bot = $"{Tokens.Text(json["bot_user_id"])}:{Tokens.Text(json["app_id"])}";
        return new OAuthGrant(Tokens.Text(json["access_token"]), Tokens.Text(json["refresh_token"]) is { Length: > 0 } r ? r : null, Expiry(json),
            Tokens.Text(json["scope"]), Tokens.Text(team?["id"]), Tokens.Text(team?["name"]) is { Length: > 0 } n ? n : "Slack", null,
            Tokens.Text(team?["id"]), bot);
    }

    public override async Task<OAuthGrant?> RefreshAsync(string refreshToken, OAuthGrant current, CancellationToken ct)
    {
        var json = await TokenAsync(Api + "oauth.v2.access",
            [new("client_id", ClientId!), new("client_secret", ClientSecret!), new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], ct);
        return current with { AccessToken = Tokens.Text(json["access_token"]), RefreshToken = Tokens.Text(json["refresh_token"]) is { Length: > 0 } r ? r : refreshToken, ExpiresAt = Expiry(json) };
    }

    public override async Task RevokeAsync(string accessToken, string? refreshToken, CancellationToken ct)
    {
        try { await SendAsync(HttpMethod.Post, Api + "auth.revoke", accessToken, null, ct, new FormUrlEncodedContent([])); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or OAuthUnauthorizedException or RateLimitedException) { }
    }

    /// <summary>A Slack method: a form post (as Slack's Web API takes), its <c>ok</c> checked.</summary>
    public Task<JsonNode?> CallAsync(string method, string token, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, Api + method, token, null, ct, new FormUrlEncodedContent(form));

    protected override void Check(JsonNode? json, int status)
    {
        if (json?["ok"] is not JsonValue v || !v.TryGetValue<bool>(out var ok) || ok) return;
        var error = Tokens.Text(json["error"]);
        throw error switch
        {
            "invalid_auth" or "token_revoked" or "account_inactive" or "not_authed" or "token_expired" => new OAuthUnauthorizedException("Slack refused the connection's sign-in."),
            "ratelimited" => new RateLimitedException("Slack asked Vantra Flow to slow down.", null),
            "not_in_channel" => new AppRefusedException(400, "Vantra Flow isn't in that channel. Invite the app to it in Slack, then try again.", error),
            "channel_not_found" => new AppRefusedException(404, "Slack has no channel by that id, or the app can't see it.", error),
            "users_not_found" or "user_not_found" => new AppRefusedException(404, "No one in this Slack workspace has that email.", error),
            "is_archived" => new AppRefusedException(400, "That Slack channel is archived.", error),
            "msg_too_long" => new AppRefusedException(400, "The message is too long for Slack.", error),
            "no_text" => new AppRefusedException(400, "The message is empty.", error),
            "missing_scope" => new AppRefusedException(403, "Slack needs more access for this. Reconnect Slack from Connections.", error),
            _ => new AppRefusedException(400, $"Slack refused it ({error}).", error),
        };
    }
}

/// <summary>
/// Microsoft 365 through Microsoft Graph, signed in as the person (delegated): Outlook mail and calendar, and posting
/// to Teams channels. The tenant is <c>Connectors:Microsoft:Tenant</c> (default <c>common</c>: work, school and
/// personal accounts; Teams needs a work or school one). Graph has no per-app token revocation, so disconnecting
/// deletes the tokens here.
/// </summary>
public sealed class MicrosoftProvider(IHttpClientFactory http, IConfiguration config) : OAuthProvider(http, config)
{
    public override string Key => "microsoft";
    public override string Name => "Microsoft 365";
    protected override string ConfigName => "Microsoft";
    public override IReadOnlyList<string> Scopes { get; } =
        ["offline_access", "openid", "profile", "email", "User.Read", "Mail.Send", "Mail.Read", "Calendars.ReadWrite", "ChannelMessage.Send", "Team.ReadBasic.All", "Channel.ReadBasic.All"];
    public const string Graph = "https://graph.microsoft.com/v1.0/";
    private string Tenant => Config["Connectors:Microsoft:Tenant"] is { Length: > 0 } t ? t.Trim() : "common";
    private string Login => $"https://login.microsoftonline.com/{Uri.EscapeDataString(Tenant)}/oauth2/v2.0/";

    public override string AuthorizeUrl(string state, string redirectUri, string? codeChallenge) =>
        Login + "authorize?" + Query(("client_id", ClientId), ("response_type", "code"), ("redirect_uri", redirectUri), ("response_mode", "query"),
            ("scope", string.Join(" ", Scopes)), ("state", state), ("code_challenge", codeChallenge), ("code_challenge_method", "S256"), ("prompt", "select_account"));

    public override async Task<OAuthGrant> ExchangeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct)
    {
        var json = await TokenAsync(Login + "token",
        [
            new("client_id", ClientId!), new("client_secret", ClientSecret!), new("code", code), new("redirect_uri", redirectUri),
            new("grant_type", "authorization_code"), new("code_verifier", codeVerifier ?? ""), new("scope", string.Join(" ", Scopes)),
        ], ct);
        var token = Tokens.Text(json["access_token"]);
        var me = await SendAsync(HttpMethod.Get, Graph + "me?$select=id,displayName,mail,userPrincipalName", token, null, ct);
        var email = Tokens.Text(me?["mail"]) is { Length: > 0 } m ? m : Tokens.Text(me?["userPrincipalName"]);
        return new OAuthGrant(token, Tokens.Text(json["refresh_token"]), Expiry(json), Tokens.Text(json["scope"]), Tokens.Text(me?["id"]),
            Tokens.Text(me?["displayName"]) is { Length: > 0 } n ? n : email, email.Length > 0 ? email : null);
    }

    public override async Task<OAuthGrant?> RefreshAsync(string refreshToken, OAuthGrant current, CancellationToken ct)
    {
        var json = await TokenAsync(Login + "token",
        [
            new("client_id", ClientId!), new("client_secret", ClientSecret!), new("grant_type", "refresh_token"), new("refresh_token", refreshToken),
            new("scope", string.Join(" ", Scopes)),
        ], ct);
        // Microsoft hands out a new refresh token each time: keep it.
        return current with { AccessToken = Tokens.Text(json["access_token"]), RefreshToken = Tokens.Text(json["refresh_token"]) is { Length: > 0 } r ? r : refreshToken, ExpiresAt = Expiry(json) };
    }
}

/// <summary>
/// Google, signed in as the person: Gmail (send, and new mail) and Google Sheets. <c>access_type=offline</c> and
/// <c>prompt=consent</c> so a refresh token comes back each time. Disconnecting revokes the grant at Google.
/// </summary>
public sealed class GoogleProvider(IHttpClientFactory http, IConfiguration config) : OAuthProvider(http, config)
{
    public override string Key => "google";
    public override string Name => "Google";
    protected override string ConfigName => "Google";
    public override IReadOnlyList<string> Scopes { get; } =
    [
        "openid", "email", "profile",
        "https://www.googleapis.com/auth/gmail.send",
        "https://www.googleapis.com/auth/gmail.readonly",
        "https://www.googleapis.com/auth/spreadsheets",
    ];

    public override string AuthorizeUrl(string state, string redirectUri, string? codeChallenge) =>
        "https://accounts.google.com/o/oauth2/v2/auth?" + Query(("client_id", ClientId), ("redirect_uri", redirectUri), ("response_type", "code"),
            ("scope", string.Join(" ", Scopes)), ("access_type", "offline"), ("prompt", "consent"), ("include_granted_scopes", "true"), ("state", state),
            ("code_challenge", codeChallenge), ("code_challenge_method", "S256"));

    public override async Task<OAuthGrant> ExchangeAsync(string code, string redirectUri, string? codeVerifier, CancellationToken ct)
    {
        var json = await TokenAsync("https://oauth2.googleapis.com/token",
        [
            new("client_id", ClientId!), new("client_secret", ClientSecret!), new("code", code), new("redirect_uri", redirectUri),
            new("grant_type", "authorization_code"), new("code_verifier", codeVerifier ?? ""),
        ], ct);
        var token = Tokens.Text(json["access_token"]);
        var me = await SendAsync(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo", token, null, ct);
        var email = Tokens.Text(me?["email"]);
        return new OAuthGrant(token, Tokens.Text(json["refresh_token"]) is { Length: > 0 } r ? r : null, Expiry(json), Tokens.Text(json["scope"]),
            Tokens.Text(me?["sub"]), Tokens.Text(me?["name"]) is { Length: > 0 } n ? n : email, email.Length > 0 ? email : null);
    }

    public override async Task<OAuthGrant?> RefreshAsync(string refreshToken, OAuthGrant current, CancellationToken ct)
    {
        var json = await TokenAsync("https://oauth2.googleapis.com/token",
            [new("client_id", ClientId!), new("client_secret", ClientSecret!), new("grant_type", "refresh_token"), new("refresh_token", refreshToken)], ct);
        return current with { AccessToken = Tokens.Text(json["access_token"]), RefreshToken = refreshToken, ExpiresAt = Expiry(json) };
    }

    public override async Task RevokeAsync(string accessToken, string? refreshToken, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://oauth2.googleapis.com/revoke")
            { Content = new FormUrlEncodedContent([new("token", refreshToken ?? accessToken)]) };
            using var _ = await Client().SendAsync(req, ct);
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>The outside apps, by key.</summary>
public sealed class OAuthProviders(SlackProvider slack, MicrosoftProvider microsoft, GoogleProvider google)
{
    public static readonly string[] Keys = ["slack", "microsoft", "google"];
    public SlackProvider Slack => slack;
    public MicrosoftProvider Microsoft => microsoft;
    public GoogleProvider Google => google;
    public IEnumerable<OAuthProvider> All => [slack, microsoft, google];
    public OAuthProvider? For(string? key) => key switch { "slack" => slack, "microsoft" => microsoft, "google" => google, _ => null };
    public static bool IsProvider(string? key) => key is "slack" or "microsoft" or "google";
}
