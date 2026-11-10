using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services;

/// <summary>
/// Verifies a Microsoft (Entra / MSAL) ID token using the v2.0 "common"
/// metadata keys. Because the app accepts personal + any-org accounts, the
/// issuer is tenant-specific, so we validate it loosely (must be a Microsoft
/// v2.0 issuer) while strictly checking audience, signature and lifetime.
/// </summary>
public sealed class MicrosoftTokenVerifier
{
    private readonly string _clientId;
    private readonly ConfigurationManager<OpenIdConnectConfiguration> _configManager;

    public MicrosoftTokenVerifier(IConfiguration config)
    {
        _clientId = config["Microsoft:ClientId"] ?? "";
        _configManager = new ConfigurationManager<OpenIdConnectConfiguration>(
            "https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever());
    }

    public async Task<ExternalUser?> VerifyAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException("Microsoft:ClientId is not configured.");

        var oidc = await _configManager.GetConfigurationAsync();

        var parameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = oidc.SigningKeys,
            ValidateIssuer = true,
            // Accept any Microsoft v2.0 tenant issuer (personal + work/school).
            IssuerValidator = (issuer, _, _) =>
                issuer.StartsWith("https://login.microsoftonline.com/", StringComparison.Ordinal)
                && issuer.EndsWith("/v2.0", StringComparison.Ordinal)
                    ? issuer
                    : throw new SecurityTokenInvalidIssuerException($"Untrusted issuer: {issuer}"),
        };

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid) return null;

        var claims = result.Claims;
        var email = Str(claims, "email") ?? Str(claims, "preferred_username");
        if (string.IsNullOrWhiteSpace(email)) return null;

        var first = Str(claims, "given_name");
        var last = Str(claims, "family_name");
        if (string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(last))
        {
            // Fall back to splitting the display name when the granular
            // name claims aren't present (common for personal accounts).
            var name = Str(claims, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                var space = name.Trim().IndexOf(' ');
                first = space == -1 ? name.Trim() : name.Trim()[..space];
                last = space == -1 ? null : name.Trim()[(space + 1)..].Trim();
            }
        }

        return new ExternalUser(email, first, last);
    }

    private static string? Str(IDictionary<string, object> claims, string key) =>
        claims.TryGetValue(key, out var v) ? v?.ToString() : null;
}
