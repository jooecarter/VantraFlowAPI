using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Services.Engine;

/// <summary>What switching a flow on or off came to: the flow as it is now, or why it couldn't be.</summary>
public sealed record SwitchOutcome(FlowRow? Flow, IReadOnlyList<DefinitionError>? Errors = null, Billing.PlanBlock? Plan = null, string? SubscribeFailed = null);

/// <summary>
/// Switching a flow on or off, the same from the studio and from the API: before it goes on, its definition is checked,
/// the apps it needs must be connected and the plan must cover it; once on, Slack joins its channel and the apps'
/// webhooks follow its trigger (a failure there switches it back off).
/// </summary>
public sealed class FlowSwitch(
    FlowsRepository flows, WorkspacesRepository workspaces, Connections connections, FlowSchema schema, OAuthRepository oauth,
    OAuth.OAuthProviders providers, OAuth.OAuthActions oauthActions, Billing.Plans plans, IConfiguration config)
{
    public async Task<SwitchOutcome> SetAsync(FlowRow f, bool enabled, string by, CancellationToken ct)
    {
        var (workspaceId, id) = (f.WorkspaceId, f.Id);
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        if (enabled)
        {
            var errors = DefinitionRules.Validate(def, FlowLimits.From(config)).ToList();
            foreach (var app in FlowCatalog.AppsNeeded(def))
                if (await connections.OpenAsync(workspaceId, app) is null)
                    errors.Add(new DefinitionError(null, $"Connect {(app == "orbit" ? "Vantra Orbit" : "Vantra Projects")} first."));
            var outside = FlowCatalog.ProvidersNeeded(def);
            if (outside.Count > 0 && !await schema.ConnectorsReadyAsync())
                errors.Add(new DefinitionError(null, "Slack, Microsoft 365 and Google arrive with the next server update."));
            else
                foreach (var p in outside)
                    if (await oauth.LiveAsync(workspaceId, p) is null)
                        errors.Add(new DefinitionError(null, $"Connect {providers.For(p)?.Name ?? p} first."));
            if (def.AllSteps().Any(x => x.Type == StepTypes.Approval) && !await schema.StepsReadyAsync())
                errors.Add(new DefinitionError(null, "Approvals arrive with the next server update."));
            if (errors.Count > 0) return new SwitchOutcome(null, Errors: errors);
            // The plan: its active-flow limit, and premium apps and steps.
            if (await workspaces.GetAsync(workspaceId) is { } wsRow && await plans.CanEnableAsync(wsRow, id, def, DateTime.UtcNow) is { } block)
                return new SwitchOutcome(null, Plan: block);
        }
        await flows.SetEnabledAsync(workspaceId, id, enabled, by);
        if (enabled && def.Trigger.App == "slack" && Tokens.Text(def.Trigger.Config["channel"]) is { Length: > 0 } channel && await schema.ConnectorsReadyAsync())
            await oauthActions.JoinSlackChannelAsync(workspaceId, channel, ct);
        try
        {
            if (f.TriggerApp is "orbit" or "projects") await connections.SyncSubscriptionsAsync(workspaceId, f.TriggerApp, ct);
        }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
        {
            if (enabled)
            {
                await flows.SetEnabledAsync(workspaceId, id, false, by);
                return new SwitchOutcome(null, SubscribeFailed: e.Message);
            }
        }
        return new SwitchOutcome(await flows.GetAsync(workspaceId, id));
    }
}
