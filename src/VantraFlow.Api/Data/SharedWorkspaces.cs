using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>
/// The suite-wide workspace record (stage 1 of shared Vantra workspaces), kept in the shared Vantra identity
/// database: <c>vantra.Workspaces</c>, linked to each app's own workspace by <c>vantra.WorkspaceApps</c>
/// (app + that app's local workspace id). The schema is VantraProjectsAPI's <c>db/shared/001_shared_workspaces.sql</c>;
/// the design is its <c>docs/shared-workspaces.md</c>.
/// </summary>
/// <param name="Id">The shared workspace id: the Projects workspace id where there is one, else the app's own.</param>
/// <param name="Color">'#rrggbb', or null when unset.</param>
/// <param name="Logo">A data:image URL, or null when unset.</param>
/// <param name="TimeZone">An IANA zone, or null when unset.</param>
public sealed record SharedWorkspace(Guid Id, string Name, string? Color, string? Logo, string? TimeZone);

/// <summary>
/// The optional <c>shared</c> block on a workspace in the list: <c>{ id, timeZone? }</c>. Stage 3: the shared time zone
/// rides along, for the apps that keep none of their own.
/// </summary>
public sealed record SharedWorkspaceRef(string Id,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TimeZone = null);

/// <summary>One link row as read: this app's local workspace id and its shared record.</summary>
public sealed class SharedWorkspaceRow
{
    public Guid LocalWorkspaceId { get; set; }
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string? Logo { get; set; }
    public string? TimeZone { get; set; }
}

/// <summary>Where the shared records are read from: the Vantra identity database (<see cref="SqlSharedWorkspaceStore"/>).</summary>
public interface ISharedWorkspaceStore
{
    /// <summary>Whether the shared tables are there (tables and the index the script makes last; never a definition).</summary>
    Task<bool> ProbeAsync();

    /// <summary>The enabled links of <paramref name="app"/> for these local workspace ids, with their shared records.</summary>
    Task<IReadOnlyList<SharedWorkspaceRow>> LoadAsync(string app, IReadOnlyCollection<Guid> localIds);
}

/// <summary>Reads <c>vantra.*</c> on the Vantra connection. Read only: stage 1 never writes there.</summary>
public sealed class SqlSharedWorkspaceStore(SqlConnectionFactory factory) : ISharedWorkspaceStore
{
    /// <summary>
    /// The shared tables exist and the script's last step (<c>UX_WorkspaceApps_Local</c>) is done. OBJECT_ID is null
    /// when the login can't see a table, so a login without SELECT there reads as "not there", not as an error.
    /// </summary>
    public const string ProbeSql = @"
SELECT CASE WHEN OBJECT_ID(N'vantra.Workspaces', N'U') IS NOT NULL
             AND OBJECT_ID(N'vantra.WorkspaceApps', N'U') IS NOT NULL
             AND EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WorkspaceApps_Local' AND object_id = OBJECT_ID(N'vantra.WorkspaceApps'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    public const string LoadSql = @"
SELECT a.LocalWorkspaceId, w.Id, w.Name, w.Color, w.Logo, w.TimeZone
FROM vantra.WorkspaceApps a
INNER JOIN vantra.Workspaces w ON w.Id = a.WorkspaceId
WHERE a.App = @App AND a.Enabled = 1 AND a.LocalWorkspaceId IN @Ids;";

    /// <summary>Ids per query, well under SQL Server's 2,100 parameters.</summary>
    public const int ChunkSize = 500;

    public async Task<bool> ProbeAsync()
    {
        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<bool>(ProbeSql);
    }

    public async Task<IReadOnlyList<SharedWorkspaceRow>> LoadAsync(string app, IReadOnlyCollection<Guid> localIds)
    {
        var rows = new List<SharedWorkspaceRow>();
        using var conn = factory.Vantra();
        foreach (var chunk in localIds.Chunk(ChunkSize))
            rows.AddRange(await conn.QueryAsync<SharedWorkspaceRow>(LoadSql, new { App = app, Ids = chunk }));
        return rows;
    }
}

/// <summary>
/// "The shared record for this app's workspace", or nothing. Nothing when the workspace isn't linked, when its link
/// is switched off, when the shared tables aren't there yet (a cached probe, like <see cref="FlowSchema"/>: a yes
/// for good, a no for <see cref="NegativeTtl"/>), and when the identity database can't be read — so the workspace
/// list never fails or changes because of it. Writes stay in each app in stage 1.
/// </summary>
public sealed class SharedWorkspaces(ISharedWorkspaceStore store, ILogger<SharedWorkspaces> log, TimeProvider? clock = null)
{
    /// <summary>This app's name in <c>vantra.WorkspaceApps.App</c>.</summary>
    public const string App = "flow";

    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    private static readonly IReadOnlyDictionary<Guid, SharedWorkspace> None = new Dictionary<Guid, SharedWorkspace>();

    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    private volatile bool ready;
    private long notReadyUntilTicks; // UTC ticks; Interlocked

    /// <summary>The shared records of these local workspaces, by local id. Missing = not shared (or not readable now).</summary>
    public async Task<IReadOnlyDictionary<Guid, SharedWorkspace>> ForLocalAsync(IEnumerable<Guid> localIds)
    {
        var ids = localIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (ids.Count == 0 || !await ReadyAsync()) return None;
        try
        {
            var rows = await store.LoadAsync(App, ids);
            var map = new Dictionary<Guid, SharedWorkspace>();
            foreach (var r in rows)
                map.TryAdd(r.LocalWorkspaceId, new SharedWorkspace(r.Id, r.Name, Blank(r.Color), Blank(r.Logo), Blank(r.TimeZone)));
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Rolled back (tables dropped), permissions changed, or the database is unreachable: probe again next time.
            ready = false;
            NotReadyFor(NegativeTtl);
            log.LogWarning(ex, "Couldn't read the shared workspace records; answering without them");
            return None;
        }
    }

    /// <summary>The shared record for one local workspace, or null.</summary>
    public async Task<SharedWorkspace?> GetAsync(Guid localId) =>
        (await ForLocalAsync([localId])).GetValueOrDefault(localId);

    /// <summary>The wire block for a workspace, or null (and so left out of the answer).</summary>
    public static SharedWorkspaceRef? Ref(IReadOnlyDictionary<Guid, SharedWorkspace> shared, Guid localId) =>
        shared.TryGetValue(localId, out var s) ? new SharedWorkspaceRef(s.Id.ToString(), s.TimeZone) : null;

    private async Task<bool> ReadyAsync()
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
            log.LogWarning(ex, "Couldn't probe for the shared workspace tables; answering without them");
            ok = false;
        }
        if (ok) ready = true;
        else NotReadyFor(NegativeTtl);
        return ok;
    }

    private void NotReadyFor(TimeSpan span) =>
        Interlocked.Exchange(ref notReadyUntilTicks, clock.GetUtcNow().Add(span).UtcTicks);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
