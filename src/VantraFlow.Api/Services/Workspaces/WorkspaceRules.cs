using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Workspaces;

/// <summary>The error codes the workspace rules answer with (and the status each is sent as).</summary>
public static class WorkspaceCodes
{
    public const string NotMember = "not_member";
    public const string NotManager = "not_manager";
    public const string OwnerOnly = "owner_only";
    public const string LastOwner = "last_owner";
    public const string Self = "self";
    public const string WorkspaceLimit = "workspace_limit";
    public const string InvalidLogo = "invalid_logo";
    public const string InvalidColor = "invalid_color";
    public const string ConfirmMismatch = "confirm_mismatch";
    public const string AlreadyMember = "already_member";
    public const string InviteLimit = "invite_limit";
    public const string NotReady = "not_ready";

    public static int StatusFor(string code) => code switch
    {
        NotMember => StatusCodes.Status404NotFound,
        NotManager or OwnerOnly => StatusCodes.Status403Forbidden,
        Self or InvalidLogo or InvalidColor or ConfirmMismatch => StatusCodes.Status400BadRequest,
        InviteLimit => StatusCodes.Status429TooManyRequests,
        NotReady => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict, // last_owner, workspace_limit, already_member
    };

    public static string MessageFor(string code) => code switch
    {
        NotMember => "That person isn't in this workspace.",
        NotManager => "Only owners and admins can do that.",
        OwnerOnly => "Only an owner can make someone an owner, or change or remove an owner.",
        LastOwner => "A workspace needs at least one owner. Make someone else an owner first.",
        Self => "To leave, use Leave workspace.",
        _ => "That change isn't allowed.",
    };
}

/// <summary>One member as the rules see them.</summary>
public sealed record MemberState(string VantraId, string Role, DateTime AddedAt);

/// <summary>A workspace's members and its primary owner (<c>flow.Workspaces.OwnerVantraId</c>), read under a lock.</summary>
public sealed record WorkspaceState(IReadOnlyList<MemberState> Members, string PrimaryOwner)
{
    public MemberState? Find(string? vantraId) =>
        string.IsNullOrWhiteSpace(vantraId) ? null : Members.FirstOrDefault(m => string.Equals(m.VantraId, vantraId.Trim(), StringComparison.OrdinalIgnoreCase));
    public int OwnerCount => Members.Count(m => m.Role == FlowRoles.Owner);
}

/// <summary>
/// What a membership change comes to: an error code, or the role changes and removals to make, the primary owner
/// after them, and whom it was about.
/// </summary>
public sealed record MemberPlan(string? Error, IReadOnlyList<MemberState> Changes, IReadOnlyList<string> Removals, string PrimaryOwner, string? Target = null)
{
    public bool Ok => Error is null;
    public static MemberPlan Fail(string code, string primary) => new(code, [], [], primary);
}

/// <summary>
/// The rules for who may change a workspace's members, as Vantra Orbit's <c>WorkspaceRules</c> with Flow's roles
/// (owner | admin | member). Pure: the repository reads the state under a lock and applies the plan.
/// <list type="bullet">
/// <item>Owners and admins manage members; only an owner makes someone an owner or changes or removes an owner.</item>
/// <item>There is always at least one owner: the last one can't be demoted, removed or leave.</item>
/// <item>Removing yourself is leaving (its own rule).</item>
/// <item>Handing over (owners only): the member named becomes an owner and the primary owner; you become an admin.</item>
/// <item>Deleting the workspace and handing it over take an owner; inviting, renaming and the look take a manager.</item>
/// </list>
/// </summary>
public static class WorkspaceRules
{
    public const int NameMin = 2;
    public const int NameMax = 80;
    public const int DefaultMaxOwned = 20;
    /// <summary>Flow's green: the tile colour of a workspace that hasn't chosen one.</summary>
    public const string DefaultColor = "#12855d";

    public static bool CanManage(string? role) => FlowRoles.CanManage(role);
    public static bool CanInvite(string? role) => FlowRoles.CanManage(role);
    public static bool CanDelete(string? role) => role == FlowRoles.Owner;
    public static bool CanTransfer(string? role) => role == FlowRoles.Owner;

    /// <summary>Roles an invitation or an add can carry: not owner (make them one once they've joined).</summary>
    public static bool IsInvitableRole(string? role) => role is FlowRoles.Admin or FlowRoles.Member;

    public static MemberPlan SetRole(WorkspaceState s, string callerId, string targetId, string role)
    {
        var caller = s.Find(callerId);
        if (caller is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (!CanManage(caller.Role)) return MemberPlan.Fail(WorkspaceCodes.NotManager, s.PrimaryOwner);
        var target = s.Find(targetId);
        if (target is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (caller.Role != FlowRoles.Owner && (role == FlowRoles.Owner || target.Role == FlowRoles.Owner))
            return MemberPlan.Fail(WorkspaceCodes.OwnerOnly, s.PrimaryOwner);
        if (target.Role == role) return new MemberPlan(null, [], [], s.PrimaryOwner, target.VantraId);
        if (target.Role == FlowRoles.Owner && s.OwnerCount <= 1)
            return MemberPlan.Fail(WorkspaceCodes.LastOwner, s.PrimaryOwner);
        var change = target with { Role = role };
        return new MemberPlan(null, [change], [], NextPrimary(s, [change], []), target.VantraId);
    }

    public static MemberPlan Remove(WorkspaceState s, string callerId, string targetId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (!CanManage(caller.Role)) return MemberPlan.Fail(WorkspaceCodes.NotManager, s.PrimaryOwner);
        var target = s.Find(targetId);
        if (target is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (string.Equals(target.VantraId, caller.VantraId, StringComparison.OrdinalIgnoreCase))
            return MemberPlan.Fail(WorkspaceCodes.Self, s.PrimaryOwner);
        if (target.Role == FlowRoles.Owner && caller.Role != FlowRoles.Owner)
            return MemberPlan.Fail(WorkspaceCodes.OwnerOnly, s.PrimaryOwner);
        if (target.Role == FlowRoles.Owner && s.OwnerCount <= 1)
            return MemberPlan.Fail(WorkspaceCodes.LastOwner, s.PrimaryOwner);
        return new MemberPlan(null, [], [target.VantraId], NextPrimary(s, [], [target.VantraId]), target.VantraId);
    }

    public static MemberPlan Leave(WorkspaceState s, string callerId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (caller.Role == FlowRoles.Owner && s.OwnerCount <= 1)
            return MemberPlan.Fail(WorkspaceCodes.LastOwner, s.PrimaryOwner);
        return new MemberPlan(null, [], [caller.VantraId], NextPrimary(s, [], [caller.VantraId]), caller.VantraId);
    }

    public static MemberPlan Transfer(WorkspaceState s, string callerId, string? targetId)
    {
        var caller = s.Find(callerId);
        if (caller is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        if (!CanTransfer(caller.Role)) return MemberPlan.Fail(WorkspaceCodes.OwnerOnly, s.PrimaryOwner);
        if (string.Equals(targetId?.Trim(), caller.VantraId, StringComparison.OrdinalIgnoreCase))
            return MemberPlan.Fail(WorkspaceCodes.Self, s.PrimaryOwner);
        var target = s.Find(targetId);
        if (target is null) return MemberPlan.Fail(WorkspaceCodes.NotMember, s.PrimaryOwner);
        var changes = new List<MemberState> { caller with { Role = FlowRoles.Admin } };
        if (target.Role != FlowRoles.Owner) changes.Add(target with { Role = FlowRoles.Owner });
        return new MemberPlan(null, changes, [], target.VantraId, target.VantraId);
    }

    /// <summary>
    /// The primary owner after the changes: unchanged while still an owner, else the longest-standing owner left.
    /// </summary>
    public static string NextPrimary(WorkspaceState s, IReadOnlyList<MemberState> changes, IReadOnlyList<string> removals)
    {
        var after = s.Members
            .Where(m => !removals.Contains(m.VantraId, StringComparer.OrdinalIgnoreCase))
            .Select(m => changes.FirstOrDefault(c => string.Equals(c.VantraId, m.VantraId, StringComparison.OrdinalIgnoreCase)) ?? m)
            .ToList();
        if (after.Any(m => m.Role == FlowRoles.Owner && string.Equals(m.VantraId, s.PrimaryOwner, StringComparison.OrdinalIgnoreCase)))
            return s.PrimaryOwner;
        return after.Where(m => m.Role == FlowRoles.Owner).OrderBy(m => m.AddedAt).ThenBy(m => m.VantraId, StringComparer.Ordinal)
            .FirstOrDefault()?.VantraId ?? s.PrimaryOwner;
    }

    /// <summary><c>Workspaces:MaxOwned</c>, default 20: how many workspaces one person may be the primary owner of.</summary>
    public static int MaxOwned(IConfiguration config) =>
        int.TryParse(config["Workspaces:MaxOwned"], out var n) && n >= 1 ? n : DefaultMaxOwned;

    /// <summary>A workspace name: trimmed, 2–80 characters. Null when it isn't one.</summary>
    public static string? CleanName(string? name)
    {
        var n = name?.Trim();
        return n is { Length: >= NameMin and <= NameMax } && !n.Any(char.IsControl) ? n : null;
    }

    /// <summary>A colour: <c>#rrggbb</c>, stored lower-case. Null when it isn't one.</summary>
    public static string? CleanColor(string? color)
    {
        var c = color?.Trim();
        if (c is not { Length: 7 } || c[0] != '#') return null;
        for (var i = 1; i < 7; i++)
            if (!char.IsAsciiHexDigit(c[i])) return null;
        return c.ToLowerInvariant();
    }

    /// <summary>Deleting asks for the workspace's name, typed: the same text, ignoring surrounding space.</summary>
    public static bool ConfirmMatches(string workspaceName, string? typed) =>
        typed is not null && string.Equals(workspaceName.Trim(), typed.Trim(), StringComparison.Ordinal);
}
