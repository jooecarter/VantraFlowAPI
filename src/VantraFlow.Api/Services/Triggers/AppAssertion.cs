using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>Who an app says is asking: their VantraId, the app's workspace, the assertion's nonce and their name.</summary>
public sealed record AppCaller(string App, string VantraId, Guid AppWorkspaceId, string Nonce, string? Name);

/// <summary>Where a nonce is spent once (<c>flow.FiredKeys</c> in production; a set in tests).</summary>
public interface INonceStore
{
    /// <summary>True the first time a nonce is seen, false ever after (until the ledger is pruned, long after it expires).</summary>
    Task<bool> SpendAsync(string audience, string nonce);
}

/// <summary>
/// Orbit and Projects asking Flow, server to server: the same signed assertion Flow sends them to connect and for two-step
/// sign-in, the other way round. The app's API signs a two-minute JWT for the signed-in person (their VantraId, the app's
/// workspace id in <c>app_ws</c>, a nonce in <c>jti</c>) with the suite's shared key — Flow's <c>Connectors:SharedKey</c>,
/// which is the apps' <c>FlowIntegration:SharedKey</c>, else <c>Jwt:Key</c> — and its own audience,
/// <c>vantra-orbit-to-flow</c> or <c>vantra-projects-to-flow</c>, which no session and no Flow-to-app assertion carries.
/// Sent in <c>X-App-Assertion</c>. Each nonce works once. What it vouches for is checked again here: the Flow workspace
/// must link that app workspace, and the person must be a member of it.
/// </summary>
public sealed class AppAssertion(IConfiguration config, INonceStore nonces)
{
    public const string HeaderName = "X-App-Assertion";
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(5);
    public static string AudienceFor(string app) => $"vantra-{app}-to-flow";

    private SymmetricSecurityKey? Key =>
        (config["Connectors:SharedKey"] is { Length: >= 32 } k ? k : config["Jwt:Key"]) is { Length: >= 32 } raw
            ? new SymmetricSecurityKey(Encoding.UTF8.GetBytes(raw)) : null;

    /// <summary>The caller, or null with why (<see cref="AppAssertionReason"/>: nothing from the token, so safe to log and return).</summary>
    public async Task<(AppCaller? Caller, string Reason)> ReadAsync(string app, string? token, DateTime? now = null)
    {
        if (app is not ("orbit" or "projects")) return (null, AppAssertionReason.Audience);
        if (Key is not { } key) return (null, AppAssertionReason.NotConfigured);
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return (null, AppAssertionReason.Missing);
        var audience = AudienceFor(app);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        ClaimsPrincipal principal;
        SecurityToken validated;
        try
        {
            principal = handler.ValidateToken(token.Trim(), new TokenValidationParameters
            {
                ValidateIssuer = false,
                ValidateAudience = true,
                ValidAudience = audience,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = key,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
                LifetimeValidator = now is { } at
                    ? (nb, exp, _, _) => exp is { } e && e > at.AddSeconds(-30) && (nb is null || nb <= at.AddSeconds(30))
                    : null,
            }, out validated);
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException or FormatException)
        {
            return (null, AppAssertionReason.From(e));
        }
        if (validated.ValidTo - validated.ValidFrom > MaxLifetime + TimeSpan.FromSeconds(30)) return (null, AppAssertionReason.Lifetime);
        var sub = principal.FindFirstValue("sub");
        if (string.IsNullOrWhiteSpace(sub) || sub.Length > 64) return (null, AppAssertionReason.Claims);
        if (!Guid.TryParse(principal.FindFirstValue("app_ws"), out var ws) || ws == Guid.Empty) return (null, AppAssertionReason.Claims);
        var jti = principal.FindFirstValue("jti");
        if (string.IsNullOrWhiteSpace(jti) || jti.Length is < 16 or > 80) return (null, AppAssertionReason.Claims);
        if (!await nonces.SpendAsync(audience, jti)) return (null, AppAssertionReason.Replayed);
        var name = principal.FindFirstValue("name");
        return (new AppCaller(app, sub.Trim(), ws, jti, string.IsNullOrWhiteSpace(name) ? null : name.Trim()), AppAssertionReason.Ok);
    }

    /// <summary>
    /// Signs one as an app would (the tests, and the apps' own code mirrors it): two minutes, the shared key, this audience.
    /// </summary>
    public static string Sign(string key, string app, string vantraId, Guid appWorkspaceId, string? name = null, DateTime? now = null, TimeSpan? life = null, string? jti = null)
    {
        var claims = new List<Claim> { new("sub", vantraId), new("app_ws", appWorkspaceId.ToString()), new("jti", jti ?? Guid.NewGuid().ToString("N")) };
        if (name is not null) claims.Add(new Claim("name", name));
        var at = now ?? DateTime.UtcNow;
        var token = new JwtSecurityToken("vantra", AudienceFor(app), claims, at, at.Add(life ?? TimeSpan.FromMinutes(2)),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

/// <summary>Why an app's assertion didn't verify. None of it comes from the token.</summary>
public static class AppAssertionReason
{
    public const string Ok = "ok";
    public const string NotConfigured = "not_configured";
    public const string Missing = "missing";
    public const string Expired = "expired";
    public const string Signature = "signature";
    public const string Audience = "audience";
    public const string Lifetime = "lifetime";
    public const string Claims = "claims";
    public const string Replayed = "replayed";
    public const string Invalid = "invalid";

    public static string From(Exception e) => e switch
    {
        SecurityTokenExpiredException or SecurityTokenInvalidLifetimeException => Expired,
        SecurityTokenInvalidSignatureException or SecurityTokenSignatureKeyNotFoundException => Signature,
        SecurityTokenInvalidAudienceException => Audience,
        _ => Invalid,
    };
}
