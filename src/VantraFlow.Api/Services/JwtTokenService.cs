using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services;

/// <summary>
/// Issues the signed JWT the SPA carries as a Bearer token. The signing
/// key, issuer, audience and lifetime all come from the <c>Jwt</c> config
/// section (the key lives only in appsettings.Development.local.json /
/// App Service config — never committed). The key and issuer match the
/// other Vantra APIs; only the audience differs (vantra-flow).
/// </summary>
public sealed class JwtTokenService
{
    private readonly SymmetricSecurityKey _key;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _lifetimeMinutes;

    public JwtTokenService(IConfiguration config)
    {
        var key = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException(
                "Jwt:Key is missing or too short (need >= 32 chars). Set it in " +
                "appsettings.Development.local.json locally or App Service config in production.");

        _key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
        _issuer = config["Jwt:Issuer"] ?? "vantra";
        _audience = config["Jwt:Audience"] ?? "vantra-flow";
        _lifetimeMinutes = int.TryParse(config["Jwt:LifetimeMinutes"], out var m) ? m : 60 * 24 * 7;
    }

    public (string token, DateTime expiresAt) Create(UserDetails user)
    {
        var expiresAt = DateTime.UtcNow.AddMinutes(_lifetimeMinutes);

        // sub = the cross-product VantraID so every Vantra app keys off the
        // same stable identifier; the int UserID rides along as a convenience.
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.VantraID ?? user.UserID.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new("uid", user.UserID.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
        };
        if (!string.IsNullOrWhiteSpace(user.FirstName))
            claims.Add(new Claim(JwtRegisteredClaimNames.GivenName, user.FirstName));
        if (!string.IsNullOrWhiteSpace(user.LastName))
            claims.Add(new Claim(JwtRegisteredClaimNames.FamilyName, user.LastName));

        var token = new JwtSecurityToken(
            issuer: _issuer,
            audience: _audience,
            claims: claims,
            expires: expiresAt,
            signingCredentials: new SigningCredentials(_key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
