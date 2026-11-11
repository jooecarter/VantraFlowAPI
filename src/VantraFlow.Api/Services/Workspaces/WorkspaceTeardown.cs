using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.OAuth;

namespace VantraFlow.Api.Services.Workspaces;

/// <summary>What deleting a workspace undoes outside Flow (<see cref="WorkspaceTeardown"/>; a fake in tests).</summary>
public interface IWorkspaceTeardown
{
    /// <summary>Every live connection disconnected, best effort: what was asked of each app and provider, and what didn't answer.</summary>
    Task<TeardownReport> DisconnectAllAsync(Guid workspaceId, CancellationToken ct);
}

/// <summary>How a teardown went: the connections disconnected, and those whose app or provider couldn't be reached.</summary>
public sealed record TeardownReport(int Disconnected, IReadOnlyList<string> Unreachable);

/// <summary>
/// Before a workspace's rows go: its Vantra Orbit and Vantra Projects connections are disconnected the way the studio
/// disconnects them (Flow's webhooks removed in the app, then the key Flow was given revoked there, through their
/// disconnect endpoints), and its Slack, Microsoft 365 and Google connections revoked at the provider where the
/// provider supports it (Slack and Google; Microsoft has no revoke for a single grant: the tokens are deleted here and
/// lapse). Each step is best effort: an app that can't be reached doesn't stop the deletion, and is reported.
/// </summary>
public sealed class WorkspaceTeardown(
    ConnectionsRepository connections, Connections apps, OAuthRepository oauth, OAuthConnections outside, FlowSchema schema, ILogger<WorkspaceTeardown> log)
    : IWorkspaceTeardown
{
    public async Task<TeardownReport> DisconnectAllAsync(Guid workspaceId, CancellationToken ct)
    {
        var done = 0;
        var unreachable = new List<string>();
        foreach (var row in await connections.LiveAsync(workspaceId))
        {
            try { await apps.DisconnectAsync(row, ct); done++; }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogWarning(e, "Deleting workspace {Workspace}: couldn't disconnect {App}", workspaceId, row.App);
                unreachable.Add(row.App);
            }
        }
        if (await schema.ConnectorsReadyAsync())
        {
            foreach (var row in await oauth.LiveAsync(workspaceId))
            {
                try { await outside.DisconnectAsync(row, ct); done++; }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    log.LogWarning(e, "Deleting workspace {Workspace}: couldn't revoke {Provider}", workspaceId, row.Provider);
                    unreachable.Add(row.Provider);
                }
            }
        }
        return new TeardownReport(done, unreachable);
    }
}
