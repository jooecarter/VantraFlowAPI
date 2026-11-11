using System.Data;
using Dapper;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Shared;

/// <summary>
/// Flow's own member list (<c>flow.WorkspaceMembers</c>) kept in line with a shared workspace's: everyone with Flow
/// access, owner → owner, admin → admin, member → member. Guests get no Flow. The main owner is
/// <c>flow.Workspaces.OwnerVantraId</c>. Flow has no seats, so nobody is ever refused.
/// </summary>
public sealed class FlowSharedMirror(SqlConnectionFactory factory) : ISharedMirror
{
    public string App => SharedApps.Flow;

    /// <summary>The Flow role for a shared role; null for a guest (no Flow).</summary>
    public static string? LocalRole(string sharedRole) => sharedRole switch
    {
        SharedRoles.Owner => FlowRoles.Owner,
        SharedRoles.Admin => FlowRoles.Admin,
        SharedRoles.Member => FlowRoles.Member,
        _ => null,
    };

    public static IReadOnlyList<(string VantraId, string Role)> Desired(SharedState s) =>
        s.Members.Where(m => m.Has(SharedApps.Flow))
            .Select(m => (m.VantraId, Role: LocalRole(m.Role)))
            .Where(m => m.Role is not null)
            .Select(m => (m.VantraId, m.Role!)).ToList();

    public async Task<IReadOnlyList<string>> ApplyAsync(Guid localId, SharedState state, string? by)
    {
        var desired = Desired(state);
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted);
        var owner = await conn.ExecuteScalarAsync<string?>(
            "SELECT OwnerVantraId FROM flow.Workspaces WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id;", new { Id = localId }, tx);
        if (owner is null) { tx.Rollback(); return []; }
        var local = (await conn.QueryAsync<(string VantraId, string Role)>(
            "SELECT VantraId, Role FROM flow.WorkspaceMembers WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @Id;", new { Id = localId }, tx)).ToList();
        var diff = MirrorDiff.Compute(local, desired);
        var primary = desired.First(d => state.IsPrimary(d.VantraId)).VantraId;
        if (diff.Empty && string.Equals(owner, primary, StringComparison.OrdinalIgnoreCase)) { tx.Rollback(); return []; }

        if (diff.Adds.Count > 0)
        {
            var people = await PeopleAsync(diff.Adds.Select(a => a.VantraId));
            foreach (var (id, role) in diff.Adds)
            {
                var p = people.GetValueOrDefault(id);
                await conn.ExecuteAsync(@"
INSERT INTO flow.WorkspaceMembers (WorkspaceId, VantraId, Role, Email, Name, AddedBy)
SELECT @Id, @VantraId, @Role, @Email, @Name, @By
WHERE NOT EXISTS (SELECT 1 FROM flow.WorkspaceMembers WHERE WorkspaceId = @Id AND VantraId = @VantraId);",
                    new { Id = localId, VantraId = id, Role = role, Email = p?.Email ?? "", Name = p?.Name, By = by ?? primary }, tx);
            }
        }
        foreach (var (id, role) in diff.Changes)
            await conn.ExecuteAsync("UPDATE flow.WorkspaceMembers SET Role = @Role WHERE WorkspaceId = @Id AND VantraId = @VantraId;",
                new { Id = localId, VantraId = id, Role = role }, tx);
        if (diff.Removals.Count > 0)
            await conn.ExecuteAsync("DELETE FROM flow.WorkspaceMembers WHERE WorkspaceId = @Id AND VantraId IN @Ids;",
                new { Id = localId, Ids = diff.Removals }, tx);
        if (!string.Equals(owner, primary, StringComparison.Ordinal))
            await conn.ExecuteAsync("UPDATE flow.Workspaces SET OwnerVantraId = @Owner, UpdatedAt = SYSUTCDATETIME() WHERE Id = @Id;",
                new { Id = localId, Owner = primary }, tx);
        tx.Commit();
        return [];
    }

    public async Task<IReadOnlyList<Guid>> MembershipsAsync(string vantraId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<Guid>("SELECT WorkspaceId FROM flow.WorkspaceMembers WHERE VantraId = @VantraId;",
            new { VantraId = vantraId })).ToList();
    }

    private async Task<Dictionary<string, SharedPerson>> PeopleAsync(IEnumerable<string> ids)
    {
        using var conn = factory.Vantra();
        var rows = await conn.QueryAsync<(string VantraID, string? FirstName, string? LastName, string Email)>(
            "SELECT VantraID, FirstName, LastName, Email FROM dbo.UserDetails WHERE VantraID IN @Ids;", new { Ids = ids.Distinct().ToList() });
        return rows.GroupBy(r => r.VantraID, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var r = g.First();
                var name = $"{r.FirstName} {r.LastName}".Trim();
                return new SharedPerson(name.Length > 0 ? name : r.Email, r.Email);
            }, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>Flow's side of the shared Members page: names, its email and link, and Projects' seats asked of Projects.</summary>
public sealed class FlowSharedHost(UsersRepository users, IWorkspaceMail mail, ProjectsApi projects, ILogger<FlowSharedHost> log) : ISharedHost
{
    public const string SharedPrefix = "/api/integrations/shared/v1";

    public string App => SharedApps.Flow;

    public async Task<IReadOnlyDictionary<string, SharedPerson>> PeopleAsync(IReadOnlyCollection<string> vantraIds) =>
        (await users.FindByVantraIdsAsync(vantraIds))
            .Where(u => !string.IsNullOrWhiteSpace(u.VantraID))
            .GroupBy(u => u.VantraID!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g =>
            {
                var u = g.First();
                var name = $"{u.FirstName} {u.LastName}".Trim();
                return new SharedPerson(name.Length > 0 ? name : u.Email, u.Email);
            }, StringComparer.OrdinalIgnoreCase);

    public Task<IReadOnlyList<string>> AccountsForEmailAsync(string email) => users.FindVantraIdsByEmailAsync(email);

    public Task<SharedResult?> SeatAsync(SharedState s, string role, string? guestType) => Task.FromResult<SharedResult?>(null);

    public async Task<SharedResult> RemoteAccessAsync(SharedState s, SharedActor actor, string target, bool grant)
    {
        var r = await CallAsync(s, actor, "/access", new { workspaceId = s.WorkspaceId, vantraId = target, grant });
        return r ?? SharedResult.NoContent;
    }

    public Task<SharedResult?> RemoteSeatAsync(SharedState s, SharedActor actor, string role, string? guestType) =>
        CallAsync(s, actor, "/seat", new { workspaceId = s.WorkspaceId, role, guestType });

    public string InviteLink(string? origin, string token) => mail.InviteLink(origin, token);

    public Task<bool> SendInviteAsync(SharedState s, SharedInviteRow invite, string link, SharedActor actor) =>
        mail.CanSend
            ? mail.SendInviteAsync(invite.Email, s.Name, actor.Name, actor.Email, invite.Role == SharedRoles.Admin ? FlowRoles.Admin : FlowRoles.Member, link)
            : Task.FromResult(false);

    /// <summary>Asks Vantra Projects, as the person acting, with Flow's two-minute assertion. Null when it said yes.</summary>
    private async Task<SharedResult?> CallAsync(SharedState s, SharedActor actor, string path, object body)
    {
        var local = s.LocalId(App) ?? Guid.Empty;
        try
        {
            await projects.SharedAsync(projects.Assertion(actor.VantraId, actor.Name, local), SharedPrefix + path, body, CancellationToken.None);
            return null;
        }
        catch (AppRefusedException e) when (e.Status != StatusCodes.Status404NotFound || e.Code is not null)
        {
            return new SharedResult(e.Status, new { error = e.Message, code = e.Code });
        }
        catch (Exception e) when (e is AppRefusedException or AppUnavailableException or AppRevokedException)
        {
            log.LogWarning("Vantra Projects couldn't be asked about a shared workspace's seats: {Reason}", e.Message);
            return SharedResult.Error(SharedCodes.Unavailable, "Vantra Projects couldn't be reached. Nothing was changed. Try again.");
        }
    }
}
