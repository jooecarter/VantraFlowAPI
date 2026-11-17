using System.Data;
using Dapper;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Shared;

// How the Vantra apps look for a person, saved once in the shared identity database (vantra.UserAppearance,
// VantraProjectsAPI db/shared/003), so Orbit, Flow and Projects match on any device. Each app shows the fields it
// supports and never clears the others. Until 003 has run every app keeps the appearance in the browser, as before.
// The same file is in Projects', Orbit's and Flow's APIs (only the namespace differs).

/// <summary>A person's saved appearance. A null field hasn't been chosen: each app uses its own default.</summary>
public sealed record SharedAppearance(
    string? Theme, string? Nav, string? Density, bool? ReduceMotion, string? Accent, bool? AiRibbon, DateTime? UpdatedAt);

/// <summary>A change: a null field is left as it is, so an app that doesn't show a field never clears it.</summary>
public sealed record SharedAppearanceBody(
    string? Theme = null, string? Nav = null, string? Density = null, bool? ReduceMotion = null, string? Accent = null, bool? AiRibbon = null)
{
    public bool Empty => Theme is null && Nav is null && Density is null && ReduceMotion is null && Accent is null && AiRibbon is null;
}

/// <summary>What the endpoints answer: <c>available</c> is false until db/shared/003 has run.</summary>
public sealed record SharedAppearanceDto(bool Available, SharedAppearance? Appearance);

public static class SharedAppearanceRules
{
    public const string InvalidCode = "invalid_appearance";
    public const string NotReadyCode = "not_ready";
    public const string UnavailableCode = "unavailable";
    /// <summary>vantra.UserAppearance.VantraId is VARCHAR(50), as the other vantra.* tables.</summary>
    public const int MaxVantraId = 50;

    public static readonly string[] Themes = ["light", "dark", "system"];
    public static readonly string[] Navs = ["match", "white", "black", "slate"];
    public static readonly string[] Densities = ["comfortable", "compact"];
    public static readonly string[] Accents = ["teal", "blue", "purple", "orange", "pink", "red"];
    public static readonly string[] Apps = [SharedApps.Projects, SharedApps.Orbit, SharedApps.Flow];

    /// <summary>The change in its stored form (trimmed, lower case), or the reason it's refused.</summary>
    public static (SharedAppearanceBody? Clean, string? Problem) Check(SharedAppearanceBody? body)
    {
        if (body is null || body.Empty) return (null, "Nothing to save.");
        string? Pick(string? value, string[] allowed, string what, out string? problem)
        {
            problem = null;
            if (value is null) return null;
            var v = value.Trim().ToLowerInvariant();
            if (Array.IndexOf(allowed, v) < 0) problem = $"That isn't a {what} we know.";
            return v;
        }
        var theme = Pick(body.Theme, Themes, "theme", out var p1);
        var nav = Pick(body.Nav, Navs, "navigation colour", out var p2);
        var density = Pick(body.Density, Densities, "density", out var p3);
        var accent = Pick(body.Accent, Accents, "accent", out var p4);
        var problem = p1 ?? p2 ?? p3 ?? p4;
        return problem is not null
            ? (null, problem)
            : (new SharedAppearanceBody(theme, nav, density, body.ReduceMotion, accent, body.AiRibbon), null);
    }

    /// <summary>Whether an account id fits the shared table (every real VantraID does).</summary>
    public static bool Fits(string? vantraId) => !string.IsNullOrWhiteSpace(vantraId) && vantraId.Length <= MaxVantraId;
}

public interface ISharedAppearanceStore
{
    /// <summary>db/shared/003's table is there (by OBJECT_ID; never a definition). False without the grant too.</summary>
    Task<bool> ProbeAsync();
    Task<SharedAppearance?> GetAsync(string vantraId);
    /// <summary>Merge a clean change into the person's row (made on first save) and answer the row as it is now.</summary>
    Task<SharedAppearance> SaveAsync(string vantraId, SharedAppearanceBody clean, string app);
}

public sealed class SqlSharedAppearanceStore(SqlConnectionFactory factory) : ISharedAppearanceStore
{
    public const string ProbeSql = "SELECT CASE WHEN OBJECT_ID(N'vantra.UserAppearance', N'U') IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public const string GetSql = @"
SELECT a.Theme, a.Nav, a.Density, a.ReduceMotion, a.Accent, a.AiRibbon, a.UpdatedAt
FROM vantra.UserAppearance a
WHERE a.VantraId = @VantraId;";

    public const string SaveSql = @"
MERGE vantra.UserAppearance WITH (HOLDLOCK) AS t
USING (SELECT @VantraId AS VantraId) AS s ON t.VantraId = s.VantraId
WHEN MATCHED THEN UPDATE SET
    Theme = COALESCE(@Theme, t.Theme),
    Nav = COALESCE(@Nav, t.Nav),
    Density = COALESCE(@Density, t.Density),
    ReduceMotion = COALESCE(@ReduceMotion, t.ReduceMotion),
    Accent = COALESCE(@Accent, t.Accent),
    AiRibbon = COALESCE(@AiRibbon, t.AiRibbon),
    UpdatedFromApp = @App,
    UpdatedAt = SYSUTCDATETIME()
WHEN NOT MATCHED THEN
    INSERT (VantraId, Theme, Nav, Density, ReduceMotion, Accent, AiRibbon, UpdatedFromApp)
    VALUES (@VantraId, @Theme, @Nav, @Density, @ReduceMotion, @Accent, @AiRibbon, @App);
SELECT a.Theme, a.Nav, a.Density, a.ReduceMotion, a.Accent, a.AiRibbon, a.UpdatedAt
FROM vantra.UserAppearance a
WHERE a.VantraId = @VantraId;";

    /// <summary>The key as the column's type (VARCHAR), so the lookup is a seek.</summary>
    private static DbString Key(string vantraId) => new() { Value = vantraId, IsAnsi = true, Length = SharedAppearanceRules.MaxVantraId };

    public async Task<bool> ProbeAsync()
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<bool>(ProbeSql);
    }

    public async Task<SharedAppearance?> GetAsync(string vantraId)
    {
        using var conn = factory.Vantra();
        return await conn.QuerySingleOrDefaultAsync<SharedAppearance>(GetSql, new { VantraId = Key(vantraId) });
    }

    public async Task<SharedAppearance> SaveAsync(string vantraId, SharedAppearanceBody clean, string app)
    {
        using var conn = factory.Vantra();
        return await conn.QuerySingleAsync<SharedAppearance>(SaveSql, new
        {
            VantraId = Key(vantraId),
            clean.Theme,
            clean.Nav,
            clean.Density,
            clean.ReduceMotion,
            clean.Accent,
            clean.AiRibbon,
            App = app,
        });
    }
}

/// <summary>
/// The endpoints' logic, the same in every app: <c>GET</c> answers <c>{ available, appearance }</c> and <c>PUT</c> merges
/// a change. The probe is cached as the shared workspaces' are: a yes for good, a no for <see cref="NegativeTtl"/>.
/// Before db/shared/003 (or without the grant) GET answers <c>{ available: false }</c> and PUT 404 <c>not_ready</c>, so
/// the SPA keeps the appearance in the browser. Nothing here logs values or account ids.
/// </summary>
public sealed class SharedAppearanceService(ISharedAppearanceStore store, ILogger<SharedAppearanceService> log, TimeProvider? clock = null)
{
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private volatile bool ready;
    private long notReadyUntilTicks;

    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (clock.GetUtcNow().UtcTicks < Interlocked.Read(ref notReadyUntilTicks)) return false;
        bool ok;
        try
        {
            ok = await store.ProbeAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't probe for the shared appearance table; answering without it");
            ok = false;
        }
        if (ok) ready = true;
        else Interlocked.Exchange(ref notReadyUntilTicks, clock.GetUtcNow().Add(NegativeTtl).UtcTicks);
        return ok;
    }

    /// <summary>The status and body for <c>GET me/appearance</c>.</summary>
    public async Task<(int Status, object Body)> GetAsync(string vantraId)
    {
        if (!SharedAppearanceRules.Fits(vantraId) || !await ReadyAsync()) return (200, new SharedAppearanceDto(false, null));
        try
        {
            return (200, new SharedAppearanceDto(true, await store.GetAsync(vantraId)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't read a shared appearance");
            return (503, new { error = "Your appearance couldn't be loaded. Try again.", code = SharedAppearanceRules.UnavailableCode });
        }
    }

    /// <summary>The status and body for <c>PUT me/appearance</c>.</summary>
    public async Task<(int Status, object Body)> SaveAsync(string vantraId, SharedAppearanceBody? body, string app)
    {
        var (clean, problem) = SharedAppearanceRules.Check(body);
        if (problem is not null) return (400, new { error = problem, code = SharedAppearanceRules.InvalidCode });
        if (!SharedAppearanceRules.Fits(vantraId) || !await ReadyAsync())
            return (404, new { available = false, error = "Appearance isn't saved to your account on this server yet.", code = SharedAppearanceRules.NotReadyCode });
        try
        {
            return (200, new SharedAppearanceDto(true, await store.SaveAsync(vantraId, clean!, app)));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't save a shared appearance");
            return (503, new { error = "Your appearance couldn't be saved. It's kept in this browser. Try again.", code = SharedAppearanceRules.UnavailableCode });
        }
    }
}
