using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Shared;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// How the Vantra apps look for the caller, saved to their Vantra account (vantra.UserAppearance, db/shared/003 in
/// VantraProjectsAPI) so Flow, Orbit and Projects match. Account scoped, no workspace.
/// <code>
/// GET api/me/appearance  → { available, appearance: { theme, nav, density, reduceMotion, accent, aiRibbon, updatedAt } | null }
/// PUT api/me/appearance  { theme?, nav?, density?, reduceMotion?, accent?, aiRibbon? } → the same (a null is left as it is)
/// </code>
/// Before db/shared/003 GET answers <c>{ available: false }</c> and PUT 404 <c>not_ready</c>: the studio keeps it in the
/// browser, as before.
/// </summary>
public sealed class AppearanceController(IWorkspaceStore workspaces, SharedAppearanceService appearance) : FlowControllerBase(workspaces)
{
    [HttpGet("api/me/appearance")]
    public async Task<IActionResult> Get()
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        var (status, body) = await appearance.GetAsync(Sub);
        return StatusCode(status, body);
    }

    [HttpPut("api/me/appearance")]
    public async Task<IActionResult> Put([FromBody] SharedAppearanceBody? body)
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        var (status, answer) = await appearance.SaveAsync(Sub, body, SharedApps.Flow);
        return StatusCode(status, answer);
    }
}
