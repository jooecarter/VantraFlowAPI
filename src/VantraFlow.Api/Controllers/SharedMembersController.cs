using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Shared;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The Members page of a shared Vantra workspace (stage 2): everyone in it with their one role and the apps they may
/// use, invitations that choose apps, and per-app access. Each answers 404 <c>not_shared</c> for a workspace whose
/// members aren't shared (not linked, or before db/shared/002 and its backfill), where <see cref="MembersController"/>
/// carries on as before. Rules: <see cref="SharedRules"/>; the shared record is written first (503 when it can't be),
/// then Flow's own list. Giving someone Vantra Projects is asked of Projects, which checks its seats (402 <c>seats_full</c>).
/// <code>
/// GET    api/w/{ws}/shared/members
/// POST   api/w/{ws}/shared/invites                       { email, role: admin | member | guest, guestType?, apps[] }
/// POST   api/w/{ws}/shared/invites/{id}/resend
/// DELETE api/w/{ws}/shared/invites/{id}
/// PUT    api/w/{ws}/shared/members/{vantraId}/role       { role, guestType? }
/// PUT    api/w/{ws}/shared/members/{vantraId}/apps/{app} give access
/// DELETE api/w/{ws}/shared/members/{vantraId}/apps/{app} take it away
/// DELETE api/w/{ws}/shared/members/{vantraId}            remove (yourself: leave)
/// POST   api/w/{ws}/shared/transfer                      { vantraId }
/// </code>
/// </summary>
public sealed class SharedMembersController(IWorkspaceStore workspaces, SharedSync sync, SharedTeam team) : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/shared/members")]
    public Task<IActionResult> Members(Guid workspaceId) =>
        Run(workspaceId, write: false, async s => SharedResult.Okay(await team.TeamAsync(s, Sub)));

    [HttpPost("api/w/{workspaceId:guid}/shared/invites")]
    [EnableRateLimiting(MembersController.RateLimitPolicy)]
    public Task<IActionResult> Invite(Guid workspaceId, [FromBody] SharedInviteBody? body) =>
        Run(workspaceId, write: true, s => team.InviteAsync(s, Actor, body, Origin));

    [HttpPost("api/w/{workspaceId:guid}/shared/invites/{inviteId:guid}/resend")]
    [EnableRateLimiting(MembersController.RateLimitPolicy)]
    public Task<IActionResult> Resend(Guid workspaceId, Guid inviteId) =>
        Run(workspaceId, write: true, s => team.ResendAsync(s, Actor, inviteId, Origin));

    [HttpDelete("api/w/{workspaceId:guid}/shared/invites/{inviteId:guid}")]
    public Task<IActionResult> RevokeInvite(Guid workspaceId, Guid inviteId) =>
        Run(workspaceId, write: true, s => team.RevokeInviteAsync(s, Actor, inviteId));

    [HttpPut("api/w/{workspaceId:guid}/shared/members/{vantraId}/role")]
    public async Task<IActionResult> Role(Guid workspaceId, string vantraId, [FromBody] SharedRoleBody? body)
    {
        var result = await Run(workspaceId, write: true, s => team.SetRoleAsync(s, Actor, vantraId, body));
        if (result is IStatusCodeActionResult { StatusCode: >= 200 and < 300 })
        {
            var who = (await Workspaces.MembersAsync(workspaceId)).FirstOrDefault(m => string.Equals(m.VantraId, vantraId, StringComparison.OrdinalIgnoreCase));
            await LogAsync(workspaceId, Services.Team.ActivityKinds.MemberRole, detail: $"{who?.Name ?? who?.Email ?? "A member"} is now {body?.Role}");
        }
        return result;
    }

    [HttpPut("api/w/{workspaceId:guid}/shared/members/{vantraId}/apps/{app}")]
    public Task<IActionResult> Grant(Guid workspaceId, string vantraId, string app) =>
        Run(workspaceId, write: true, s => team.GrantAsync(s, Actor, vantraId, app));

    [HttpDelete("api/w/{workspaceId:guid}/shared/members/{vantraId}/apps/{app}")]
    public Task<IActionResult> Revoke(Guid workspaceId, string vantraId, string app) =>
        Run(workspaceId, write: true, s => team.RevokeAsync(s, Actor, vantraId, app));

    [HttpDelete("api/w/{workspaceId:guid}/shared/members/{vantraId}")]
    public Task<IActionResult> Remove(Guid workspaceId, string vantraId) =>
        Run(workspaceId, write: true, s => team.RemoveAsync(s, Actor, vantraId));

    [HttpPost("api/w/{workspaceId:guid}/shared/transfer")]
    public Task<IActionResult> Transfer(Guid workspaceId, [FromBody] SharedTransferBody? body) =>
        Run(workspaceId, write: true, s => team.TransferAsync(s, Actor, body?.VantraId));

    private SharedActor Actor => new(Sub, DisplayName, Email);

    private string? Origin => Request.Headers.Origin.ToString() is { Length: > 0 } o ? o : null;

    private async Task<IActionResult> Run(Guid workspaceId, bool write, Func<SharedState, Task<SharedResult>> act)
    {
        try
        {
            var state = await sync.WorkspaceAsync(workspaceId, force: !write, strict: write);
            var (_, refusal) = await MemberAsync(workspaceId);
            if (refusal is not null) return refusal;
            if (state is null) return Answer(SharedResult.Error(SharedCodes.NotShared));
            return Answer(await act(state));
        }
        catch (Exception e) when (e is SharedUnavailableException or Microsoft.Data.SqlClient.SqlException)
        {
            return Answer(SharedResult.Error(SharedCodes.Unavailable, "The shared workspace couldn't be reached. Nothing was changed. Try again."));
        }
    }

    private IActionResult Answer(SharedResult r) => r.Body is null ? StatusCode(r.Status) : StatusCode(r.Status, r.Body);
}
