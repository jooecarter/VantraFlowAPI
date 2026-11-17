using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Shared;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// A workspace's cover and description. They're the shared Vantra workspace's (vantra.Workspaces, db/shared/003 in
/// VantraProjectsAPI), so Flow and Orbit show the same.
/// <code>
/// GET api/w/{ws}/details  → { available, canEdit, description, cover, coverPreset } (members)
/// PUT api/w/{ws}/details  { description?, cover?, coverPreset? } → the same (owners and admins; null keeps, "" clears;
///                         a photo clears the preset and the other way round; both at once is 400 invalid_cover)
/// </code>
/// <c>available</c> is false before db/shared/003 has run, or while the workspace isn't shared yet: the studio hides
/// them with a hint, and PUT answers 409 <c>not_ready</c>.
/// </summary>
public sealed class WorkspaceDetailsController(
    IWorkspaceStore workspaces, ILogger<WorkspaceDetailsController> log, SharedDetailsService? details = null, SharedSpaces? spaces = null)
    : FlowControllerBase(workspaces)
{
    /// <summary>The cover photo: a data URL like the logo, at most 400,000 characters (about 290 KB).</summary>
    public static string? ImageProblem(string cover) =>
        LogoValidator.ImageProblem(cover, SharedDetailsRules.CoverMax, "cover image", "use an image under about 290 KB");

    public sealed record DetailsBody(string? Description, string? Cover, string? CoverPreset);

    public sealed record DetailsDto(bool Available, bool CanEdit, string? Description, string? Cover, string? CoverPreset);

    [HttpGet("api/w/{workspaceId:guid}/details")]
    public async Task<ActionResult<DetailsDto>> Get(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var sharedId = await SharedIdAsync(workspaceId);
        var canEdit = FlowRoles.CanManage(role);
        if (sharedId is null) return Ok(new DetailsDto(false, canEdit, null, null, null));
        var found = await details!.GetAsync([sharedId.Value]);
        return Ok(Dto(found.TryGetValue(sharedId.Value, out var d) ? d : null, canEdit));
    }

    [HttpPut("api/w/{workspaceId:guid}/details")]
    public async Task<ActionResult<DetailsDto>> Put(Guid workspaceId, [FromBody] DetailsBody? body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        var change = new SharedDetailsChange(body?.Description, body?.Cover, body?.CoverPreset);
        if (change.Empty) return BadRequest(new { error = "Nothing to save.", code = "invalid_details" });
        if (SharedDetailsRules.Problem(change, ImageProblem) is { } problem)
            return BadRequest(new { error = problem, code = change.Description is { } d && d.Trim().Length > SharedDetailsRules.DescriptionMax ? "invalid_description" : "invalid_cover" });
        try
        {
            var sharedId = await SharedIdAsync(workspaceId, strict: true);
            if (sharedId is null)
                return Conflict(new { error = "Covers and descriptions arrive with the next server update.", code = "not_ready" });
            await details!.SetAsync(sharedId.Value, change);
            var changed = new List<string>();
            if (change.Description is not null) changed.Add("description");
            if (change.Cover is not null || change.CoverPreset is not null) changed.Add("cover");
            await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: "Changed the " + string.Join(" and ", changed));
            var found = await details.GetAsync([sharedId.Value]);
            return Ok(Dto(found.TryGetValue(sharedId.Value, out var d) ? d : null, true));
        }
        catch (Exception e) when (e is SharedUnavailableException or Microsoft.Data.SqlClient.SqlException)
        {
            log.LogWarning(e, "Couldn't save a shared workspace's cover or description");
            return StatusCode(503, new { error = "The shared workspace couldn't be reached. Nothing was changed. Try again.", code = SharedCodes.Unavailable });
        }
    }

    private static DetailsDto Dto(SharedDetails? d, bool canEdit)
    {
        var cover = string.IsNullOrEmpty(d?.Cover) ? null : d.Cover;
        return new DetailsDto(true, canEdit, string.IsNullOrWhiteSpace(d?.Description) ? null : d.Description,
            cover, cover is null && !string.IsNullOrWhiteSpace(d?.CoverPreset) ? d.CoverPreset : null);
    }

    /// <summary>The shared workspace this one is, once db/shared/003 has run and it's linked; null otherwise.</summary>
    private async Task<Guid?> SharedIdAsync(Guid workspaceId, bool strict = false)
    {
        if (details is null || spaces is null || !await details.ReadyAsync()) return null;
        try
        {
            return await spaces.LinkAsync(SharedApps.Flow, workspaceId, strict) is { Enabled: true } link ? link.SharedId : null;
        }
        catch (Exception e) when (!strict && e is not OperationCanceledException)
        {
            log.LogWarning(e, "Couldn't read a workspace's shared link; answering without its cover");
            return null;
        }
    }
}
