using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>What the app-facing endpoints read (the repositories in production; a fake in tests).</summary>
public interface IAppLinkStore
{
    /// <summary>Live connections, in any Flow workspace, to this app workspace.</summary>
    Task<IReadOnlyList<ConnectionRow>> LinkingAsync(string app, Guid appWorkspaceId);
    Task<string?> RoleAsync(Guid workspaceId, string vantraId);
    Task<IReadOnlyList<FlowRow>> ButtonFlowsAsync(IReadOnlyCollection<Guid> workspaceIds, string app);
    Task<FlowRow?> FlowAsync(Guid workspaceId, string flowId);
}

public sealed class SqlAppLinkStore(ConnectionsRepository connections, WorkspacesRepository workspaces, FlowsRepository flows) : IAppLinkStore
{
    public Task<IReadOnlyList<ConnectionRow>> LinkingAsync(string app, Guid appWorkspaceId) => connections.LinkingAsync(app, appWorkspaceId);
    public Task<string?> RoleAsync(Guid workspaceId, string vantraId) => workspaces.RoleAsync(workspaceId, vantraId);
    public Task<IReadOnlyList<FlowRow>> ButtonFlowsAsync(IReadOnlyCollection<Guid> workspaceIds, string app) => flows.ByTriggerAsync(workspaceIds, app, ButtonRules.Event);
    public Task<FlowRow?> FlowAsync(Guid workspaceId, string flowId) => flows.GetAsync(workspaceId, flowId);
}

/// <summary>A button flow as an app's menu lists it.</summary>
public sealed record ButtonFlowDto(string Id, string Name, string Label, string Subtitle, Guid WorkspaceId);

/// <summary>
/// What an app may ask Flow for someone, once their assertion has checked out (<see cref="AppAssertion"/>): only through
/// a Flow workspace that links that app workspace (a live connection to it) and that they're a member of. A run also needs
/// the flow to be on, its trigger to be the app's <c>button.record</c>, and its table (Orbit) or kind (Projects) to be the
/// record's. The app itself checks first that the person can see the record.
/// </summary>
public sealed class AppLinks(IAppLinkStore store)
{
    /// <summary>The Flow workspaces that link the caller's app workspace and have them as a member.</summary>
    public async Task<IReadOnlyList<Guid>> WorkspacesAsync(AppCaller c)
    {
        var list = new List<Guid>();
        foreach (var link in await store.LinkingAsync(c.App, c.AppWorkspaceId))
            if (!list.Contains(link.WorkspaceId) && await store.RoleAsync(link.WorkspaceId, c.VantraId) is not null) list.Add(link.WorkspaceId);
        return list;
    }

    /// <summary>The button flows for a table (Orbit) or tasks (Projects) the caller may run.</summary>
    public async Task<IReadOnlyList<ButtonFlowDto>> ButtonsAsync(AppCaller c, string target)
    {
        var mine = await WorkspacesAsync(c);
        if (mine.Count == 0) return [];
        var list = new List<ButtonFlowDto>();
        foreach (var f in await store.ButtonFlowsAsync(mine, c.App))
        {
            if (FlowDefinition.Parse(f.Definition) is not { } def || !ButtonRules.IsButton(def.Trigger) || def.Trigger.App != c.App) continue;
            if (!string.Equals(ButtonRules.Target(def.Trigger), target, StringComparison.Ordinal)) continue;
            list.Add(new ButtonFlowDto(f.Id, f.Name, ButtonRules.Label(def.Trigger, f.Name), ButtonRules.Subtitle(def), f.WorkspaceId));
        }
        return list;
    }

    /// <summary>The flow to run, or why not: <c>not_linked</c>, <c>no_flow</c>, <c>off</c>, <c>not_button</c> or <c>wrong_target</c>.</summary>
    public async Task<(FlowRow? Flow, string? Code)> RunnableAsync(AppCaller c, string flowId, string target)
    {
        if (string.IsNullOrWhiteSpace(flowId) || flowId.Length > 40) return (null, "no_flow");
        var mine = await WorkspacesAsync(c);
        if (mine.Count == 0) return (null, "not_linked");
        foreach (var ws in mine)
        {
            if (await store.FlowAsync(ws, flowId) is not { } f) continue;
            if (!f.Enabled) return (null, "off");
            if (FlowDefinition.Parse(f.Definition) is not { } def || !ButtonRules.IsButton(def.Trigger) || def.Trigger.App != c.App) return (null, "not_button");
            if (!string.Equals(ButtonRules.Target(def.Trigger), target, StringComparison.Ordinal)) return (null, "wrong_target");
            return (f, null);
        }
        return (null, "no_flow");
    }

    public static string Message(string code) => code switch
    {
        "not_linked" => "This workspace isn't linked to a Vantra Flow workspace you're in.",
        "off" => "That flow is switched off.",
        "not_button" or "wrong_target" => "That flow doesn't run from here.",
        _ => "That flow isn't there any more.",
    };
}
