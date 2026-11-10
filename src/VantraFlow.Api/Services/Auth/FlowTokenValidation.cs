using System.Text;
using Microsoft.IdentityModel.Tokens;

namespace VantraFlow.Api.Services.Auth;

/// <summary>
/// How a session token is checked (as Orbit's OrbitTokenValidation): the suite's issuer and signing key, and Flow's
/// audience only — an Orbit or Projects session, or a connect assertion (<c>vantra-flow-to-*</c>), is refused.
/// </summary>
public static class FlowTokenValidation
{
    public static string Issuer(IConfiguration config) => config["Jwt:Issuer"] ?? "vantra";
    public static string Audience(IConfiguration config) => config["Jwt:Audience"] ?? "vantra-flow";

    public static SymmetricSecurityKey Key(IConfiguration config)
    {
        var key = config["Jwt:Key"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
    }

    public static TokenValidationParameters Session(IConfiguration config) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = Issuer(config),
        ValidateAudience = true,
        ValidAudience = Audience(config),
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = Key(config),
        ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
        NameClaimType = "sub",
    };
}
