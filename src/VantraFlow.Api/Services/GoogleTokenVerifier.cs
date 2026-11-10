using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Google.Apis.Auth;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services;

/// <summary>
/// Handles Google sign-in. Verifies a Google ID token directly (popup/GIS
/// flow), or exchanges an authorization code for one (same-window redirect
/// flow) using the client secret, then verifies it. Either way it returns
/// the normalised user. Config: <c>Google:ClientId</c> / <c>Google:ClientSecret</c>.
/// </summary>
public sealed class GoogleTokenVerifier(IConfiguration config, IHttpClientFactory httpFactory)
{
    private readonly string _clientId = config["Google:ClientId"] ?? "";
    private readonly string _clientSecret = config["Google:ClientSecret"] ?? "";

    /// <summary>Validate an ID token (signature, audience, expiry, issuer).</summary>
    public async Task<ExternalUser?> VerifyAsync(string idToken)
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            throw new InvalidOperationException("Google:ClientId is not configured.");

        GoogleJsonWebSignature.Payload payload;
        try
        {
            payload = await GoogleJsonWebSignature.ValidateAsync(idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = new[] { _clientId } });
        }
        catch (InvalidJwtException)
        {
            return null; // bad signature / audience / expired
        }

        // Only trust a verified email — it's the key we match/create accounts on.
        if (string.IsNullOrWhiteSpace(payload.Email) || payload.EmailVerified != true)
            return null;

        return new ExternalUser(payload.Email, payload.GivenName, payload.FamilyName);
    }

    /// <summary>
    /// Exchange a one-time authorization code (from the redirect flow) for an
    /// ID token at Google's token endpoint, then verify it. redirectUri must
    /// match the one used to obtain the code; codeVerifier is the PKCE secret.
    /// </summary>
    public async Task<ExternalUser?> ExchangeCodeAsync(string code, string redirectUri, string? codeVerifier)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
            throw new InvalidOperationException("Google:ClientId / Google:ClientSecret are not configured.");

        var form = new Dictionary<string, string>
        {
            ["code"] = code,
            ["client_id"] = _clientId,
            ["client_secret"] = _clientSecret,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };
        if (!string.IsNullOrWhiteSpace(codeVerifier)) form["code_verifier"] = codeVerifier;

        var http = httpFactory.CreateClient();
        using var resp = await http.PostAsync(
            "https://oauth2.googleapis.com/token", new FormUrlEncodedContent(form));
        if (!resp.IsSuccessStatusCode) return null;

        var token = await resp.Content.ReadFromJsonAsync<GoogleTokenResponse>();
        return token?.IdToken is null ? null : await VerifyAsync(token.IdToken);
    }

    private sealed record GoogleTokenResponse(
        [property: JsonPropertyName("id_token")] string? IdToken);
}
