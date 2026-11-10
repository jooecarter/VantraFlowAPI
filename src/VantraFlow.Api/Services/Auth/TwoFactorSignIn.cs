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
        if (!api.Configured) throw new AppUnavailableException($"{api.AppName} isn't set up on this server.", notSetUp: true);
        return api.TwoFactorEnabledAsync(api.Assertion(vantraId, null, Guid.Empty), ct);
    }

    public Task<AppTwoFactorVerdict> VerifyAsync(string app, string vantraId, Guid challengeId, string code, CancellationToken ct)
    {
        var api = Api(app);
        return api.TwoFactorVerifyAsync(api.Assertion(vantraId, null, Guid.Empty), code, challengeId, ct);
    }
}

/// <summary>Why an app couldn't check two-step sign-in: it didn't answer, or it refused Flow's assertion (a setup problem).</summary>
public enum TwoFactorAppProblem { Unreachable, Mismatch }

/// <summary>
/// Two-step sign-in couldn't be checked: the sign-in stops, it never goes ahead without. Names the app and why, so the
/// sign-in page can say it as it is — "Vantra Projects didn't answer" (unreachable, a timeout or a 5xx:
/// <c>two_factor_unavailable</c>), or "Vantra Projects and Vantra Flow aren't set up to connect yet" (the app refused
/// the assertion with a 4xx, or this server has no address or key for it: <c>two_factor_mismatch</c>).
/// </summary>
public sealed class TwoFactorUnavailableException : Exception
{
    public const string GenericMessage = "We couldn't check your two-step sign-in. Try again in a moment.";

    public string? App { get; }
    public TwoFactorAppProblem Problem { get; }
    public int? Status { get; }
    public string Code => Problem == TwoFactorAppProblem.Mismatch ? "two_factor_mismatch" : "two_factor_unavailable";

    public TwoFactorUnavailableException(Exception? inner = null) : base(GenericMessage, inner) { }

    private TwoFactorUnavailableException(string app, TwoFactorAppProblem problem, int? status, Exception inner)
        : base(MessageFor(app, problem), inner)
    {
        App = app;
        Problem = problem;
        Status = status;
    }

    /// <summary>What an app's failure means for the sign-in.</summary>
    public static TwoFactorUnavailableException From(string app, Exception e) => e switch
    {
        AppRefusedException r => new(app, TwoFactorAppProblem.Mismatch, r.Status, e),
        AppUnavailableException { NotSetUp: true } u => new(app, TwoFactorAppProblem.Mismatch, u.Status, e),
        AppUnavailableException u => new(app, TwoFactorAppProblem.Unreachable, u.Status, e),
        _ => new(app, TwoFactorAppProblem.Unreachable, null, e),
    };

    public static string NameOf(string app) => app switch { "orbit" => "Vantra Orbit", "projects" => "Vantra Projects", _ => app };

    public static string MessageFor(string app, TwoFactorAppProblem problem) => problem == TwoFactorAppProblem.Mismatch
        ? $"{NameOf(app)} and Vantra Flow aren't set up to connect yet."
        : $"{NameOf(app)} didn't answer. Try again in a moment.";
}

public enum TwoFactorFailure { None, Expired, TooManyAttempts, WrongCode, Unavailable }

/// <summary>The code step's outcome: the account to sign in, or why not (in the app's words when it said).</summary>
public sealed record TwoFactorCompletion(TwoFactorFailure Failure, string? Error, string? VantraId, string? Method,
    string? Kind = null, int? BackupCodesLeft = null, string? Code = null)
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
        var answers = await Task.WhenAll(apps.Apps.Select(async a =>
        {
            try { return (App: a, On: await apps.EnabledAsync(a, vantraId, ct), Failure: (TwoFactorUnavailableException?)null); }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException)
            {
                return (App: a, On: false, Failure: Logged(TwoFactorUnavailableException.From(a, e), "Two-step sign-in couldn't be checked"));
            }
        }));
        // Fail closed: one app that can't say means no answer at all.
        if (answers.FirstOrDefault(x => x.Failure is not null).Failure is { } failure) throw failure;
        return answers.Where(x => x.On).Select(x => x.App).ToList();
    }

    /// <summary>Logs the app, the kind of failure and the HTTP status (never a token or an assertion).</summary>
    private TwoFactorUnavailableException Logged(TwoFactorUnavailableException e, string what)
    {
        log.LogWarning("{What}: {App} {Problem} (HTTP {Status})", what, e.App, e.Problem, e.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
        return e;
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
        TwoFactorUnavailableException? unavailable = null;
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
                unavailable ??= Logged(TwoFactorUnavailableException.From(app, e), "A two-step code couldn't be checked");
            }
        }

        if (verdicts.Any(x => x.V.Code == "too_many_attempts")) return Fail(TwoFactorFailure.TooManyAttempts, TooManyMessage);
        if (unavailable is not null) return Fail(TwoFactorFailure.Unavailable, unavailable.Message) with { Code = unavailable.Code };
        // Turned off (or the challenge used) everywhere it was on: start again.
        if (verdicts.Count > 0 && verdicts.All(x => x.V.Code is "not_enabled" or "challenge_expired")) return Fail(TwoFactorFailure.Expired, ExpiredMessage);
        // Orbit's own words when it said why (a code used twice, a backup code); otherwise Flow's.
        var orbit = verdicts.FirstOrDefault(x => x.App == "orbit" && x.V.Code == "wrong_code").V;
        return Fail(TwoFactorFailure.WrongCode, orbit?.Error is { Length: > 0 and <= 200 } said ? said : WrongCodeMessage);
    }
}
