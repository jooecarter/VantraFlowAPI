using System.Collections.Concurrent;
using Dapper;
using VantraFlow.Api.Services.Auth;

namespace VantraFlow.Api.Data;

/// <summary>
/// Tries per sign-in challenge (<c>flow.TwoFactorChallenges</c>, db/001), shared by every instance, as Orbit's
/// db/057 guard: one challenge allows <see cref="TwoFactorSignIn.MaxAttemptsPerChallenge"/> tries and is spent once it
/// has signed someone in. Kept until 5 minutes after it expires; each write first deletes up to 500 old rows. Until
/// db/001 has run (sign-in works before it), or if the write fails, the count runs in this instance's memory instead.
/// </summary>
public sealed class TwoFactorGuards(SqlConnectionFactory factory, FlowSchema schema, ILogger<TwoFactorGuards> log) : ITwoFactorGuards
{
    private static readonly MemoryTwoFactorGuards Memory = new();

    public const string AttemptSql = @"
DELETE TOP (500) FROM flow.TwoFactorChallenges WHERE ExpiresAt < DATEADD(MINUTE, -5, SYSUTCDATETIME());
MERGE flow.TwoFactorChallenges WITH (HOLDLOCK) AS t
USING (SELECT @ChallengeId AS ChallengeId) AS s ON t.ChallengeId = s.ChallengeId
WHEN MATCHED THEN UPDATE SET Attempts = t.Attempts + 1
WHEN NOT MATCHED THEN INSERT (ChallengeId, Attempts, ExpiresAt) VALUES (@ChallengeId, 1, @ExpiresAt)
OUTPUT inserted.Attempts;";

    public const string SpendSql = @"
UPDATE flow.TwoFactorChallenges SET Attempts = @Spent WHERE ChallengeId = @ChallengeId;";

    public async Task<int> CountAttemptAsync(Guid challengeId, DateTime expiresAt)
    {
        try
        {
            if (await schema.ReadyAsync())
            {
                using var conn = factory.Flow();
                return await conn.ExecuteScalarAsync<int>(AttemptSql, new { ChallengeId = challengeId, ExpiresAt = expiresAt });
            }
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "The two-step attempt count fell back to this instance's memory.");
        }
        return Memory.CountAttempt(challengeId, expiresAt);
    }

    public async Task SpendAsync(Guid challengeId, DateTime expiresAt)
    {
        Memory.Spend(challengeId, expiresAt);
        try
        {
            if (!await schema.ReadyAsync()) return;
            using var conn = factory.Flow();
            await conn.ExecuteAsync(SpendSql, new { ChallengeId = challengeId, Spent = TwoFactorSignIn.SpentAttempts });
        }
        catch (Exception ex)
        {
            log.LogWarning(ex, "Couldn't mark a two-step challenge as used.");
        }
    }
}

/// <summary>The same count in one instance's memory: before db/001, and in tests.</summary>
public sealed class MemoryTwoFactorGuards(TimeProvider? clock = null) : ITwoFactorGuards
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly ConcurrentDictionary<Guid, (int Attempts, DateTime Until)> _challenges = new();

    public int CountAttempt(Guid challengeId, DateTime expiresAt)
    {
        Prune();
        return _challenges.AddOrUpdate(challengeId, _ => (1, expiresAt.AddMinutes(5)),
            (_, v) => (Math.Min(v.Attempts + 1, TwoFactorSignIn.SpentAttempts), v.Until)).Attempts;
    }

    public void Spend(Guid challengeId, DateTime expiresAt) =>
        _challenges[challengeId] = (TwoFactorSignIn.SpentAttempts, expiresAt.AddMinutes(5));

    private void Prune()
    {
        if (_challenges.Count < 256) return;
        var now = _clock.GetUtcNow().UtcDateTime;
        foreach (var (k, v) in _challenges) if (v.Until < now) _challenges.TryRemove(k, out _);
    }

    Task<int> ITwoFactorGuards.CountAttemptAsync(Guid challengeId, DateTime expiresAt) => Task.FromResult(CountAttempt(challengeId, expiresAt));
    Task ITwoFactorGuards.SpendAsync(Guid challengeId, DateTime expiresAt) { Spend(challengeId, expiresAt); return Task.CompletedTask; }
}
