using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>The apps Flow connects to, by key.</summary>
public sealed class AppConnectors(OrbitApi orbit, ProjectsApi projects)
{
    public static readonly string[] Apps = ["orbit", "projects"];
    public AppApi? For(string? app) => app switch { "orbit" => orbit, "projects" => projects, _ => null };
    public IEnumerable<AppApi> All => [orbit, projects];
}

/// <summary>
/// A workspace's connections in use: open a sealed key, call the app on it (a 401 marks the connection revoked
/// and pauses the flows that need it), and keep Flow's webhooks in the app in step with the enabled flows — one per
/// Orbit table a flow listens to, one per Projects workspace — removing any no flow needs any more.
/// </summary>
public sealed class Connections(
    ConnectionsRepository repo, FlowsRepository flows, AppConnectors apps, FlowSecrets secrets, IConfiguration config, ILogger<Connections> log)
{
    public string ApiBase => (config["App:ApiBaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow.azurewebsites.net").TrimEnd('/');

    /// <summary>Where an app delivers a subscription's events.</summary>
    public string ReceiverUrl(string app, string subscriptionId) => $"{ApiBase}/api/events/{app}/{subscriptionId}";

    public string? Key(ConnectionRow row) => secrets.Unprotect(row.TokenCipher, row.WorkspaceId, row.Id.ToString("N"));

    public byte[] Seal(string key, Guid workspaceId, Guid connectionId) => secrets.Protect(key, workspaceId, connectionId.ToString("N"));

    /// <summary>The workspace's live connection to <paramref name="app"/> and its key; null when there's none (or it can't be opened).</summary>
    public async Task<(ConnectionRow Row, string Key, AppApi Api)?> OpenAsync(Guid workspaceId, string app)
    {
        var api = apps.For(app);
        if (api is null) return null;
        var row = await repo.LiveAsync(workspaceId, app);
        if (row is null) return null;
        return Key(row) is { } key ? (row, key, api) : null;
    }

    /// <summary>A call on the workspace's connection. Throws <see cref="AppRevokedException"/> (after marking it) and the other app exceptions.</summary>
    public async Task<T> CallAsync<T>(Guid workspaceId, string app, Func<AppApi, string, Task<T>> call)
    {
        var open = await OpenAsync(workspaceId, app)
            ?? throw new AppRefusedException(409, $"{apps.For(app)?.AppName ?? app} isn't connected. Connect it from Connections.", "not_connected");
        try
        {
            var result = await call(open.Api, open.Key);
            await repo.TouchAsync(open.Row.Id, null);
            return result;
        }
        catch (AppRevokedException)
        {
            await LostAsync(open.Row, $"Disconnected in {open.Api.AppName}.");
            throw;
        }
        catch (AppUnavailableException e)
        {
            await repo.TouchAsync(open.Row.Id, e.Message);
            throw;
        }
    }

    /// <summary>The connection is gone: revoked here, and the flows that need the app switched off with the reason.</summary>
    public async Task LostAsync(ConnectionRow row, string reason)
    {
        await repo.RevokeAsync(row.Id, reason);
        foreach (var id in await flows.UsingAppAsync(row.WorkspaceId, row.App))
            await flows.PauseAsync(row.WorkspaceId, id, $"{apps.For(row.App)?.AppName ?? row.App} was disconnected.");
        log.LogInformation("Connection {Connection} ({App}) lost", row.Id, row.App);
    }

    /// <summary>Disconnect from the studio: Flow's webhooks and its key removed in the app (best effort), then revoked here.</summary>
    public async Task DisconnectAsync(ConnectionRow row, CancellationToken ct)
    {
        var api = apps.For(row.App);
        if (api is not null && Key(row) is { } key)
        {
            foreach (var s in await repo.SubscriptionsAsync(row.Id))
                try { await api.UnsubscribeAsync(key, s.RemoteId, ct); }
                catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { }
            try { await api.DisconnectAsync(key, ct); }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { }
        }
        await LostAsync(row, "Disconnected in Vantra Flow.");
    }

    /// <summary>
    /// Makes the app's webhooks match the enabled flows: one subscription per scope in use, none for scopes no flow
    /// needs. Throws when a needed one can't be made (the flow then isn't switched on).
    /// </summary>
    public async Task SyncSubscriptionsAsync(Guid workspaceId, string app, CancellationToken ct)
    {
        var open = await OpenAsync(workspaceId, app);
        if (open is null) return;
        var (row, key, api) = open.Value;
        var wanted = (await flows.ScopesInUseAsync(workspaceId, app)).Select(s => app == "projects" ? "*" : s).ToHashSet(StringComparer.Ordinal);
        var have = await repo.SubscriptionsAsync(row.Id);
        foreach (var s in have.Where(s => !wanted.Contains(s.Scope)))
        {
            try { await api.UnsubscribeAsync(key, s.RemoteId, ct); }
            catch (AppRefusedException) { /* already gone there */ }
            catch (AppUnavailableException e) { log.LogInformation(e, "Couldn't remove subscription {Subscription}; removing it here", s.Id); }
            await repo.DeleteSubscriptionAsync(s.Id);
        }
        foreach (var scope in wanted.Where(w => have.All(s => s.Scope != w)))
        {
            var id = FlowSecrets.NewId("fsb_");
            AppHook hook;
            try { hook = await api.SubscribeAsync(key, scope, ReceiverUrl(app, id), ct); }
            catch (AppRevokedException) { await LostAsync(row, $"Disconnected in {api.AppName}."); throw; }
            await repo.InsertSubscriptionAsync(new SubscriptionRow(id, workspaceId, row.Id, app, scope, hook.Id,
                secrets.Protect(hook.Secret, workspaceId, id), app == "orbit" ? string.Join(",", OrbitApi.Events) : ProjectsApi.Events, DateTime.UtcNow, null));
        }
    }

    public string? SubscriptionSecret(SubscriptionRow s) => secrets.Unprotect(s.SecretCipher, s.WorkspaceId, s.Id);

    /// <summary>Real data to test a flow with, from the app; null when there's none to be had (the catalog's sample is used).</summary>
    public async Task<JsonObject?> SampleAsync(Guid workspaceId, TriggerDef trigger, CancellationToken ct)
    {
        if (trigger.App is not ("orbit" or "projects")) return null;
        try { return await CallAsync(workspaceId, trigger.App, (api, key) => api.SampleAsync(key, trigger, ct)); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { return null; }
    }
}
