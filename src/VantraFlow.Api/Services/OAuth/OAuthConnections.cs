using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.OAuth;

/// <summary>An open connection: its row, a fresh access token, and the provider to call with it.</summary>
public sealed record OAuthSession(OAuthConnectionRow Row, string Token, OAuthProvider Provider);

/// <summary>
/// A workspace's connections to outside apps (db/004):
/// <list type="bullet">
/// <item><b>Connect.</b> <see cref="Start"/> seals a ten-minute state (the workspace, who asked, a one-time nonce and
/// the PKCE verifier) into the provider's consent URL. The shared callback (<c>{App:ApiBaseUrl}/api/oauth/{provider}/callback</c>)
/// opens the state, spends the nonce, swaps the code for tokens and keeps them sealed as a waiting consent; the studio,
/// signed in as the same person, claims it (<see cref="FinishAsync"/>). So a consent link someone else started can't
/// attach your account to their workspace.</item>
/// <item><b>Use.</b> <see cref="CallAsync{T}"/> opens the tokens (AES-256-GCM, bound to the row), refreshes them a
/// couple of minutes before they expire, and retries once with a fresh token when the provider refuses one. A refresh
/// the provider refuses ends the connection and pauses the flows that need it, with the reason.</item>
/// <item><b>Disconnect.</b> Revokes the grant at the provider where it can be (Slack, Google), then here.</item>
/// </list>
/// Tokens never reach a log or the studio.
/// </summary>
public sealed class OAuthConnections(
    OAuthRepository repo, FlowsRepository flows, OAuthProviders providers, FlowSecrets secrets, EngineRepository engine, IConfiguration config,
    FlowSchema schema, ILogger<OAuthConnections> log)
{
    public static readonly TimeSpan StateLife = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RefreshAhead = TimeSpan.FromMinutes(2);

    public string ApiBase => (config["App:ApiBaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow.azurewebsites.net").TrimEnd('/');
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://flow.vantrasoftware.com").TrimEnd('/');

    /// <summary>The one redirect URL each provider's app registration lists.</summary>
    public string RedirectUri(string provider) => $"{ApiBase}/api/oauth/{provider}/callback";

    // ── Connect ──

    private sealed record State(Guid Ws, string P, string Sub, string? Name, string N, string? V, long Exp);

    /// <summary>The provider's consent URL for this person and workspace.</summary>
    public string Start(OAuthProvider provider, Guid workspaceId, string sub, string? name)
    {
        string? verifier = null, challenge = null;
        if (provider.UsesPkce) (verifier, challenge) = OAuthProvider.Pkce();
        var state = new State(workspaceId, provider.Key, sub, name, FlowSecrets.NewId(""), verifier, DateTimeOffset.UtcNow.Add(StateLife).ToUnixTimeSeconds());
        var sealedState = secrets.Protect(JsonSerializer.Serialize(state), Guid.Empty, "oauth-state", FlowSecrets.MaxLargeCipherBytes);
        return provider.AuthorizeUrl(System.Buffers.Text.Base64Url.EncodeToString(sealedState), RedirectUri(provider.Key), challenge);
    }

    /// <summary>
    /// The callback: the state opened and spent, the code swapped for tokens, kept as a waiting consent for the person
    /// who started it. Where to send the browser next (the studio's Connections page, with the consent's id or why not).
    /// </summary>
    public async Task<string> CallbackAsync(OAuthProvider provider, string? code, string? stateText, string? error, CancellationToken ct)
    {
        string Back(string query) => $"{AppBase}/connections?oauth={provider.Key}&{query}";
        State? state = null;
        try
        {
            var raw = System.Buffers.Text.Base64Url.DecodeFromChars(stateText ?? "");
            if (secrets.Unprotect(raw, Guid.Empty, "oauth-state") is { } json) state = JsonSerializer.Deserialize<State>(json);
        }
        catch (Exception e) when (e is FormatException or JsonException) { }
        if (state is null || state.P != provider.Key || DateTimeOffset.FromUnixTimeSeconds(state.Exp) < DateTimeOffset.UtcNow)
            return Back("error=expired");
        if (!await engine.ClaimKeyAsync(state.Ws, "", "oauth:" + state.N)) return Back("error=expired");
        if (error is { Length: > 0 }) return Back(error == "access_denied" ? "error=denied" : "error=refused");
        if (string.IsNullOrWhiteSpace(code)) return Back("error=refused");
        OAuthGrant grant;
        try { grant = await provider.ExchangeAsync(code, RedirectUri(provider.Key), state.V, ct); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or OAuthUnauthorizedException or RateLimitedException)
        {
            log.LogInformation("OAuth exchange with {Provider} failed: {Reason}", provider.Key, e.Message);
            return Back("error=refused");
        }
        if (grant.AccessToken.Length == 0 || grant.AccountId.Length == 0) return Back("error=refused");
        var id = FlowSecrets.NewId("oap_");
        var payload = secrets.Protect(JsonSerializer.Serialize(grant), state.Ws, id, FlowSecrets.MaxLargeCipherBytes);
        await repo.InsertPendingAsync(id, state.Ws, provider.Key, state.Sub, payload, DateTime.UtcNow.Add(StateLife));
        return Back($"finish={Uri.EscapeDataString(id)}&ws={state.Ws:D}");
    }

    /// <summary>Claims a waiting consent as a live connection (replacing any earlier one, revoked at the provider too).</summary>
    public async Task<OAuthConnectionRow?> FinishAsync(OAuthProvider provider, Guid workspaceId, string pendingId, string sub, string? name, CancellationToken ct)
    {
        var raw = await repo.TakePendingAsync(pendingId, workspaceId, provider.Key, sub, DateTime.UtcNow);
        if (raw is null || secrets.Unprotect(raw, workspaceId, pendingId) is not { } json || JsonSerializer.Deserialize<OAuthGrant>(json) is not { } grant) return null;
        if (await repo.LiveAsync(workspaceId, provider.Key) is { } old) await RevokeRemoteAsync(old, ct);
        var id = Guid.NewGuid();
        var row = new OAuthConnectionRow(id, workspaceId, provider.Key, Clip(grant.AccountId, 200), Clip(grant.AccountName, 200), grant.AccountEmail is null ? null : Clip(grant.AccountEmail, 256),
            grant.TeamId, grant.BotUserId, Clip(grant.Scopes, 2000), SealAccess(grant.AccessToken, workspaceId, id),
            grant.RefreshToken is null ? null : SealRefresh(grant.RefreshToken, workspaceId, id), grant.ExpiresAt, "active", null, sub, name, DateTime.UtcNow, null);
        await repo.InsertAsync(row);
        log.LogInformation("Workspace {Workspace} connected {Provider}", workspaceId, provider.Key);
        return row;
    }

    // ── Use ──

    private byte[] SealAccess(string token, Guid ws, Guid id) => secrets.Protect(token, ws, id.ToString("N") + ":a", FlowSecrets.MaxLargeCipherBytes);
    private byte[] SealRefresh(string token, Guid ws, Guid id) => secrets.Protect(token, ws, id.ToString("N") + ":r", FlowSecrets.MaxLargeCipherBytes);
    private string? Access(OAuthConnectionRow r) => secrets.Unprotect(r.AccessCipher, r.WorkspaceId, r.Id.ToString("N") + ":a");
    private string? Refresh(OAuthConnectionRow r) => r.RefreshCipher is null ? null : secrets.Unprotect(r.RefreshCipher, r.WorkspaceId, r.Id.ToString("N") + ":r");

    /// <summary>The workspace's live connection with a fresh token. Throws <see cref="AppRefusedException"/> (not_connected) when there's none.</summary>
    public async Task<OAuthSession> OpenAsync(Guid workspaceId, string providerKey, CancellationToken ct, bool forceRefresh = false)
    {
        var provider = providers.For(providerKey) ?? throw new AppRefusedException(404, "Vantra Flow doesn't connect to that app.", "no_app");
        if (!await schema.ConnectorsReadyAsync())
            throw new AppRefusedException(409, $"{provider.Name} arrives with the next server update.", "not_connected");
        var row = await repo.LiveAsync(workspaceId, providerKey)
            ?? throw new AppRefusedException(409, $"{provider.Name} isn't connected. Connect it from Connections.", "not_connected");
        var token = Access(row) ?? throw new AppRefusedException(409, $"{provider.Name}'s connection can't be opened. Connect it again from Connections.", "not_connected");
        if (forceRefresh || row.ExpiresAt is { } exp && exp < DateTime.UtcNow.Add(RefreshAhead))
        {
            if (Refresh(row) is not { } refresh)
            {
                if (forceRefresh || row.ExpiresAt < DateTime.UtcNow) { await LostAsync(row, $"{provider.Name} needs you to sign in again."); throw new AppRevokedException(provider.Name); }
            }
            else
            {
                OAuthGrant? fresh;
                try { fresh = await provider.RefreshAsync(refresh, Grant(row, token, refresh), ct); }
                catch (OAuthUnauthorizedException)
                {
                    await LostAsync(row, $"{provider.Name} needs you to sign in again.");
                    throw new AppRevokedException(provider.Name);
                }
                if (fresh is not null && fresh.AccessToken.Length > 0)
                {
                    await repo.UpdateTokensAsync(row.Id, SealAccess(fresh.AccessToken, row.WorkspaceId, row.Id),
                        fresh.RefreshToken is { } r && r != refresh ? SealRefresh(r, row.WorkspaceId, row.Id) : null, fresh.ExpiresAt);
                    token = fresh.AccessToken;
                }
            }
        }
        return new OAuthSession(row, token, provider);
    }

    private static OAuthGrant Grant(OAuthConnectionRow r, string access, string? refresh) =>
        new(access, refresh, r.ExpiresAt, r.Scopes, r.AccountId, r.AccountName, r.AccountEmail, r.TeamId, r.BotUserId);

    /// <summary>
    /// A call on the workspace's connection: a refused token is refreshed and the call made once more; then a refusal
    /// marks the connection in error (Reconnect). Throws the app exceptions the engine knows.
    /// </summary>
    public async Task<T> CallAsync<T>(Guid workspaceId, string providerKey, Func<OAuthSession, Task<T>> call, CancellationToken ct)
    {
        try { return await CallCoreAsync(workspaceId, providerKey, call, ct); }
        catch (RateLimitedException e) { throw new AppUnavailableException(e.Message, e, 429); }
    }

    private async Task<T> CallCoreAsync<T>(Guid workspaceId, string providerKey, Func<OAuthSession, Task<T>> call, CancellationToken ct)
    {
        var session = await OpenAsync(workspaceId, providerKey, ct);
        try
        {
            var result = await call(session);
            await repo.TouchAsync(session.Row.Id, null);
            return result;
        }
        catch (OAuthUnauthorizedException)
        {
            session = await OpenAsync(workspaceId, providerKey, ct, forceRefresh: true);
            try
            {
                var result = await call(session);
                await repo.TouchAsync(session.Row.Id, null);
                return result;
            }
            catch (OAuthUnauthorizedException)
            {
                await repo.TouchAsync(session.Row.Id, $"{session.Provider.Name} refused the sign-in. Reconnect it from Connections.");
                throw new AppRefusedException(401, $"{session.Provider.Name} refused the connection's sign-in. Reconnect it from Connections.", "reconnect");
            }
        }
        catch (AppUnavailableException e)
        {
            await repo.TouchAsync(session.Row.Id, e.Message);
            throw;
        }
    }

    /// <summary>The connection is gone: revoked here, and the flows that need the app switched off with the reason.</summary>
    public async Task LostAsync(OAuthConnectionRow row, string reason)
    {
        await repo.RevokeAsync(row.Id, reason);
        var name = providers.For(row.Provider)?.Name ?? row.Provider;
        foreach (var id in await flows.UsingAppAsync(row.WorkspaceId, row.Provider))
            await flows.PauseAsync(row.WorkspaceId, id, $"{name} was disconnected.");
        log.LogInformation("OAuth connection {Connection} ({Provider}) lost", row.Id, row.Provider);
    }

    /// <summary>Disconnect from the studio: the grant revoked at the provider (where it can be), then here.</summary>
    public async Task DisconnectAsync(OAuthConnectionRow row, CancellationToken ct)
    {
        await RevokeRemoteAsync(row, ct);
        await LostAsync(row, "Disconnected in Vantra Flow.");
    }

    private async Task RevokeRemoteAsync(OAuthConnectionRow row, CancellationToken ct)
    {
        if (providers.For(row.Provider) is not { } provider || Access(row) is not { } access) return;
        try { await provider.RevokeAsync(access, Refresh(row), ct); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or OAuthUnauthorizedException or RateLimitedException) { }
    }

    public OAuthConnectionDto Dto(OAuthConnectionRow c) =>
        new(c.Id.ToString("N"), c.Provider, c.AccountName, c.AccountEmail, c.Status, c.LastError, c.ConnectedByName,
            Controllers.FlowControllerBase.Iso(c.ConnectedAt), Controllers.FlowControllerBase.Iso(c.LastUsedAt),
            c.Scopes.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>Slack's request signing: <c>v0=hex(HMAC-SHA256(signing secret, "v0:{timestamp}:{body}"))</c>, within five minutes.</summary>
public static class SlackSignature
{
    public static bool Verify(string secret, string? signature, string? timestamp, ReadOnlySpan<byte> body, DateTime nowUtc)
    {
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ts)) return false;
        var age = nowUtc - DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        if (age > TimeSpan.FromMinutes(5) || age < TimeSpan.FromMinutes(-1)) return false;
        if (signature is null || !signature.StartsWith("v0=", StringComparison.Ordinal)) return false;
        var prefix = Encoding.ASCII.GetBytes($"v0:{ts}:");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        var expected = System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
        byte[] given;
        try { given = Convert.FromHexString(signature[3..]); } catch (FormatException) { return false; }
        return given.Length == expected.Length && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>Signs a body as Slack does (tests).</summary>
    public static string Sign(string secret, long ts, string body) =>
        "v0=" + Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{ts}:{body}")));
}
