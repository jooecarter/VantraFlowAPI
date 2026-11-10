using System.Text.Json.Serialization;

namespace VantraFlow.Api.Models;

/// <summary>
/// Maps the shared <c>Vantra.dbo.UserDetails</c> table — the single
/// identity record used across every Vantra product. This API only ever
/// reads/writes the auth-relevant columns; other products own the rest.
/// </summary>
public sealed class UserDetails
{
    /// <summary>Identity PK. Local to the Vantra DB; not exposed to clients
    /// as the durable key — <see cref="VantraID"/> is.</summary>
    public int UserID { get; set; }

    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Email { get; set; } = "";

    /// <summary>
    /// Legacy plaintext password column the original Vantra app
    /// authenticates against. Kept in sync on register so cross-product
    /// login keeps working during the migration; retire it once every
    /// product verifies against <see cref="PasswordHash"/>.
    /// </summary>
    public string Password { get; set; } = "";

    /// <summary>
    /// BCrypt hash — the column this API owns. Null for users created
    /// before the hash column existed; backfilled transparently on their
    /// next successful sign-in (see AuthController.Login).
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// Stable, opaque cross-product user id (a GUID string). This is the
    /// value every Vantra product should key off — it goes in the JWT
    /// <c>sub</c> claim. Backfilled on login for legacy rows that lack one.
    /// </summary>
    public string? VantraID { get; set; }
}

public sealed record RegisterRequest(
    string FirstName,
    string LastName,
    string Email,
    string Password);

/// <summary>Password sign-in. <c>DeviceToken</c>: a trusted device's token from an earlier code step (skips the code while it's valid).</summary>
public sealed record LoginRequest(string Email, string Password, string? DeviceToken = null);

/// <summary>Body for the social sign-in endpoints — the provider ID token.</summary>
public sealed record SocialLoginRequest(string Token, string? DeviceToken = null);

/// <summary>
/// Google sign-in body. Either an ID token (popup/GIS flow) OR an
/// authorization code + redirect URI + PKCE verifier (same-window redirect).
/// </summary>
public sealed record GoogleLoginRequest(
    string? Token,
    string? Code,
    string? RedirectUri,
    string? CodeVerifier,
    string? DeviceToken = null);

/// <summary>Normalised identity extracted from a verified provider ID token.</summary>
public sealed record ExternalUser(string Email, string? FirstName, string? LastName);

/// <summary>
/// The user shape returned to the SPA — never includes secrets. The API
/// omits nulls by default (so Flow payloads match types.ts's optional
/// fields), but the names are always written, null or not, so the wire shape
/// stays identical to the other Vantra APIs' and the suite's login client
/// can be reused unchanged.
/// </summary>
public sealed record AuthUser(
    string VantraId,
    int UserId,
    string Email,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FirstName,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? LastName);

public sealed record AuthResponse(
    string Token,
    DateTime ExpiresAt,
    AuthUser User);
