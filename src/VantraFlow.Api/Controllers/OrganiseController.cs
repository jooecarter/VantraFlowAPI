using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Team;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// How a workspace's flows are organised (db/011; 503 <c>not_ready</c> before): folders (one level), tags with colours,
/// and each flow's owner. Roles are checked here for every change (<see cref="TeamRules"/>).
/// <code>
/// GET    api/w/{ws}/organise                    → { folders, tags, flows: [{ flowId, folderId, ownerId, tagIds, comments }], can }
/// POST   api/w/{ws}/folders                     { name } → 201 (owners and admins)
/// PATCH  api/w/{ws}/folders/{id}                { name } (owners and admins)
/// DELETE api/w/{ws}/folders/{id}                its flows go back to the top level (owners and admins)
/// PUT    api/w/{ws}/flows/{id}/folder           { folderId | null } (members)
/// POST   api/w/{ws}/tags                        { name, color } → 201, or 200 with the tag of that name (members)
/// PATCH  api/w/{ws}/tags/{id}                   { name?, color? } (owners and admins)
/// DELETE api/w/{ws}/tags/{id}                   taken off every flow (owners and admins)
/// PUT    api/w/{ws}/flows/{id}/tags             { tagIds } (members; at most 10)
/// PUT    api/w/{ws}/flows/{id}/owner            { vantraId } a member of the workspace (owners and admins)
/// </code>
/// </summary>
public sealed class OrganiseController(IWorkspaceStore workspaces, ITeamStore team) : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/organise")]
    public async Task<IActionResult> Get(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await team.ReadyAsync()) return NotReady();
        var folders = await team.FoldersAsync(workspaceId);
        var tags = await team.TagsAsync(workspaceId);
        var byFlow = (await team.FlowTagsAsync(workspaceId)).GroupBy(t => t.FlowId).ToDictionary(g => g.Key, g => g.Select(t => t.TagId).ToList());
        var flows = (await team.PlacementsAsync(workspaceId))
            .Select(p => new FlowPlacementDto(p.Id, p.FolderId, p.Owner, byFlow.GetValueOrDefault(p.Id) ?? [], p.Comments)).ToList();
        return Ok(new OrganiseDto(folders.Select(Dto).ToList(), tags.Select(Dto).ToList(), flows, TeamRules.Can(role)));
    }

    // ── Folders ──

    [HttpPost("api/w/{workspaceId:guid}/folders")]
    public async Task<IActionResult> CreateFolder(Guid workspaceId, [FromBody] FolderBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanManageFolders, "Only owners and admins manage folders.") is { } no) return no;
        if (TeamRules.CleanName(body?.Name, TeamRules.FolderNameMax) is not { } name) return BadRequest(new { error = "Name the folder (up to 80 characters).", code = "invalid_name" });
        if ((await team.FoldersAsync(workspaceId)).Count >= TeamRules.MaxFolders) return Conflict(new { error = $"A workspace can have up to {TeamRules.MaxFolders} folders.", code = "too_many" });
        var row = new FolderRow(FlowSecrets.NewRowId("fld_"), workspaceId, name, Sub, DateTime.UtcNow);
        if (!await team.InsertFolderAsync(row)) return FolderExists();
        await LogAsync(workspaceId, ActivityKinds.FolderChanged, detail: $"Created “{name}”");
        return StatusCode(201, Dto(row));
    }

    [HttpPatch("api/w/{workspaceId:guid}/folders/{id}")]
    public async Task<IActionResult> RenameFolder(Guid workspaceId, string id, [FromBody] FolderBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanManageFolders, "Only owners and admins manage folders.") is { } no) return no;
        if ((await team.FoldersAsync(workspaceId)).FirstOrDefault(f => f.Id == id) is not { } folder) return NoFolder();
        if (TeamRules.CleanName(body?.Name, TeamRules.FolderNameMax) is not { } name) return BadRequest(new { error = "Name the folder (up to 80 characters).", code = "invalid_name" });
        if (name != folder.Name)
        {
            if (!await team.RenameFolderAsync(workspaceId, id, name)) return FolderExists();
            await LogAsync(workspaceId, ActivityKinds.FolderChanged, detail: $"Renamed “{folder.Name}” to “{name}”");
        }
        return Ok(Dto(folder with { Name = name }));
    }

    [HttpDelete("api/w/{workspaceId:guid}/folders/{id}")]
    public async Task<IActionResult> DeleteFolder(Guid workspaceId, string id)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanManageFolders, "Only owners and admins manage folders.") is { } no) return no;
        if ((await team.FoldersAsync(workspaceId)).FirstOrDefault(f => f.Id == id) is not { } folder) return NoContent();
        await team.DeleteFolderAsync(workspaceId, id);
        await LogAsync(workspaceId, ActivityKinds.FolderChanged, detail: $"Deleted “{folder.Name}”");
        return NoContent();
    }

    [HttpPut("api/w/{workspaceId:guid}/flows/{flowId}/folder")]
    public async Task<IActionResult> Move(Guid workspaceId, string flowId, [FromBody] FlowFolderBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanEdit, "You can't change flows in this workspace.") is { } no) return no;
        if (await team.PlacementAsync(workspaceId, flowId) is not { } flow) return NoFlow();
        FolderRow? folder = null;
        if (body?.FolderId is { Length: > 0 } fid && (folder = (await team.FoldersAsync(workspaceId)).FirstOrDefault(f => f.Id == fid)) is null) return NoFolder();
        if (flow.FolderId == folder?.Id) return NoContent();
        await team.SetFolderAsync(workspaceId, flowId, folder?.Id);
        await LogAsync(workspaceId, ActivityKinds.FlowMoved, flowId, flow.Name, detail: folder is null ? "To the top level" : $"Into “{folder.Name}”");
        return NoContent();
    }

    // ── Tags ──

    [HttpPost("api/w/{workspaceId:guid}/tags")]
    public async Task<IActionResult> CreateTag(Guid workspaceId, [FromBody] TagBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanEdit, "You can't change flows in this workspace.") is { } no) return no;
        if (TeamRules.CleanName(body?.Name, TeamRules.TagNameMax) is not { } name) return BadRequest(new { error = "Name the tag (up to 40 characters).", code = "invalid_name" });
        var color = body?.Color is null ? TeamRules.Colors[0] : TeamRules.CleanColor(body.Color);
        if (color is null) return BadRequest(new { error = "Choose one of the tag colours.", code = "invalid_color" });
        var tags = await team.TagsAsync(workspaceId);
        if (tags.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) is { } same) return Ok(Dto(same));
        if (tags.Count >= TeamRules.MaxTags) return Conflict(new { error = $"A workspace can have up to {TeamRules.MaxTags} tags.", code = "too_many" });
        var row = new TagRow(FlowSecrets.NewRowId("tag_"), workspaceId, name, color, Sub, DateTime.UtcNow);
        if (!await team.InsertTagAsync(row)) return TagExists();
        return StatusCode(201, Dto(row));
    }

    [HttpPatch("api/w/{workspaceId:guid}/tags/{id}")]
    public async Task<IActionResult> UpdateTag(Guid workspaceId, string id, [FromBody] TagBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanManageTags, "Only owners and admins change tags.") is { } no) return no;
        if ((await team.TagsAsync(workspaceId)).FirstOrDefault(t => t.Id == id) is not { } tag) return NotFound(new { error = "That tag doesn't exist any more.", code = "no_tag" });
        var name = body?.Name is null ? tag.Name : TeamRules.CleanName(body.Name, TeamRules.TagNameMax);
        if (name is null) return BadRequest(new { error = "Name the tag (up to 40 characters).", code = "invalid_name" });
        var color = body?.Color is null ? tag.Color : TeamRules.CleanColor(body.Color);
        if (color is null) return BadRequest(new { error = "Choose one of the tag colours.", code = "invalid_color" });
        if (!await team.UpdateTagAsync(workspaceId, id, name, color)) return TagExists();
        return Ok(Dto(tag with { Name = name, Color = color }));
    }

    [HttpDelete("api/w/{workspaceId:guid}/tags/{id}")]
    public async Task<IActionResult> DeleteTag(Guid workspaceId, string id)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanManageTags, "Only owners and admins change tags.") is { } no) return no;
        await team.DeleteTagAsync(workspaceId, id);
        return NoContent();
    }

    [HttpPut("api/w/{workspaceId:guid}/flows/{flowId}/tags")]
    public async Task<IActionResult> SetTags(Guid workspaceId, string flowId, [FromBody] FlowTagsBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanEdit, "You can't change flows in this workspace.") is { } no) return no;
        if (await team.PlacementAsync(workspaceId, flowId) is null) return NoFlow();
        var ids = (body?.TagIds ?? []).Where(t => t is { Length: > 0 and <= 40 }).Distinct(StringComparer.Ordinal).ToList();
        if (ids.Count > TeamRules.MaxTagsPerFlow) return BadRequest(new { error = $"A flow can have up to {TeamRules.MaxTagsPerFlow} tags.", code = "too_many" });
        var known = (await team.TagsAsync(workspaceId)).Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (ids.Any(t => !known.Contains(t))) return NotFound(new { error = "That tag doesn't exist any more.", code = "no_tag" });
        await team.SetTagsAsync(workspaceId, flowId, ids);
        return Ok(new { tagIds = ids });
    }

    // ── Owners ──

    [HttpPut("api/w/{workspaceId:guid}/flows/{flowId}/owner")]
    public async Task<IActionResult> SetOwner(Guid workspaceId, string flowId, [FromBody] FlowOwnerBody? body)
    {
        if (await RefuseAsync(workspaceId, TeamRules.CanChangeOwner, "Only owners and admins change a flow's owner.") is { } no) return no;
        if (await team.PlacementAsync(workspaceId, flowId) is not { } flow) return NoFlow();
        var member = (await Workspaces.MembersAsync(workspaceId)).FirstOrDefault(m => string.Equals(m.VantraId, body?.VantraId, StringComparison.OrdinalIgnoreCase));
        if (member is null) return BadRequest(new { error = "Choose someone in this workspace.", code = "not_member" });
        if (!string.Equals(flow.Owner, member.VantraId, StringComparison.OrdinalIgnoreCase))
        {
            await team.SetOwnerAsync(workspaceId, flowId, member.VantraId);
            await LogAsync(workspaceId, ActivityKinds.FlowOwner, flowId, flow.Name, detail: $"To {member.Name ?? member.Email ?? "a member"}");
        }
        return Ok(new { ownerId = member.VantraId });
    }

    // ── Helpers ──

    /// <summary>The refusal for a caller who isn't a member, or whose role can't do this; and 503 before db/011.</summary>
    private async Task<IActionResult?> RefuseAsync(Guid workspaceId, Func<string?, bool> allowed, string message)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!allowed(role)) return StatusCode(403, new { error = message, code = "not_allowed" });
        return await team.ReadyAsync() ? null : NotReady();
    }

    private ObjectResult NotReady() => StatusCode(503, new { error = "Folders, tags and owners arrive with the next server update.", code = "not_ready" });
    private NotFoundObjectResult NoFlow() => NotFound(new { error = "That flow doesn't exist any more.", code = "no_flow" });
    private NotFoundObjectResult NoFolder() => NotFound(new { error = "That folder doesn't exist any more.", code = "no_folder" });
    private ConflictObjectResult FolderExists() => Conflict(new { error = "There's already a folder with that name.", code = "folder_exists" });
    private ConflictObjectResult TagExists() => Conflict(new { error = "There's already a tag with that name.", code = "tag_exists" });

    private static FolderDto Dto(FolderRow f) => new(f.Id, f.Name, Iso(f.CreatedAt));
    private static TagDto Dto(TagRow t) => new(t.Id, t.Name, t.Color);
}
