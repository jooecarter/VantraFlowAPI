using VantraFlow.Api.Shared;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>A workspace in Orbit or Projects you could connect, with its shared logo and colour when Flow knows them.</summary>
public sealed record AppWorkspaceView(Guid Id, string Name, string Role, bool CanConnect, string? Color, string? Logo);

/// <summary>
/// Orbit and Projects list their workspaces by name and role only. Every workspace is a shared Vantra workspace now,
/// so Flow fills in its logo and colour from the shared record you're a member of: the one that links this app's
/// workspace, or else the one with the same id. A workspace with no shared record keeps neither.
/// </summary>
public static class AppWorkspaceLooks
{
    public static IReadOnlyList<AppWorkspaceView> Apply(string app, IReadOnlyList<AppWorkspace> list, IReadOnlyList<SharedPlace> places) =>
        list.Select(w =>
        {
            var place = places.FirstOrDefault(p => p.Links.Any(l => l.App == app && l.LocalWorkspaceId == w.Id))
                        ?? places.FirstOrDefault(p => p.Id == w.Id);
            return new AppWorkspaceView(w.Id, w.Name, w.Role, w.CanConnect,
                place is null ? null : SharedAppRules.CleanColor(place.Look.Color),
                place is null ? null : SharedAppRules.Shown(place.Look.Logo));
        }).ToList();
}
