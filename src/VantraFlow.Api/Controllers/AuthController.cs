using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services;
using VantraFlow.Api.Services.Auth;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Sign-in, as Vantra Orbit's AuthController: the shared Vantra identity DB (dbo.UserDetails), so one account works
/// across the suite; BCrypt with a transparent upgrade of legacy plaintext rows; Google and Microsoft by verified ID
/// token, matched to an account by email. The token is signed with the suite's <c>Jwt:Key</c> and issuer for
/// Flow's own audience (<c>vantra-flow</c>), with the VantraId as <c>sub</c>.
/// <para>
/// Two-step sign-in, as Orbit's: when the person has it on in Vantra Orbit or Vantra Projects, a password, Google or
/// Microsoft sign-in answers <c>{ twoFactorRequired, challenge }</c> instead of a session, and <c>POST api/auth/2fa</c>
/// takes that challenge and a code (<see cref="TwoFactorSignIn"/>). The session then carries <c>amr: otp</c>.
/// If the apps can't be asked, the sign-in stops (503 <c>two_factor_unavailable</c>: "Vantra Projects didn't answer", or
/// <c>two_factor_mismatch</c>: the app refused Flow's assertion, "… and Vantra Flow aren't set up to connect yet"); it never goes ahead without.
/// </para>
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(UsersRepository users, JwtTokenService jwt, GoogleTokenVerifier google, MicrosoftTokenVerifier microsoft,
    TwoFactorSignIn twoFactor, ILogger<AuthController> log) : ControllerBase
{
    private const int BcryptWorkFactor = 11;
    public const string RateLimitPolicy = "auth";

    [HttpPost("register")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<AuthResponse>> Register([FromBody] RegisterRequest body)
    {
        var email = body.Email?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(body.FirstName) || string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(body.Password))
            return BadRequest(new { error = "First name, email and password are required." });
        if (body.Password.Length < 8) return BadRequest(new { error = "Password must be at least 8 characters." });
        if (await users.EmailExistsAsync(email)) return Conflict(new { error = "An account with that email already exists. Sign in instead." });

        var hash = BCrypt.Net.BCrypt.HashPassword(body.Password, workFactor: BcryptWorkFactor);
        var vantraId = Guid.NewGuid().ToString();
        // Transitional, as in Orbit: the legacy plaintext column stays in sync so the original Vantra app can still sign in.
        var userId = await users.CreateAsync(body.FirstName.Trim(), body.LastName?.Trim() ?? "", email, body.Password, hash, vantraId);
        // A new account: nothing to ask for yet (two-step sign-in is set up in Vantra Orbit).
        return Ok(Issue(new UserDetails { UserID = userId, FirstName = body.FirstName.Trim(), LastName = body.LastName?.Trim() ?? "", Email = email, VantraID = vantraId }, Password));
    }

    [HttpPost("login")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<AuthResponse>> Login([FromBody] LoginRequest body)
    {
        var email = body.Email?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(body.Password))
            return BadRequest(new { error = "Email and password are required." });
        var user = await users.FindByEmailAsync(email);
        if (user is null || !await VerifyAndUpgradeAsync(user, body.Password))
            return Unauthorized(new { error = "Invalid email or password." });
        if (string.IsNullOrWhiteSpace(user.VantraID))
        {
            user.VantraID = Guid.NewGuid().ToString();
            await users.SetVantraIdAsync(user.UserID, user.VantraID);
        }
        return await IssueOrChallengeAsync(user, Password, HttpContext.RequestAborted);
    }

    [HttpPost("google")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<AuthResponse>> Google([FromBody] GoogleLoginRequest body)
    {
        ExternalUser? ext;
        try
        {
            if (!string.IsNullOrWhiteSpace(body.Code) && !string.IsNullOrWhiteSpace(body.RedirectUri))
                ext = await google.ExchangeCodeAsync(body.Code, body.RedirectUri, body.CodeVerifier);
            else if (!string.IsNullOrWhiteSpace(body.Token))
                ext = await google.VerifyAsync(body.Token);
            else
                return BadRequest(new { error = "Missing Google code or token." });
        }
        catch (Exception) { ext = null; }
        if (ext is null) return Unauthorized(new { error = "Could not verify your Google sign-in." });
        return await IssueOrChallengeAsync(await FindOrCreateAsync(ext), "google", HttpContext.RequestAborted);
    }

    [HttpPost("microsoft")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<AuthResponse>> Microsoft([FromBody] SocialLoginRequest body)
    {
        if (string.IsNullOrWhiteSpace(body.Token)) return BadRequest(new { error = "Missing Microsoft token." });
        ExternalUser? ext;
        try { ext = await microsoft.VerifyAsync(body.Token); }
        catch (Exception) { ext = null; }
        if (ext is null) return Unauthorized(new { error = "Could not verify your Microsoft sign-in." });
        return await IssueOrChallengeAsync(await FindOrCreateAsync(ext), "microsoft", HttpContext.RequestAborted);
    }

    /// <summary>
    /// Finishes a sign-in that stopped at the two-step gate: the challenge it handed back and a code. 401 with
    /// <c>invalid_code</c> (try again), <c>too_many_attempts</c> or <c>challenge_expired</c> (start again), as Orbit.
    /// </summary>
    [HttpPost("2fa")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<ActionResult<TwoFactorSignInResponse>> CompleteTwoFactor([FromBody] TwoFactorCompleteRequest body, CancellationToken ct)
    {
        var result = await twoFactor.CompleteAsync(body.Challenge, body.Code, ct);
        if (result.Failure == TwoFactorFailure.Unavailable)
            return StatusCode(503, new { error = result.Error, code = result.Code ?? "two_factor_unavailable" });
        if (!result.Ok)
            return Unauthorized(new { error = result.Error, code = result.Failure switch
            {
                TwoFactorFailure.Expired => "challenge_expired",
                TwoFactorFailure.TooManyAttempts => "too_many_attempts",
                _ => "invalid_code",
            } });

        var user = await users.FindByVantraIdAsync(result.VantraId!);
        if (user is null) return Unauthorized(new { error = TwoFactorSignIn.ExpiredMessage, code = "challenge_expired" });
        var session = Issue(user, result.Method ?? Password, AppApi.SecondStepAmr);
        return Ok(new TwoFactorSignInResponse(session.Token, session.ExpiresAt, session.User, result.BackupCodesLeft));
    }

    [HttpGet("me")]
    [Authorize]
    public ActionResult<AuthUser> Me()
    {
        var uid = int.TryParse(User.FindFirstValue("uid"), out var n) ? n : 0;
        return Ok(new AuthUser(User.FindFirstValue("sub") ?? "", uid, User.FindFirstValue("email") ?? "",
            User.FindFirstValue("given_name"), User.FindFirstValue("family_name")));
    }

    private async Task<bool> VerifyAndUpgradeAsync(UserDetails user, string password)
    {
        if (!string.IsNullOrEmpty(user.PasswordHash)) return BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        if (string.IsNullOrEmpty(user.Password) || user.Password != password) return false;
        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, workFactor: BcryptWorkFactor);
        await users.SetPasswordHashAsync(user.UserID, user.PasswordHash);
        return true;
    }

    private async Task<UserDetails> FindOrCreateAsync(ExternalUser ext)
    {
        var user = await users.FindByEmailAsync(ext.Email);
        if (user is null)
        {
            var vantraId = Guid.NewGuid().ToString();
            var userId = await users.CreateSocialAsync(ext.FirstName ?? "", ext.LastName ?? "", ext.Email, vantraId);
            return new UserDetails { UserID = userId, FirstName = ext.FirstName, LastName = ext.LastName, Email = ext.Email, VantraID = vantraId };
        }
        if (string.IsNullOrWhiteSpace(user.VantraID))
        {
            user.VantraID = Guid.NewGuid().ToString();
            await users.SetVantraIdAsync(user.UserID, user.VantraID);
        }
        return user;
    }

    private const string Password = "password";

    /// <summary>A session now, or (two-step sign-in on in Orbit or Projects) a challenge to finish with a code.</summary>
    private async Task<ActionResult> IssueOrChallengeAsync(UserDetails user, string method, CancellationToken ct)
    {
        string? challenge;
        try { challenge = await twoFactor.GateAsync(user.VantraID!, method, ct); }
        catch (TwoFactorUnavailableException e) { return StatusCode(503, new { error = e.Message, code = e.Code }); }
        if (challenge is not null)
        {
            log.LogInformation("Sign-in waits for a two-step code ({Method})", method);
            return Ok(new TwoFactorChallengeResponse(true, challenge));
        }
        return Ok(Issue(user, method));
    }

    private AuthResponse Issue(UserDetails user, params string[] amr)
    {
        var (token, expiresAt) = jwt.Create(user, amr);
        return new AuthResponse(token, expiresAt, new AuthUser(user.VantraID ?? "", user.UserID, user.Email, user.FirstName, user.LastName));
    }
}
