using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The studio's endpoints: signed in (Flow's audience, <c>vantra-flow</c>), db/001 in place, and — under
/// <c>api/w/{workspaceId}</c> — a member of that workspace. Owners and admins alone connect apps and manage the
/// workspace (<see cref="ManageAsync"/>).
/// </summary>
[ApiController]
[Authorize]
[ServiceFilter(typeof(FlowReadyFilter))]
public abstract class FlowControllerBase(IWorkspaceStore workspaces) : ControllerBase
{
    protected string Sub => User.FindFirstValue("sub") ?? "";
    protected string? Email => User.FindFirstValue("email");
    /// <summary>This session passed two-step sign-in (<c>amr: otp</c>).</summary>
    protected bool PassedSecondStep => User.FindAll("amr").Any(c => c.Value == Services.Connectors.AppApi.SecondStepAmr);
    protected string DisplayName
    {
        get
        {
            var n = $"{User.FindFirstValue("given_name")} {User.FindFirstValue("family_name")}".Trim();
            return n.Length > 0 ? n : Email ?? "Someone";
        }
    }

    protected IWorkspaceStore Workspaces => workspaces;

    /// <summary>The caller's role in the workspace, or the refusal to return (404: not theirs to see).</summary>
    protected async Task<(string? Role, ActionResult? Refusal)> MemberAsync(Guid workspaceId)
    {
        var role = await workspaces.RoleAsync(workspaceId, Sub);
        return role is null ? (null, NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" })) : (role, null);
    }

    /// <summary>As <see cref="MemberAsync"/>, and an owner or admin (403 otherwise).</summary>
    protected async Task<(string? Role, ActionResult? Refusal)> ManageAsync(Guid workspaceId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return (null, refusal);
        return FlowRoles.CanManage(role) ? (role, null)
            : (null, StatusCode(403, new { error = "Only owners and admins can do that.", code = "not_manager" }));
    }

    public static string Iso(DateTime d) => DateTime.SpecifyKind(d, DateTimeKind.Utc).ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    public static string? Iso(DateTime? d) => d is { } v ? Iso(v) : null;
}

/// <summary>503 <c>not_ready</c> until db/001 has run (sign-in and health work before).</summary>
public sealed class FlowReadyFilter(FlowSchema schema) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        bool ready;
        try { ready = await schema.ReadyAsync(); }
        catch (Exception) { ready = false; }
        if (!ready)
        {
            context.Result = new ObjectResult(new { error = "Vantra Flow isn't set up on this server yet.", code = "not_ready" })
                { StatusCode = StatusCodes.Status503ServiceUnavailable };
            return;
        }
        await next();
    }
}
