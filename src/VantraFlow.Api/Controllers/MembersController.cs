using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;
using VantraFlow.Api.Shared;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// A workspace's members and invitations, as Vantra Orbit's. Every endpoint checks the caller's role here, on the
/// server (<see cref="WorkspaceRules"/>): members see the list; owners and admins invite, change roles and remove;
/// only an owner makes, changes or removes an owner; there's always one owner left.
/// <code>
/// GET    api/w/{ws}/members
/// POST   api/w/{ws}/members                     { email, role } (owner / admin; an existing Vantra account, added at once)
/// PATCH  api/w/{ws}/members/{vantraId}          { role: owner | admin | member }
/// DELETE api/w/{ws}/members/{vantraId}          (yourself: leaving)
/// GET    api/w/{ws}/invites                     open invitations (owner / admin; db/005)
/// POST   api/w/{ws}/invites                     { email, role } → 201 { kind: added | invited, … } (owner / admin)
/// POST   api/w/{ws}/invites/{id}/resend         → a new link, emailed, 14 more days (one a minute)
/// DELETE api/w/{ws}/invites/{id}                → 204: revoked, its link stops working
/// </code>
/// Inviting an address that has a Vantra account adds them straight away and emails them a note; anyone else gets an
/// invitation (db/005: 503 <c>not_ready</c> before) whose link is emailed when a sender is set up. 100 a day per
/// workspace (429 <c>invite_limit</c>), and each person 30 a minute.
/// </summary>
public sealed class MembersController(
    IWorkspaceStore workspaces, IInviteStore invites, IUserDirectory users, IWorkspaceMail mail, IWorkspaceFeatures features, ILogger<MembersController> log,
    SharedMembers? shared = null, SharedTeam? team = null)
    : FlowControllerBase(workspaces)
{
    public const string RateLimitPolicy = "invites";

    [HttpGet("api/w/{workspaceId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> Members(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return Ok((await Workspaces.MembersAsync(workspaceId)).Select(Dto).ToList());
    }

    /// <summary>Add someone with a Vantra account straight away (no account: 404 <c>no_account</c>; use invitations).</summary>
    [HttpPost("api/w/{workspaceId:guid}/members")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Add(Guid workspaceId, [FromBody] AddMemberBody? body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await SharedRefusalAsync(workspaceId) is { } sharedRefusal) return sharedRefusal;
        var role = body?.Role ?? FlowRoles.Member;
        if (!WorkspaceRules.IsInvitableRole(role)) return BadRequest(new { error = "Choose admin or member." });
        var email = InviteRules.NormalizeEmail(body?.Email);
        if (email is null) return BadRequest(new { error = "Enter an email address." });
        if (await OverDailyLimitAsync(workspaceId)) return DailyLimit();
        var user = await users.FindByEmailAsync(email);
        if (user?.VantraID is not { Length: > 0 })
            return NotFound(new { error = "There's no Vantra account with that email. Invite them instead.", code = "no_account" });
        var (member, _) = await AddExistingAsync(workspaceId, user, role);
        return member is null ? AlreadyMember(email) : StatusCode(201, member);
    }

    [HttpPatch("api/w/{workspaceId:guid}/members/{vantraId}")]
    public async Task<IActionResult> Role(Guid workspaceId, string vantraId, [FromBody] UpdateMemberBody? body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await SharedRefusalAsync(workspaceId) is { } sharedRefusal) return sharedRefusal;
        if (!FlowRoles.IsValid(body?.Role)) return BadRequest(new { error = "Choose owner, admin or member." });
        var plan = await Workspaces.ApplyAsync(workspaceId, s => WorkspaceRules.SetRole(s, Sub, vantraId, body!.Role!));
        if (plan is null) return NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" });
        if (!plan.Ok) return Refuse(plan.Error!);
        if (plan.Changes.Count > 0)
        {
            var who = (await Workspaces.MembersAsync(workspaceId)).FirstOrDefault(m => string.Equals(m.VantraId, vantraId, StringComparison.OrdinalIgnoreCase));
            await LogAsync(workspaceId, Services.Team.ActivityKinds.MemberRole, detail: $"{who?.Name ?? who?.Email ?? "A member"} is now {body!.Role}");
        }
        return NoContent();
    }

    [HttpDelete("api/w/{workspaceId:guid}/members/{vantraId}")]
    public async Task<IActionResult> Remove(Guid workspaceId, string vantraId)
    {
        // Removing yourself is leaving (anyone may, but the last owner); removing someone else takes an owner or admin.
        var self = string.Equals(vantraId, Sub, StringComparison.OrdinalIgnoreCase);
        var (_, refusal) = self ? await MemberAsync(workspaceId) : await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (team is not null && shared is not null)
        {
            // A shared workspace: leaving is the shared record's; removing someone else is the shared Members page's.
            var state = await SharedStateAsync(workspaceId);
            if (state.Refusal is not null) return state.Refusal;
            if (state.State is { } st)
            {
                if (!self) return SharedOnly();
                var r = await team.LeaveAsync(st, new SharedActor(Sub, DisplayName, Email));
                return r.Body is null ? StatusCode(r.Status) : StatusCode(r.Status, r.Body);
            }
        }
        var plan = await Workspaces.ApplyAsync(workspaceId, s => self ? WorkspaceRules.Leave(s, Sub) : WorkspaceRules.Remove(s, Sub, vantraId));
        if (plan is null) return NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" });
        return plan.Ok ? NoContent() : Refuse(plan.Error!);
    }

    // ── Invitations ──

    [HttpGet("api/w/{workspaceId:guid}/invites")]
    public async Task<IActionResult> Invites(Guid workspaceId)
    {
        if (await InviteRefusalAsync(workspaceId) is { } refused) return refused;
        var now = DateTime.UtcNow;
        return Ok((await invites.ListOpenAsync(workspaceId)).Select(r => InviteDto(r, now, null)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/invites")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Invite(Guid workspaceId, [FromBody] CreateInviteBody? body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await SharedRefusalAsync(workspaceId) is { } sharedRefusal) return sharedRefusal;
        var email = InviteRules.NormalizeEmail(body?.Email);
        if (email is null) return BadRequest(new { error = "Enter a valid email address.", code = "invalid_email" });
        var role = string.IsNullOrWhiteSpace(body?.Role) ? FlowRoles.Member : body.Role.Trim().ToLowerInvariant();
        if (!WorkspaceRules.IsInvitableRole(role)) return BadRequest(new { error = "Invite them as an admin or a member.", code = "invalid_role" });
        if (await OverDailyLimitAsync(workspaceId)) return DailyLimit();

        // Someone with a Vantra account joins at once, and hears about it.
        var user = await users.FindByEmailAsync(email);
        if (user?.VantraID is { Length: > 0 })
        {
            var (member, emailed) = await AddExistingAsync(workspaceId, user, role);
            return member is null ? AlreadyMember(email) : StatusCode(201, new InviteResultDto("added", member, null, emailed));
        }

        if (!await features.InvitesReadyAsync())
            return StatusCode(503, new { error = "Inviting people without a Vantra account arrives with the next server update.", code = WorkspaceCodes.NotReady });
        var token = InviteRules.NewToken();
        var row = await invites.IssueAsync(workspaceId, email, role, InviteRules.Hash(token), Sub, DisplayName);
        var link = mail.InviteLink(Origin, token);
        var sent = await SendInviteAsync(workspaceId, row, link);
        log.LogInformation("Workspace {Workspace} invited someone ({Role}; emailed: {Emailed})", workspaceId, role, sent is not null);
        return StatusCode(201, new InviteResultDto("invited", null, InviteDto(sent ?? row, DateTime.UtcNow, link), sent is not null));
    }

    /// <summary>Email it again with a new link (the old one stops working) and 14 more days. 429 <c>invite_limit</c> with <c>retryAfter</c> within a minute of the last.</summary>
    [HttpPost("api/w/{workspaceId:guid}/invites/{inviteId:guid}/resend")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Resend(Guid workspaceId, Guid inviteId)
    {
        if (await InviteRefusalAsync(workspaceId) is { } refused) return refused;
        var row = await invites.GetAsync(workspaceId, inviteId);
        if (row is null || !InviteRules.IsOpen(row.Status)) return NotFound(new { error = "That invitation isn't open any more.", code = "invite_invalid" });
        var now = DateTime.UtcNow;
        if (!InviteRules.CanResend(row.SentAt, now))
            return StatusCode(429, new
            {
                error = "That invitation was just sent. Give it a minute.", code = WorkspaceCodes.InviteLimit, retryAfter = InviteRules.RetryAfterSeconds(row.SentAt, now),
            });
        if (await OverDailyLimitAsync(workspaceId)) return DailyLimit();
        var token = InviteRules.NewToken();
        row = await invites.RenewAsync(workspaceId, inviteId, InviteRules.Hash(token));
        if (row is null) return NotFound(new { error = "That invitation isn't open any more.", code = "invite_invalid" });
        var link = mail.InviteLink(Origin, token);
        var sent = await SendInviteAsync(workspaceId, row, link);
        return Ok(InviteDto(sent ?? row, DateTime.UtcNow, link));
    }

    /// <summary>Revoke it (204): its link stops working. Revoking one that's already closed is fine too.</summary>
    [HttpDelete("api/w/{workspaceId:guid}/invites/{inviteId:guid}")]
    public async Task<IActionResult> Revoke(Guid workspaceId, Guid inviteId)
    {
        if (await InviteRefusalAsync(workspaceId) is { } refused) return refused;
        if (await invites.RevokeAsync(workspaceId, inviteId)) return NoContent();
        return await invites.GetAsync(workspaceId, inviteId) is null
            ? NotFound(new { error = "There's no such invitation.", code = "invite_invalid" })
            : NoContent();
    }

    // ── Helpers ──

    private string? Origin => Request.Headers.Origin.ToString() is { Length: > 0 } o ? o : null;

    /// <summary>The shared state when this workspace's members are shared (stage 2), or the refusal when that can't be told.</summary>
    private async Task<(SharedState? State, IActionResult? Refusal)> SharedStateAsync(Guid workspaceId)
    {
        if (shared is null) return (null, null);
        try { return (await shared.ForLocalAsync(SharedApps.Flow, workspaceId, strict: true), null); }
        catch (SharedUnavailableException)
        {
            return (null, StatusCode(503, new { error = "The shared workspace couldn't be reached. Nothing was changed. Try again.", code = SharedCodes.Unavailable }));
        }
    }

    /// <summary>
    /// 409 <c>shared_workspace</c> when this workspace's members are shared across the Vantra apps: they're managed on
    /// the shared Members page (<see cref="SharedMembersController"/>), never in Flow's own list alone.
    /// </summary>
    private async Task<IActionResult?> SharedRefusalAsync(Guid workspaceId)
    {
        var (state, refusal) = await SharedStateAsync(workspaceId);
        return refusal ?? (state is null ? null : SharedOnly());
    }

    private ObjectResult SharedOnly() => Conflict(new
    {
        error = "This workspace's members are shared across the Vantra apps. Reload to manage them here.", code = "shared_workspace",
    });

    /// <summary>Owners and admins, once db/005 has run.</summary>
    private async Task<IActionResult?> InviteRefusalAsync(Guid workspaceId)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await SharedRefusalAsync(workspaceId) is { } sharedRefusal) return sharedRefusal;
        if (!await features.InvitesReadyAsync())
            return StatusCode(503, new { error = "Invitations arrive with the next server update.", code = WorkspaceCodes.NotReady });
        return null;
    }

    private async Task<bool> OverDailyLimitAsync(Guid workspaceId)
    {
        var since = DateTime.UtcNow.Date;
        var sent = await Workspaces.AddedSinceAsync(workspaceId, since);
        if (await features.InvitesReadyAsync()) sent += await invites.CountSentSinceAsync(workspaceId, since);
        return sent >= InviteRules.DailyLimitPerWorkspace;
    }

    private ObjectResult DailyLimit() => StatusCode(429, new
    {
        error = $"This workspace has invited {InviteRules.DailyLimitPerWorkspace} people today. Try again tomorrow.", code = WorkspaceCodes.InviteLimit,
    });

    private ObjectResult AlreadyMember(string email) =>
        Conflict(new { error = $"{email} is already in this workspace.", code = WorkspaceCodes.AlreadyMember });

    /// <summary>Adds an existing account and emails them (best effort). Null when they're already in.</summary>
    private async Task<(MemberDto? Member, bool Emailed)> AddExistingAsync(Guid workspaceId, UserDetails user, string role = FlowRoles.Member)
    {
        var name = $"{user.FirstName} {user.LastName}".Trim();
        if (!await Workspaces.AddMemberAsync(workspaceId, user.VantraID!, role, user.Email, name.Length > 0 ? name : null, Sub)) return (null, false);
        var ws = await Workspaces.GetAsync(workspaceId);
        var emailed = mail.CanSend && ws is not null
                      && await mail.SendAddedAsync(user.Email, name.Length > 0 ? name : null, ws.Name, DisplayName, Email, role, mail.WorkspaceLink(Origin, workspaceId));
        return (new MemberDto(user.VantraID!, name.Length > 0 ? name : null, user.Email, role, Iso(DateTime.UtcNow)), emailed);
    }

    /// <summary>Emails the invitation when a sender is set up; the row as sent, or null when it wasn't.</summary>
    private async Task<InviteRow?> SendInviteAsync(Guid workspaceId, InviteRow row, string link)
    {
        if (!mail.CanSend) return null;
        var ws = await Workspaces.GetAsync(workspaceId);
        if (ws is null || !await mail.SendInviteAsync(row.Email, ws.Name, DisplayName, Email, row.Role, link)) return null;
        return await invites.MarkSentAsync(row.Id) ?? row;
    }

    private static MemberDto Dto(MemberRow m) => new(m.VantraId, m.Name, m.Email, m.Role, Iso(m.AddedAt));

    private static InviteDto InviteDto(InviteRow r, DateTime now, string? link) => new(
        r.Id.ToString(), r.Email, r.Role, InviteRules.ListStatus(r.Status, r.ExpiresAt, now), Iso(r.CreatedAt), Iso(r.ExpiresAt), Iso(r.SentAt),
        r.InvitedByName, link);

    private ObjectResult Refuse(string code) => StatusCode(WorkspaceCodes.StatusFor(code), new { error = WorkspaceCodes.MessageFor(code), code });
}
