using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Flow workspaces and their members.
/// <code>
/// GET    api/workspaces                         → yours (a first one is made on first visit)
/// POST   api/workspaces                         { name, timeZone } → 201
/// PATCH  api/w/{ws}                             { name, timeZone } (owner / admin)
/// GET    api/w/{ws}/members
/// POST   api/w/{ws}/members                     { email, role } (owner / admin; a Vantra account)
/// PATCH  api/w/{ws}/members/{vantraId}          { role }
/// DELETE api/w/{ws}/members/{vantraId}
/// </code>
/// </summary>
public sealed class WorkspacesController(WorkspacesRepository workspaces, UsersRepository users, IConfiguration config) : FlowControllerBase(workspaces)
{
    [HttpGet("api/workspaces")]
    public async Task<ActionResult<IReadOnlyList<WorkspaceDto>>> List()
    {
        var mine = await Workspaces.ForUserAsync(Sub);
        if (mine.Count == 0)
        {
            var first = await Workspaces.CreateAsync("My workspace", "UTC", Sub, Email, DisplayName);
            mine = [(first, FlowRoles.Owner)];
        }
        var list = new List<WorkspaceDto>();
        foreach (var (w, role) in mine) list.Add(await DtoAsync(w, role));
        return Ok(list);
    }

    [HttpPost("api/workspaces")]
    public async Task<ActionResult<WorkspaceDto>> Create([FromBody] CreateWorkspaceBody body)
    {
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 120) return BadRequest(new { error = "Give the workspace a name (up to 120 characters)." });
        var tz = ValidZone(body.TimeZone) ?? "UTC";
        var w = await Workspaces.CreateAsync(name, tz, Sub, Email, DisplayName);
        return StatusCode(201, await DtoAsync(w, FlowRoles.Owner));
    }

    [HttpPatch("api/w/{workspaceId:guid}")]
    public async Task<ActionResult<WorkspaceDto>> Update(Guid workspaceId, [FromBody] UpdateWorkspaceBody body)
    {
        var (role, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        var w = (await Workspaces.GetAsync(workspaceId))!;
        var name = body.Name is null ? w.Name : body.Name.Trim();
        if (name.Length is 0 or > 120) return BadRequest(new { error = "Give the workspace a name (up to 120 characters)." });
        var tz = body.TimeZone is null ? w.TimeZone : ValidZone(body.TimeZone) ?? "";
        if (tz.Length == 0) return BadRequest(new { error = "That isn't a time zone we know." });
        await Workspaces.UpdateAsync(workspaceId, name, tz);
        return Ok(await DtoAsync((await Workspaces.GetAsync(workspaceId))!, role!));
    }

    [HttpGet("api/w/{workspaceId:guid}/members")]
    public async Task<ActionResult<IReadOnlyList<MemberDto>>> Members(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return Ok((await Workspaces.MembersAsync(workspaceId)).Select(m => new MemberDto(m.VantraId, m.Name, m.Email, m.Role, Iso(m.AddedAt))).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/members")]
    public async Task<ActionResult<MemberDto>> Add(Guid workspaceId, [FromBody] AddMemberBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        var role = body.Role ?? FlowRoles.Member;
        if (role is not (FlowRoles.Admin or FlowRoles.Member)) return BadRequest(new { error = "Choose admin or member." });
        var email = body.Email?.Trim() ?? "";
        var user = email.Length > 0 ? await users.FindByEmailAsync(email) : null;
        if (user?.VantraID is not { Length: > 0 } id)
            return NotFound(new { error = "There's no Vantra account with that email. Ask them to sign up first.", code = "no_account" });
        var name = $"{user.FirstName} {user.LastName}".Trim();
        if (!await Workspaces.UpsertMemberAsync(workspaceId, id, role, user.Email, name.Length > 0 ? name : null, Sub))
            return Conflict(new { error = "They're already in this workspace.", code = "already_member" });
        return StatusCode(201, new MemberDto(id, name, user.Email, role, Iso(DateTime.UtcNow)));
    }

    [HttpPatch("api/w/{workspaceId:guid}/members/{vantraId}")]
    public async Task<IActionResult> Role(Guid workspaceId, string vantraId, [FromBody] UpdateMemberBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (body.Role is not (FlowRoles.Admin or FlowRoles.Member)) return BadRequest(new { error = "Choose admin or member." });
        if (await Workspaces.RoleAsync(workspaceId, vantraId) is not { } current) return NotFound(new { error = "They aren't in this workspace." });
        if (current == FlowRoles.Owner) return BadRequest(new { error = "The owner's role can't change." });
        await Workspaces.UpsertMemberAsync(workspaceId, vantraId, body.Role, null, null, Sub);
        return NoContent();
    }

    [HttpDelete("api/w/{workspaceId:guid}/members/{vantraId}")]
    public async Task<IActionResult> Remove(Guid workspaceId, string vantraId)
    {
        // Anyone may leave; removing someone else takes an owner or admin.
        var (_, refusal) = vantraId == Sub ? await MemberAsync(workspaceId) : await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        return await Workspaces.RemoveMemberAsync(workspaceId, vantraId) ? NoContent()
            : BadRequest(new { error = "The owner can't be removed." });
    }

    private async Task<WorkspaceDto> DtoAsync(WorkspaceRow w, string role)
    {
        var limits = FlowLimits.From(config);
        var (runs, emails) = await Workspaces.UsageAsync(w.Id, DateTime.UtcNow);
        return new WorkspaceDto(w.Id, w.Name, w.TimeZone, role, w.MonthlyRunQuota ?? limits.MonthlyRuns, runs, emails, limits.EmailsPerDay);
    }

    private static string? ValidZone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()).Id; }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }
}
