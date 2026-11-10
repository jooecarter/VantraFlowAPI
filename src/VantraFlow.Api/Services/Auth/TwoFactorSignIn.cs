using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Services.Auth;

/// <summary>Tries per challenge, shared by every instance (<see cref="Data.TwoFactorGuards"/>, db/001).</summary>
public interface ITwoFactorGuards
{
    /// <summary>Counts one more try at this challenge; returns the count so far.</summary>
    Task<int> CountAttemptAsync(Guid challengeId, DateTime expiresAt);
    /// <summary>The challenge has signed someone in: no further tries.</summary>
    Task SpendAsync(Guid challengeId, DateTime expiresAt);
}

/// <summary>Where the person's two-step sign-in lives: Vantra Orbit and Vantra Projects, asked server to server.</summary>
public interface ITwoFactorApps
{
    /// <summary>The apps to ask, in order (Orbit first: it also takes backup codes).</summary>
    IReadOnlyList<string> Apps { get; }
    /// <summary>Throws <see cref="AppUnavailableException"/> when the app can't answer.</summary>
    Task<bool> EnabledAsync(string app, string vantraId, CancellationToken ct);
    /// <summary>Throws <see cref="AppUnavailableException"/> when the app can't answer.</summary>
    Task<AppTwoFactorVerdict> VerifyAsync(string app, string vantraId, Guid challengeId, string code, CancellationToken ct);
}

/// <summary>The connectors, with an assertion for the person (no Flow workspace yet: <see cref="Guid.Empty"/>).</summary>
public sealed class AppTwoFactorApps(AppConnectors apps) : ITwoFactorApps
{
    public IReadOnlyList<string> Apps => AppConnectors.Apps;

    private AppApi Api(string app) => apps.For(app) ?? throw new AppUnavailableException("Unknown app.");

    public Task<bool> EnabledAsync(string app, string vantraId, CancellationToken ct)
    {
        var api = Api(app);
        if (!api.Configured) throw new AppUnavailableException($"{api.AppName} isn't set up on this server.");
        return api.TwoFactorEnabledAsync(api.Assertion(vantraId, null, Guid.Empty), ct);
    }

    public Task<AppTwoFactorVerdict> VerifyAsync(string app, string vantraId, Guid challengeId, string code, CancellationToken ct)
    {
        var api = Api(app);
        return api.TwoFactorVerifyAsync(api.Assertion(vantraId, null, Guid.Empty), code, challengeId, ct);
    }
}

/// <summary>Two-step sign-in couldn't be checked (an app didn't answer): the sign-in stops, it never goes ahead without.</summary>
public sealed class TwoFactorUnavailableException(Exception? inner = null)
    : Exception("We couldn't check your two-step sign-in. Try again in a moment.", inner);

public enum TwoFactorFailure { None, Expired, TooManyAttempts, WrongCode, Unavailable }

/// <summary>The code step's outcome: the account to sign in, or why not (in the app's words when it said).</summary>
public sealed record TwoFactorCompletion(TwoFactorFailure Failure, string? Error, string? VantraId, string? Method,
    string? Kind = null, int? BackupCodesLeft = null)
{
    public bool Ok => Failure == TwoFactorFailure.None && VantraId is not null;
}

/// <summary>
/// Vantra Flow honours the two-step sign-in people set up in Vantra Orbit or Vantra Projects (Flow has none of its
/// own: it is set up in Orbit). Each keeps it in its own database, so Flow asks them, server to server, with the
/// signed assertion its connectors already use. After a password, Google or Microsoft sign-in (all three alike, as
/// in Orbit), <see cref="GateAsync"/> names the apps that have it on, or none; when there are any, a 5-minute
/// challenge (<see cref="TwoFactorChallengeTokens"/>) stands in for the session, and <see cref="CompleteAsync"/> takes
/// it and a code: the authenticator's 6 digits, checked by each of those apps until one accepts it, or an Orbit backup
/// code. The apps apply their own rules (Orbit: each code once, backup codes once, five tries per challenge).
/// <para>
/// As in Orbit: one challenge allows <see cref="MaxAttemptsPerChallenge"/> tries, counted here as well, and is spent
/// once it signs someone in; a challenge whose apps have since turned it off has expired. An app that can't answer
/// stops the sign-in; nothing reads as "off" except the app saying so.
/// </para>
/// </summary>
public sealed class TwoFactorSignIn(ITwoFactorApps apps, ITwoFactorGuards guards, TwoFactorChallengeTokens challenges, ILogger<TwoFactorSignIn> log)
{
    public const int MaxAttemptsPerChallenge = 5;
    /// <summary>What a spent challenge's count is set to (more than any cap).</summary>
    public const int SpentAttempts = 1000;

    public const string ExpiredMessage = "That sign-in attempt expired. Start again.";
    public const string TooManyMessage = "Too many wrong codes. Start again.";
    public const string WrongCodeMessage = "That code isn't right. Check your authenticator app and try again.";

    /// <summary>The apps that have the person's two-step sign-in on (none: sign in now). Throws <see cref="TwoFactorUnavailableException"/>.</summary>
    public async Task<IReadOnlyList<string>> EnabledInAsync(string vantraId, CancellationToken ct)
    {
        try
        {
            var answers = await Task.WhenAll(apps.Apps.Select(async a => (App: a, On: await apps.EnabledAsync(a, vantraId, ct))));
            return answers.Where(x => x.On).Select(x => x.App).ToList();
        }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException)
        {
            log.LogWarning("Two-step sign-in couldn't be checked: {Reason}", e.Message);
            throw new TwoFactorUnavailableException(e);
        }
    }

    /// <summary>After the first step: null (sign in now) or a challenge to finish with a code.</summary>
    public async Task<string?> GateAsync(string vantraId, string method, CancellationToken ct)
    {
        var on = await EnabledInAsync(vantraId, ct);
        return on.Count == 0 ? null : challenges.Create(vantraId, method, on);
    }

    public async Task<TwoFactorCompletion> CompleteAsync(string? challengeToken, string? code, CancellationToken ct)
    {
        var challenge = challenges.Read(challengeToken);
        if (challenge is null) return new TwoFactorCompletion(TwoFactorFailure.Expired, ExpiredMessage, null, null);
        TwoFactorCompletion Fail(TwoFactorFailure f, string? error) => new(f, error, null, challenge.Method);

        // Every try counts, the right one included; a used challenge is spent.
        var attempts = await guards.CountAttemptAsync(challenge.Id, challenge.ExpiresAt);
        if (attempts > MaxAttemptsPerChallenge)
            return attempts >= SpentAttempts ? Fail(TwoFactorFailure.Expired, ExpiredMessage) : Fail(TwoFactorFailure.TooManyAttempts, TooManyMessage);

        var value = (code ?? "").Trim();
        if (value.Length == 0 || value.Length > 64) return Fail(TwoFactorFailure.WrongCode, WrongCodeMessage);
        var digits = value.Replace(" ", "");
        var totp = digits.Length == 6 && digits.All(char.IsAsciiDigit);

        var verdicts = new List<(string App, AppTwoFactorVerdict V)>();
        var unavailable = false;
        foreach (var app in challenge.Apps.Where(a => apps.Apps.Contains(a)))
        {
            if (!totp && app != "orbit") continue;      // backup codes are Orbit's alone
            try
            {
                var v = await apps.VerifyAsync(app, challenge.VantraId, challenge.Id, totp ? digits : value, ct);
                if (v.Ok)
                {
                    await guards.SpendAsync(challenge.Id, challenge.ExpiresAt);
                    return new TwoFactorCompletion(TwoFactorFailure.None, null, challenge.VantraId, challenge.Method, v.Kind ?? "totp", v.BackupCodesLeft);
                }
                verdicts.Add((app, v));
            }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException)
            {
                log.LogWarning("A two-step code couldn't be checked: {Reason}", e.Message);
                unavailable = true;
            }
        }

        if (verdicts.Any(x => x.V.Code == "too_many_attempts")) return Fail(TwoFactorFailure.TooManyAttempts, TooManyMessage);
        if (unavailable) return Fail(TwoFactorFailure.Unavailable, new TwoFactorUnavailableException().Message);
        // Turned off (or the challenge used) everywhere it was on: start again.
        if (verdicts.Count > 0 && verdicts.All(x => x.V.Code is "not_enabled" or "challenge_expired")) return Fail(TwoFactorFailure.Expired, ExpiredMessage);
        // Orbit's own words when it said why (a code used twice, a backup code); otherwise Flow's.
        var orbit = verdicts.FirstOrDefault(x => x.App == "orbit" && x.V.Code == "wrong_code").V;
        return Fail(TwoFactorFailure.WrongCode, orbit?.Error is { Length: > 0 and <= 200 } said ? said : WrongCodeMessage);
    }
}
