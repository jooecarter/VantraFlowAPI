using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Connections: the apps a workspace is linked to.
/// <code>
/// GET    api/w/{ws}/apps                       → each app, configured or not, and its connection
/// POST   api/w/{ws}/apps/{app}/workspaces      → the app's workspaces you could connect (owner / admin)
/// POST   api/w/{ws}/apps/{app}/connect         { workspaceId } → the connection (owner / admin)
/// DELETE api/w/{ws}/apps/{app}                 → 204: Flow's webhooks and key removed in the app (owner / admin)
/// GET    api/w/{ws}/apps/{app}/schema          → tables (Orbit) / projects (Projects), for the pickers
/// </code>
/// Connecting: Flow signs a two-minute assertion for you (your VantraId, this workspace) that only the app's
/// integration endpoints accept; the app checks you own or administer the workspace you chose there and hands
/// back a key for it, which Flow seals at once (AES-256-GCM, bound to the connection's row) and never shows.
/// </summary>
public sealed class ConnectionsController(WorkspacesRepository workspaces, ConnectionsRepository repo, AppConnectors apps, Connections connections, ILogger<ConnectionsController> log)
    : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/apps")]
    public async Task<ActionResult<IReadOnlyList<AppDto>>> List(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var live = await repo.LiveAsync(workspaceId);
        return Ok(apps.All.Select(a => new AppDto(a.App, a.AppName, a.Configured,
            live.FirstOrDefault(c => c.App == a.App) is { } c ? Dto(c, a) : null)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/apps/{app}/workspaces")]
    public async Task<IActionResult> Choices(Guid workspaceId, string app, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (apps.For(app) is not { } api) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        try { return Ok(await api.WorkspacesAsync(api.Assertion(Sub, DisplayName, workspaceId), ct)); }
        catch (AppUnavailableException e) { return StatusCode(502, new { error = e.Message, code = "app_unavailable" }); }
        catch (AppRefusedException e) { return StatusCode(e.Status is 401 or 403 ? 403 : 400, new { error = e.Message, code = e.Code }); }
    }

    [HttpPost("api/w/{workspaceId:guid}/apps/{app}/connect")]
    public async Task<IActionResult> Connect(Guid workspaceId, string app, [FromBody] ConnectBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (apps.For(app) is not { } api) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        if (body.WorkspaceId is not { } remote || remote == Guid.Empty) return BadRequest(new { error = "Choose a workspace." });
        var flowWs = (await Workspaces.GetAsync(workspaceId))!;
        AppConnectResult result;
        try { result = await api.ConnectAsync(api.Assertion(Sub, DisplayName, workspaceId), remote, flowWs.Name, ct); }
        catch (AppUnavailableException e) { return StatusCode(502, new { error = e.Message, code = "app_unavailable" }); }
        catch (AppRefusedException e) { return StatusCode(e.Status is 401 or 403 ? 403 : e.Status == 402 ? 402 : 400, new { error = e.Message, code = e.Code }); }

        // A connection made before is replaced; its webhooks and key are removed in the app first.
        if (await repo.LiveAsync(workspaceId, app) is { } old) await connections.DisconnectAsync(old, ct);
        var id = Guid.NewGuid();
        var row = new ConnectionRow(id, workspaceId, app, result.WorkspaceId, result.WorkspaceName, result.KeyId,
            connections.Seal(result.Key, workspaceId, id), "active", null, Sub, DisplayName, DateTime.UtcNow, null);
        await repo.InsertAsync(row);
        log.LogInformation("Workspace {Workspace} connected to {App} workspace {Remote}", workspaceId, app, result.WorkspaceId);
        return Ok(Dto(row, api));
    }

    [HttpDelete("api/w/{workspaceId:guid}/apps/{app}")]
    public async Task<IActionResult> Disconnect(Guid workspaceId, string app, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await repo.LiveAsync(workspaceId, app) is not { } row) return NoContent();
        await connections.DisconnectAsync(row, ct);
        return NoContent();
    }

    [HttpGet("api/w/{workspaceId:guid}/apps/{app}/schema")]
    public async Task<IActionResult> Schema(Guid workspaceId, string app, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (apps.For(app) is null) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        try { return Ok(await connections.CallAsync(workspaceId, app, (api, key) => api.SchemaAsync(key, ct))); }
        catch (AppUnavailableException e) { return StatusCode(502, new { error = e.Message, code = "app_unavailable" }); }
        catch (AppRevokedException e) { return StatusCode(409, new { error = e.Message, code = "connection_revoked" }); }
        catch (AppRefusedException e) { return StatusCode(e.Code == "not_connected" ? 409 : 400, new { error = e.Message, code = e.Code }); }
    }

    private static ConnectionDto Dto(ConnectionRow c, AppApi api) =>
        new(c.Id.ToString("N"), c.App, c.RemoteWorkspaceId, c.RemoteWorkspaceName, c.Status, c.LastError, c.ConnectedByName,
            Iso(c.ConnectedAt), Iso(c.LastUsedAt), api.AppUrl);
}
