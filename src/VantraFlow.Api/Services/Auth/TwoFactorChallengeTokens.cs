using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace VantraFlow.Api.Services.Auth;

/// <summary>
/// What a valid challenge carries: the account, its id (for the attempt cap), how the first step was done
/// (<c>password</c>, <c>google</c>, <c>microsoft</c>) and which apps have the person's two-step sign-in on.
/// </summary>
public sealed record TwoFactorChallenge(string VantraId, Guid Id, DateTime ExpiresAt, string Method, IReadOnlyList<string> Apps);

/// <summary>
/// The short-lived token handed back when the first step passed but the person has two-step sign-in on in Vantra
/// Orbit or Vantra Projects (as Orbit's own TwoFactorChallengeTokens). It lasts 5 minutes and is signed with the
/// session key for its own audience (<c>vantra-flow-2fa</c>), so it is not a session: the bearer handler refuses it
/// (<see cref="FlowTokenValidation.Session"/>), and a session, or Orbit's or Projects' own challenge, is refused here.
/// </summary>
public sealed class TwoFactorChallengeTokens(IConfiguration config, TimeProvider? clock = null)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    private readonly SymmetricSecurityKey _key = FlowTokenValidation.Key(config);
    private readonly string _issuer = FlowTokenValidation.Issuer(config);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    /// <summary>Deliberately not the session audience.</summary>
    public string ChallengeAudience { get; } = FlowTokenValidation.Audience(config) + "-2fa";

    public string Create(string vantraId, string method, IReadOnlyList<string> apps)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: ChallengeAudience,
            claims:
            [
                new Claim(JwtRegisteredClaimNames.Sub, vantraId),
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
                new Claim(JwtRegisteredClaimNames.Amr, method),
                new Claim("apps", string.Join(',', apps)),
            ],
            notBefore: now,
            expires: now.Add(Lifetime),
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>The challenge inside a token, or null: expired, tampered with, or another kind of token.</summary>
    public TwoFactorChallenge? Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096) return null;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token.Trim(), new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = _issuer,
                ValidateAudience = true,
                ValidAudience = ChallengeAudience,
                ValidateLifetime = true,
                LifetimeValidator = (nbf, exp, _, _) =>
                {
                    var now = _clock.GetUtcNow().UtcDateTime;
                    return exp is { } e && now <= e.AddSeconds(30) && (nbf is not { } n || now >= n.AddSeconds(-30));
                },
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = _key,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            }, out var validated);
            var sub = principal.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
            var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
            var apps = (principal.FindFirst("apps")?.Value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (string.IsNullOrWhiteSpace(sub) || !Guid.TryParse(jti, out var id) || apps.Length == 0) return null;
            var method = principal.FindFirst(JwtRegisteredClaimNames.Amr)?.Value ?? "password";
            return new TwoFactorChallenge(sub, id, validated.ValidTo, method, apps);
        }
        catch (Exception e) when (e is SecurityTokenException or ArgumentException or FormatException)
        {
            return null;
        }
    }
}
