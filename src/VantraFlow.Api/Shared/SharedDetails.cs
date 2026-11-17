using Dapper;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Shared;

// A shared workspace's cover and description (vantra.Workspaces, VantraProjectsAPI db/shared/003), so Orbit and Flow
// show the same. The cover is a photo (a data URL) or a preset gradient, never both, checked as Orbit's
// CoverValidator does. DetailsUpdatedAt is null until an app sets them: Orbit then fills them in from its own copy,
// once. Until 003 has run nothing here is read or written. The same file is in Orbit's and Flow's APIs (only the
// namespace differs).

/// <summary>A shared workspace's cover and description. <see cref="Set"/> is false until an app has written them.</summary>
public sealed record SharedDetails(Guid Id, string? Description, string? Cover, string? CoverPreset, DateTime? DetailsUpdatedAt)
{
    public bool Set => DetailsUpdatedAt is not null;
}

/// <summary>
/// A change: null leaves a field as it is, <c>""</c> clears it. Setting the photo clears the preset and vice versa.
/// </summary>
public sealed record SharedDetailsChange(string? Description = null, string? Cover = null, string? CoverPreset = null)
{
    public bool Empty => Description is null && Cover is null && CoverPreset is null;
}

public static class SharedDetailsRules
{
    public const int DescriptionMax = 500;
    public const int CoverMax = 400_000;
    public const int PresetMax = 400;

    private static readonly string[] PresetStarts = ["linear-gradient(", "radial-gradient("];
    private static readonly string[] PresetForbidden = ["url(", "expression", ";", "{", "}", "<", ">", "\\", "\""];

    /// <summary>Why a change can't be saved (the description, then the cover), or null.</summary>
    public static string? Problem(SharedDetailsChange change, Func<string, string?> imageProblem)
    {
        if (change.Description is { } d && d.Trim().Length > DescriptionMax) return $"The description is too long ({DescriptionMax} characters at most).";
        if (!string.IsNullOrEmpty(change.Cover) && !string.IsNullOrWhiteSpace(change.CoverPreset)) return "Choose a cover image or a preset, not both.";
        if (!string.IsNullOrEmpty(change.Cover) && imageProblem(change.Cover) is { } bad) return bad;
        if (!string.IsNullOrWhiteSpace(change.CoverPreset) && PresetProblem(change.CoverPreset.Trim()) is { } badPreset) return badPreset;
        return null;
    }

    /// <summary>Orbit's CoverValidator.PresetProblem: a CSS gradient that can't fetch, script or break out.</summary>
    public static string? PresetProblem(string preset)
    {
        if (preset.Length > PresetMax) return $"The cover preset is too long ({PresetMax} characters at most).";
        if (!PresetStarts.Any(p => preset.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            return "The cover preset must be a CSS linear-gradient(…) or radial-gradient(…).";
        if (PresetForbidden.Any(f => preset.Contains(f, StringComparison.OrdinalIgnoreCase)))
            return "The cover preset contains characters or functions that aren't allowed.";
        return null;
    }
}

public interface ISharedDetailsStore
{
    /// <summary>db/shared/003's last column is there (COL_LENGTH: null without it, or without the grant).</summary>
    Task<bool> ProbeAsync();
    Task<IReadOnlyList<SharedDetails>> GetAsync(IReadOnlyCollection<Guid> sharedIds);
    /// <summary>Apply a change and stamp it; with <paramref name="onlyIfUnset"/> only while no app has set them. False when nothing was written.</summary>
    Task<bool> SetAsync(Guid sharedId, SharedDetailsChange change, bool onlyIfUnset);
}

public sealed class SqlSharedDetailsStore(SqlConnectionFactory factory) : ISharedDetailsStore
{
    public const string ProbeSql = "SELECT CASE WHEN COL_LENGTH(N'vantra.Workspaces', N'DetailsUpdatedAt') IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public const string GetSql = @"
SELECT w.Id, w.Description, w.Cover, w.CoverPreset, w.DetailsUpdatedAt
FROM vantra.Workspaces w
WHERE w.Id IN @Ids;";

    public const string SetSql = @"
UPDATE vantra.Workspaces SET
    Description = CASE WHEN @SetDescription = 1 THEN @Description ELSE Description END,
    Cover = CASE WHEN @SetCover = 1 THEN @Cover ELSE Cover END,
    CoverPreset = CASE WHEN @SetCoverPreset = 1 THEN @CoverPreset ELSE CoverPreset END,
    DetailsUpdatedAt = SYSUTCDATETIME()
WHERE Id = @Id AND (@OnlyIfUnset = 0 OR DetailsUpdatedAt IS NULL);";

    public async Task<bool> ProbeAsync()
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<bool>(ProbeSql);
    }

    public async Task<IReadOnlyList<SharedDetails>> GetAsync(IReadOnlyCollection<Guid> sharedIds)
    {
        if (sharedIds.Count == 0) return [];
        var rows = new List<SharedDetails>();
        using var conn = factory.Vantra();
        foreach (var chunk in sharedIds.Distinct().Chunk(500))
            rows.AddRange(await conn.QueryAsync<SharedDetails>(GetSql, new { Ids = chunk }));
        return rows;
    }

    public async Task<bool> SetAsync(Guid sharedId, SharedDetailsChange change, bool onlyIfUnset)
    {
        var (setCover, cover, setPreset, preset) = CoverColumns(change);
        using var conn = factory.Vantra();
        return await conn.ExecuteAsync(SetSql, new
        {
            Id = sharedId,
            SetDescription = change.Description is not null,
            Description = string.IsNullOrWhiteSpace(change.Description) ? null : change.Description.Trim(),
            SetCover = setCover,
            Cover = cover,
            SetCoverPreset = setPreset,
            CoverPreset = preset,
            OnlyIfUnset = onlyIfUnset,
        }) > 0;
    }

    /// <summary>Orbit's WorkspaceChanges.ForCover: setting one clears the other; <c>""</c> clears that one.</summary>
    public static (bool SetCover, string? Cover, bool SetPreset, string? Preset) CoverColumns(SharedDetailsChange c)
    {
        if (!string.IsNullOrEmpty(c.Cover)) return (true, c.Cover, true, null);
        if (!string.IsNullOrWhiteSpace(c.CoverPreset)) return (true, null, true, c.CoverPreset.Trim());
        return (c.Cover is not null, null, c.CoverPreset is not null, null);
    }
}

/// <summary>
/// Readiness (a yes for good, a no for <see cref="NegativeTtl"/>) and guarded reads: a read that fails answers nothing,
/// so a page never breaks because of the identity database. Writes throw <see cref="SharedUnavailableException"/>.
/// </summary>
public sealed class SharedDetailsService(ISharedDetailsStore store, ILogger<SharedDetailsService> log, TimeProvider? clock = null)
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
            log.LogWarning(ex, "Couldn't probe for the shared workspace cover columns; answering without them");
            ok = false;
        }
        if (ok) ready = true;
        else Interlocked.Exchange(ref notReadyUntilTicks, clock.GetUtcNow().Add(NegativeTtl).UtcTicks);
        return ok;
    }

    /// <summary>The details of these shared workspaces, by shared id; empty before 003 or on an error.</summary>
    public async Task<IReadOnlyDictionary<Guid, SharedDetails>> GetAsync(IReadOnlyCollection<Guid> sharedIds)
    {
        if (sharedIds.Count == 0 || !await ReadyAsync()) return new Dictionary<Guid, SharedDetails>();
        try
        {
            return (await store.GetAsync(sharedIds)).GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning(ex, "Couldn't read shared workspace covers; answering without them");
            return new Dictionary<Guid, SharedDetails>();
        }
    }

    public async Task<bool> SetAsync(Guid sharedId, SharedDetailsChange change, bool onlyIfUnset = false)
    {
        if (change.Empty) return false;
        try
        {
            return await store.SetAsync(sharedId, change, onlyIfUnset);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SharedUnavailableException("The shared workspace's cover couldn't be saved.", ex);
        }
    }
}
