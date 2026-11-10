using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services;
using VantraFlow.Api.Services.Auth;
using VantraFlow.Api.Services.Connectors;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>
/// Two-step sign-in, honoured from Orbit and Projects: who is asked, the challenge (never a session), the code step's
/// tries and outcomes, an app that can't answer (never "off"), and the session's <c>amr</c>.
/// </summary>
public sealed class TwoFactorTests
{
    private const string Key = "a-test-signing-key-that-is-long-enough-0123456789";
    private const string Ada = "11111111-2222-3333-4444-555555555555";
    private const string Good = "123456";

    private static IConfiguration Config(string key = Key) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = key }).Build();

    /// <summary>Orbit and Projects as Flow sees them: who has it on, the code each takes, and whether they answer.</summary>
    private sealed class FakeApps : ITwoFactorApps
    {
        public readonly HashSet<string> On = [];
        public readonly HashSet<string> Down = [];
        public readonly HashSet<string> Refusing = [];
        public readonly Dictionary<string, string> Codes = new() { ["orbit"] = Good, ["projects"] = "654321" };
        public string? OrbitBackup;
        public readonly List<(string App, string Code)> Asked = [];

        public IReadOnlyList<string> Apps => AppConnectors.Apps;

        public Task<bool> EnabledAsync(string app, string vantraId, CancellationToken ct) =>
            Down.Contains(app) ? throw new AppUnavailableException("down", status: 503)
            : Refusing.Contains(app) ? throw new AppRefusedException(401, "Sign in to Vantra Flow again.", "assertion_mismatch")
            : Task.FromResult(On.Contains(app));

        public Task<AppTwoFactorVerdict> VerifyAsync(string app, string vantraId, Guid challengeId, string code, CancellationToken ct)
        {
            Asked.Add((app, code));
            if (Down.Contains(app)) throw new AppUnavailableException("down");
            if (Refusing.Contains(app)) throw new AppRefusedException(401, "no", "assertion_mismatch");
            if (!On.Contains(app)) return Task.FromResult(new AppTwoFactorVerdict(false, "not_enabled", "expired"));
            if (code == Codes[app]) return Task.FromResult(new AppTwoFactorVerdict(true, Kind: "totp"));
            if (app == "orbit" && code == OrbitBackup) return Task.FromResult(new AppTwoFactorVerdict(true, Kind: "backup", BackupCodesLeft: 9));
            return Task.FromResult(new AppTwoFactorVerdict(false, "wrong_code", app == "orbit" ? "Orbit says no." : "Invalid authentication code."));
        }
    }

    private static (TwoFactorSignIn SignIn, FakeApps Apps, TwoFactorChallengeTokens Tokens) Make(params string[] on)
    {
        var apps = new FakeApps();
        foreach (var a in on) apps.On.Add(a);
        var tokens = new TwoFactorChallengeTokens(Config());
        return (new TwoFactorSignIn(apps, new MemoryTwoFactorGuards(), tokens, NullLogger<TwoFactorSignIn>.Instance), apps, tokens);
    }

    [Fact]
    public async Task No_two_step_anywhere_signs_in_at_once()
    {
        var (signIn, _, _) = Make();
        Assert.Null(await signIn.GateAsync(Ada, "password", default));
    }

    [Theory]
    [InlineData("orbit")]
    [InlineData("projects")]
    public async Task On_in_either_app_asks_for_a_code_and_that_apps_code_signs_in(string app)
    {
        var (signIn, apps, tokens) = Make(app);
        var challenge = await signIn.GateAsync(Ada, "google", default);
        Assert.NotNull(challenge);
        Assert.Equal([app], tokens.Read(challenge)!.Apps);
        Assert.Equal("google", tokens.Read(challenge)!.Method);

        var done = await signIn.CompleteAsync(challenge, " " + apps.Codes[app] + " ", default);
        Assert.True(done.Ok);
        Assert.Equal(Ada, done.VantraId);
        Assert.Equal("google", done.Method);
        Assert.All(apps.Asked, a => Assert.Equal(app, a.App));     // only the app that has it on is asked
    }

    [Fact]
    public async Task On_in_both_either_apps_code_works_and_a_backup_code_goes_to_orbit_only()
    {
        var (signIn, apps, _) = Make("orbit", "projects");
        Assert.True((await signIn.CompleteAsync(await signIn.GateAsync(Ada, "password", default), "654321", default)).Ok);

        apps.OrbitBackup = "abcde-fghjk";
        apps.Asked.Clear();
        var backup = await signIn.CompleteAsync(await signIn.GateAsync(Ada, "password", default), "abcde-fghjk", default);
        Assert.True(backup.Ok);
        Assert.Equal("backup", backup.Kind);
        Assert.Equal(9, backup.BackupCodesLeft);
        Assert.Equal([("orbit", "abcde-fghjk")], apps.Asked);
    }

    [Fact]
    public async Task A_wrong_code_says_so_in_orbits_words_and_five_tries_end_the_challenge()
    {
        var (signIn, apps, _) = Make("orbit");
        var challenge = await signIn.GateAsync(Ada, "password", default);
        var wrong = await signIn.CompleteAsync(challenge, "000000", default);
        Assert.Equal(TwoFactorFailure.WrongCode, wrong.Failure);
        Assert.Equal("Orbit says no.", wrong.Error);
        for (var i = 1; i < TwoFactorSignIn.MaxAttemptsPerChallenge; i++) await signIn.CompleteAsync(challenge, "000000", default);
        var locked = await signIn.CompleteAsync(challenge, Good, default);          // even the right code, now
        Assert.Equal(TwoFactorFailure.TooManyAttempts, locked.Failure);
        Assert.Equal(TwoFactorSignIn.TooManyMessage, locked.Error);

        // Projects' wording isn't Orbit's: Flow's own.
        var (p, _, _) = Make("projects");
        Assert.Equal(TwoFactorSignIn.WrongCodeMessage, (await p.CompleteAsync(await p.GateAsync(Ada, "password", default), "000000", default)).Error);
    }

    [Fact]
    public async Task A_challenge_signs_in_once()
    {
        var (signIn, _, _) = Make("orbit");
        var challenge = await signIn.GateAsync(Ada, "password", default);
        Assert.True((await signIn.CompleteAsync(challenge, Good, default)).Ok);
        Assert.Equal(TwoFactorFailure.Expired, (await signIn.CompleteAsync(challenge, Good, default)).Failure);
    }

    [Fact]
    public async Task An_app_that_cant_answer_stops_the_sign_in_and_never_reads_as_off()
    {
        var (signIn, apps, _) = Make("orbit");
        apps.Down.Add("projects");
        await Assert.ThrowsAsync<TwoFactorUnavailableException>(() => signIn.GateAsync(Ada, "password", default));

        apps.Down.Clear();
        var challenge = await signIn.GateAsync(Ada, "password", default);
        apps.Down.Add("orbit");
        Assert.Equal(TwoFactorFailure.Unavailable, (await signIn.CompleteAsync(challenge, Good, default)).Failure);
    }

    [Fact]
    public async Task A_failure_names_the_app_and_whether_it_didnt_answer_or_refused()
    {
        var (signIn, apps, _) = Make("orbit");
        apps.Down.Add("projects");
        var down = await Assert.ThrowsAsync<TwoFactorUnavailableException>(() => signIn.GateAsync(Ada, "password", default));
        Assert.Equal("projects", down.App);
        Assert.Equal(503, down.Status);
        Assert.Equal("two_factor_unavailable", down.Code);
        Assert.StartsWith("Vantra Projects didn't answer", down.Message);

        apps.Down.Clear();
        apps.Refusing.Add("projects");
        var refused = await Assert.ThrowsAsync<TwoFactorUnavailableException>(() => signIn.GateAsync(Ada, "password", default));
        Assert.Equal("two_factor_mismatch", refused.Code);
        Assert.Equal(401, refused.Status);
        Assert.Equal("Vantra Projects and Vantra Flow aren't set up to connect yet.", refused.Message);

        // The code step says the same.
        apps.Refusing.Clear();
        var challenge = await signIn.GateAsync(Ada, "password", default);
        apps.Refusing.Add("orbit");
        var done = await signIn.CompleteAsync(challenge, Good, default);
        Assert.Equal(TwoFactorFailure.Unavailable, done.Failure);
        Assert.Equal("two_factor_mismatch", done.Code);
        Assert.Equal("Vantra Orbit and Vantra Flow aren't set up to connect yet.", done.Error);
    }

    [Fact]
    public async Task Turned_off_since_the_challenge_means_start_again()
    {
        var (signIn, apps, _) = Make("orbit");
        var challenge = await signIn.GateAsync(Ada, "password", default);
        apps.On.Clear();
        Assert.Equal(TwoFactorFailure.Expired, (await signIn.CompleteAsync(challenge, Good, default)).Failure);
    }

    [Fact]
    public async Task A_challenge_is_never_a_session_and_nothing_else_is_a_challenge()
    {
        var (signIn, _, tokens) = Make("orbit");
        var challenge = (await signIn.GateAsync(Ada, "password", default))!;
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(challenge, FlowTokenValidation.Session(Config()), out _));

        var (session, _) = new JwtTokenService(Config()).Create(new UserDetails { UserID = 1, Email = "ada@example.com", VantraID = Ada }, "password");
        Assert.Null(tokens.Read(session));
        Assert.Null(tokens.Read(OrbitStyleChallenge()));                         // Orbit's own challenge isn't Flow's
        Assert.Null(new TwoFactorChallengeTokens(Config("another-key-that-is-also-long-enough-0123456")).Read(challenge));
        Assert.Null(tokens.Read(challenge[..^4] + "AAAA"));
        Assert.Equal(TwoFactorFailure.Expired, (await signIn.CompleteAsync("not a token", Good, default)).Failure);
    }

    private static string OrbitStyleChallenge()
    {
        var now = DateTime.UtcNow;
        var t = new JwtSecurityToken("vantra", "vantra-orbit-2fa",
            [new Claim("sub", Ada), new Claim("jti", Guid.NewGuid().ToString()), new Claim("apps", "orbit")], now, now.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(t);
    }

    [Fact]
    public void The_session_says_how_it_signed_in_and_whether_it_passed_the_second_step()
    {
        var user = new UserDetails { UserID = 1, Email = "ada@example.com", VantraID = Ada };
        var (plain, _) = new JwtTokenService(Config()).Create(user, "google");
        var (passed, _) = new JwtTokenService(Config()).Create(user, "google", AppApi.SecondStepAmr);
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var a = handler.ValidateToken(plain, FlowTokenValidation.Session(Config()), out _);
        var b = handler.ValidateToken(passed, FlowTokenValidation.Session(Config()), out _);
        Assert.Equal(["google"], a.FindAll("amr").Select(c => c.Value));
        Assert.Equal(["google", "otp"], b.FindAll("amr").Select(c => c.Value));
    }

    [Fact]
    public void The_assertion_carries_the_second_step_only_when_the_session_passed_it()
    {
        var api = new OrbitApi(null!, Config());
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        Assert.DoesNotContain(handler.ReadJwtToken(api.Assertion(Ada, null, Guid.Empty)).Claims, c => c.Type == "amr");
        Assert.Equal(["otp"], handler.ReadJwtToken(api.Assertion(Ada, null, Guid.Empty, true)).Claims.Where(c => c.Type == "amr").Select(c => c.Value));
    }

    [Fact]
    public void The_attempt_sql_parses()
    {
        foreach (var sql in new[] { TwoFactorGuards.AttemptSql, TwoFactorGuards.SpendSql })
        {
            new Microsoft.SqlServer.TransactSql.ScriptDom.TSql170Parser(true).Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
    }
}
