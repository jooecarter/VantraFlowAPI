using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Shared;

/// <summary>
/// Flow's side of stage 3 (one workspace everywhere): what a Flow workspace holds when it becomes shared, and its copy
/// of the shared name, time zone, colour and logo.
/// </summary>
public sealed class FlowSpaces(IWorkspaceStore workspaces, IWorkspaceFeatures features, SharedSpaces spaces)
{
    public const string App = SharedApps.Flow;

    /// <summary>
    /// The two empty automatic "My workspace"s made before stage 3. They're retired (a guarded script deletes them while
    /// they're still empty), so they're never made shared.
    /// </summary>
    public static readonly IReadOnlySet<Guid> Retired = new HashSet<Guid>
    {
        Guid.Parse("B25498CA-A9BB-4AD3-BD8C-559C639B82E8"),
        Guid.Parse("2CEB40CB-FB3F-4BFD-96AA-89AC2B3248A7"),
    };

    public SharedSpaces Spaces => spaces;

    /// <summary>A Flow workspace as a shared one: its look, its owner, and its members (owner / admin / member).</summary>
    public async Task<SharedSeed?> SeedAsync(Guid localId)
    {
        if (Retired.Contains(localId)) return null;
        var w = await workspaces.GetAsync(localId);
        if (w is null || string.IsNullOrWhiteSpace(w.OwnerVantraId)) return null;
        var look = await features.InvitesReadyAsync() ? (await workspaces.LooksAsync([localId])).GetValueOrDefault(localId) : null;
        var members = (await workspaces.MembersAsync(localId))
            .Select(m => new SharedChange(m.VantraId, m.Role switch { FlowRoles.Owner => SharedRoles.Owner, FlowRoles.Admin => SharedRoles.Admin, _ => SharedRoles.Member }, null))
            .ToList();
        return new SharedSeed(localId, new SharedLook(w.Name, SharedAppRules.CleanColor(look?.Color), SharedAppRules.CleanLogo(look?.Logo), w.TimeZone), w.OwnerVantraId, members);
    }

    /// <summary>Bring Flow's copy in line with the shared look (the source of truth); answers the row as it is afterwards.</summary>
    public async Task<WorkspaceRow> MirrorAsync(WorkspaceRow w, Guid sharedId, SharedLook look)
    {
        var name = WorkspaceRules.CleanName(look.Name.Length > 80 ? look.Name[..80] : look.Name) ?? w.Name;
        var tz = look.TimeZone is { Length: > 0 } z && Valid(z) ? z : w.TimeZone;
        if (!string.Equals(name, w.Name, StringComparison.Ordinal) || !string.Equals(tz, w.TimeZone, StringComparison.Ordinal))
        {
            await workspaces.UpdateAsync(w.Id, name, tz);
            w = w with { Name = name, TimeZone = tz };
        }
        if (await features.InvitesReadyAsync())
        {
            var have = (await workspaces.LooksAsync([w.Id])).GetValueOrDefault(w.Id);
            var setColor = !string.Equals(SharedAppRules.CleanColor(have?.Color), look.Color, StringComparison.Ordinal);
            // A shared logo never set: Flow keeps its own (and fills the shared one in). Removed: Flow's goes too.
            var mine = string.IsNullOrEmpty(have?.Logo) ? null : have.Logo;
            var want = SharedAppRules.IsNoLogo(look.Logo) ? null : look.Logo;
            if (look.Logo is null && mine is not null) await spaces.FillLogoAsync(sharedId, mine);
            var setLogo = look.Logo is not null && !string.Equals(mine, want, StringComparison.Ordinal);
            if (setColor || setLogo) await workspaces.SetLookAsync(w.Id, setColor, look.Color, setLogo, want);
        }
        return w;
    }

    private static bool Valid(string id)
    {
        try { TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return false; }
    }
}
