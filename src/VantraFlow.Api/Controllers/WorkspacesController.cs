using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Flow workspaces as a whole. Members and invitations are <see cref="MembersController"/>.
/// <code>
/// GET    api/workspaces                         → yours (a first one is made on first visit)
/// POST   api/workspaces                         { name, timeZone, color?, logo? } → 201 (409 workspace_limit)
/// PATCH  api/w/{ws}                             { name?, timeZone?, color?, logo? } (owner / admin; the look: db/005, 503 before)
/// PUT    api/w/{ws}/alerts                      { on } the failure-alert default (owner / admin; db/002, 503 before)
/// POST   api/w/{ws}/leave                       → 204 (409 last_owner)
/// POST   api/w/{ws}/transfer                    { vantraId } → 204: they become an owner, you an admin (owners)
/// DELETE api/w/{ws}                             { confirm: its name } → 200 (owners): see <see cref="Delete"/>
/// </code>
/// </summary>
public sealed class WorkspacesController(
    IWorkspaceStore workspaces, IConfiguration config, IWorkspaceAlerts alerts, IWorkspaceFeatures features, IWorkspaceTeardown teardown,
    ILogger<WorkspacesController> log, SharedWorkspaces? shared = null)
    : FlowControllerBase(workspaces)
{
    private const string NameError = "Give the workspace a name of 2–80 characters.";

    [HttpGet("api/workspaces")]
    public async Task<ActionResult<IReadOnlyList<WorkspaceDto>>> List()
    {
        var mine = await Workspaces.ForUserAsync(Sub);
        if (mine.Count == 0)
        {
            var first = await Workspaces.CreateAsync("My workspace", "UTC", Sub, Email, DisplayName);
            mine = [(first, FlowRoles.Owner)];
        }
        return Ok(await DtosAsync(mine));
    }

    [HttpPost("api/workspaces")]
    public async Task<ActionResult<WorkspaceDto>> Create([FromBody] CreateWorkspaceBody? body)
    {
        var name = WorkspaceRules.CleanName(body?.Name);
        if (name is null) return BadRequest(new { error = NameError, code = "invalid_name" });
        var tz = ValidZone(body!.TimeZone) ?? "UTC";
        if (LookProblem(body.Color, body.Logo) is { } bad) return bad;
        var max = WorkspaceRules.MaxOwned(config);
        if (await Workspaces.OwnedCountAsync(Sub) >= max)
            return Refuse(WorkspaceCodes.WorkspaceLimit, $"You own {max} workspaces, the most one person can. Delete or hand one over to make another.");
        // Before db/005 there's nowhere to keep the look: the workspace is made without it.
        WorkspaceLook? look = null;
        if (await features.InvitesReadyAsync())
            look = new WorkspaceLook(Guid.Empty, WorkspaceRules.CleanColor(body.Color), string.IsNullOrEmpty(body.Logo) ? null : body.Logo);
        var w = await Workspaces.CreateAsync(name, tz, Sub, Email, DisplayName, look);
        return StatusCode(201, (await DtosAsync([(w, FlowRoles.Owner)]))[0]);
    }

    [HttpPatch("api/w/{workspaceId:guid}")]
    public async Task<ActionResult<WorkspaceDto>> Update(Guid workspaceId, [FromBody] UpdateWorkspaceBody? body)
    {
        var (role, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        var w = (await Workspaces.GetAsync(workspaceId))!;
        string? name = w.Name;
        if (body?.Name is not null && (name = WorkspaceRules.CleanName(body.Name)) is null) return BadRequest(new { error = NameError, code = "invalid_name" });
        var tz = body?.TimeZone is null ? w.TimeZone : ValidZone(body.TimeZone) ?? "";
        if (tz.Length == 0) return BadRequest(new { error = "That isn't a time zone we know." });
        var setColor = body?.Color is not null;
        var setLogo = body?.Logo is not null;
        if (setColor || setLogo)
        {
            if (!await features.InvitesReadyAsync()) return NotReady("Workspace logos and colours arrive with the next server update.");
            if (LookProblem(body!.Color, body.Logo) is { } bad) return bad;
        }
        await Workspaces.UpdateAsync(workspaceId, name, tz);
        if (setColor || setLogo)
            await Workspaces.SetLookAsync(workspaceId, setColor, WorkspaceRules.CleanColor(body!.Color), setLogo, string.IsNullOrEmpty(body.Logo) ? null : body.Logo);
        return Ok((await DtosAsync([((await Workspaces.GetAsync(workspaceId))!, role!)]))[0]);
    }

    /// <summary>The failure-alert default: email a flow's creator when it fails, for flows that don't choose for themselves.</summary>
    [HttpPut("api/w/{workspaceId:guid}/alerts")]
    public async Task<ActionResult<WorkspaceDto>> Alerts(Guid workspaceId, [FromBody] WorkspaceAlertsBody body)
    {
        var (role, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await features.AlertsReadyAsync()) return NotReady("Failure alerts arrive with the next server update.");
        await alerts.SetWorkspaceDefaultAsync(workspaceId, body.On);
        return Ok((await DtosAsync([((await Workspaces.GetAsync(workspaceId))!, role!)]))[0]);
    }

    /// <summary>Leave the workspace (204). Never as its last owner (409 <c>last_owner</c>): make someone else an owner, or delete it.</summary>
    [HttpPost("api/w/{workspaceId:guid}/leave")]
    public async Task<IActionResult> Leave(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var plan = await Workspaces.ApplyAsync(workspaceId, s => WorkspaceRules.Leave(s, Sub));
        if (plan is null) return NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" });
        if (plan.Ok) return NoContent();
        return plan.Error == WorkspaceCodes.LastOwner
            ? Refuse(WorkspaceCodes.LastOwner, "You're the only owner. Make someone else an owner first, or delete the workspace.")
            : Refuse(plan.Error!);
    }

    /// <summary>Hand the workspace over (204): the member named becomes an owner and the primary owner; you become an admin. Owners only.</summary>
    [HttpPost("api/w/{workspaceId:guid}/transfer")]
    public async Task<IActionResult> Transfer(Guid workspaceId, [FromBody] TransferWorkspaceBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!WorkspaceRules.CanTransfer(role)) return Refuse(WorkspaceCodes.OwnerOnly, "Only an owner can hand the workspace over.");
        if (string.IsNullOrWhiteSpace(body?.VantraId)) return BadRequest(new { error = "Choose who to hand it to." });
        var plan = await Workspaces.ApplyAsync(workspaceId, s => WorkspaceRules.Transfer(s, Sub, body.VantraId));
        if (plan is null) return NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" });
        if (plan.Ok) return NoContent();
        return plan.Error == WorkspaceCodes.Self ? Refuse(WorkspaceCodes.Self, "You already own it.") : Refuse(plan.Error!);
    }

    /// <summary>
    /// Delete the workspace, for everyone: owners only (403 <c>owner_only</c>), with its name typed as <c>confirm</c>
    /// (400 <c>confirm_mismatch</c>). In order:
    /// <list type="number">
    /// <item>every flow is switched off, so nothing new starts;</item>
    /// <item>its Orbit and Projects connections are disconnected in the apps (Flow's webhooks removed and its key revoked
    /// there), and its Slack and Google connections revoked at the provider, best effort (<see cref="IWorkspaceTeardown"/>);</item>
    /// <item>its runs, run logs and queued events go, then — in one transaction — its flows and their versions,
    /// connections, outside-app tokens, poll cursors, invitations, usage, members and the workspace itself.</item>
    /// </list>
    /// Answers which connections were disconnected and which apps couldn't be reached. A workspace that's already
    /// gone answers 204, and one that failed part way can be deleted again: the members go last.
    /// </summary>
    [HttpDelete("api/w/{workspaceId:guid}")]
    public async Task<IActionResult> Delete(Guid workspaceId, [FromBody] DeleteWorkspaceBody? body, CancellationToken ct)
    {
        var w = await Workspaces.GetAsync(workspaceId);
        if (w is null) return NoContent();
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!WorkspaceRules.CanDelete(role)) return Refuse(WorkspaceCodes.OwnerOnly, "Only an owner can delete the workspace.");
        if (!WorkspaceRules.ConfirmMatches(w.Name, body?.Confirm))
            return Refuse(WorkspaceCodes.ConfirmMismatch, $"Type the workspace's name, {w.Name}, to delete it.");
        await Workspaces.PauseAllAsync(workspaceId, "The workspace is being deleted.");
        var report = await teardown.DisconnectAllAsync(workspaceId, ct);
        await Workspaces.DeleteAllAsync(workspaceId);
        log.LogInformation("Workspace {Workspace} deleted by its owner ({Disconnected} connections disconnected, {Unreachable} unreachable)",
            workspaceId, report.Disconnected, report.Unreachable.Count);
        return Ok(new { deleted = true, disconnected = report.Disconnected, unreachable = report.Unreachable });
    }

    private async Task<IReadOnlyList<WorkspaceDto>> DtosAsync(IReadOnlyList<(WorkspaceRow Workspace, string Role)> rows)
    {
        var limits = FlowLimits.From(config);
        var ids = rows.Select(r => r.Workspace.Id).ToList();
        var lookReady = await features.InvitesReadyAsync();
        var looks = lookReady ? await Workspaces.LooksAsync(ids) : new Dictionary<Guid, WorkspaceLook>();
        var counts = await Workspaces.MemberCountsAsync(ids);
        var alertsReady = await features.AlertsReadyAsync();
        // Shared Vantra workspaces, stage 1: read only, and nothing on the wire changes until a workspace is linked.
        var links = shared is null ? null : await shared.ForLocalAsync(ids);
        var list = new List<WorkspaceDto>();
        foreach (var (w, role) in rows)
        {
            var (runs, emails) = await Workspaces.UsageAsync(w.Id, DateTime.UtcNow);
            bool? alertDefault = alertsReady ? await alerts.WorkspaceDefaultAsync(w.Id) : null;
            var look = looks.GetValueOrDefault(w.Id);
            list.Add(new WorkspaceDto(w.Id, w.Name, w.TimeZone, role, w.MonthlyRunQuota ?? limits.MonthlyRuns, runs, emails, limits.EmailsPerDay, alertDefault,
                lookReady ? look?.Color ?? WorkspaceRules.DefaultColor : null, look?.Logo, counts.GetValueOrDefault(w.Id, 1), Iso(w.CreatedAt),
                string.Equals(w.OwnerVantraId, Sub, StringComparison.OrdinalIgnoreCase),
                links is null ? null : SharedWorkspaces.Ref(links, w.Id)));
        }
        return list;
    }

    /// <summary>A colour that isn't <c>#rrggbb</c>, or a logo that isn't a small PNG, JPEG or WebP; null when they're fine ("" clears).</summary>
    private ObjectResult? LookProblem(string? color, string? logo)
    {
        if (!string.IsNullOrWhiteSpace(color) && WorkspaceRules.CleanColor(color) is null)
            return Refuse(WorkspaceCodes.InvalidColor, "Choose a colour like #12855d.");
        if (!string.IsNullOrEmpty(logo) && LogoValidator.Problem(logo) is { } bad)
            return Refuse(WorkspaceCodes.InvalidLogo, bad);
        return null;
    }

    private ObjectResult NotReady(string message) => StatusCode(503, new { error = message, code = WorkspaceCodes.NotReady });

    private ObjectResult Refuse(string code, string? message = null) =>
        StatusCode(WorkspaceCodes.StatusFor(code), new { error = message ?? WorkspaceCodes.MessageFor(code), code });

    private static string? ValidZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()).Id; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}
