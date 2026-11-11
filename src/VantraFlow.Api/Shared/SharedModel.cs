namespace VantraFlow.Api.Shared;

// Shared Vantra workspaces, stage 2: members, roles, app access and invitations kept once, in the shared identity
// database (vantra.*), for every app. The same files are in Vantra Projects', Orbit's and Flow's APIs (only the
// namespace differs). Design: VantraProjectsAPI docs/shared-workspaces.md.

/// <summary>The apps a shared workspace can have. Each is sold and billed on its own.</summary>
public static class SharedApps
{
    public const string Projects = "projects";
    public const string Orbit = "orbit";
    public const string Flow = "flow";

    public static readonly string[] All = [Projects, Orbit, Flow];

    public static bool IsValid(string? app) => app is Projects or Orbit or Flow;

    /// <summary>Lower-cased and trimmed, or null when it isn't an app.</summary>
    public static string? Clean(string? app)
    {
        var a = app?.Trim().ToLowerInvariant();
        return IsValid(a) ? a : null;
    }

    public static string Name(string app) => app switch
    {
        Projects => "Vantra Projects",
        Orbit => "Vantra Orbit",
        Flow => "Vantra Flow",
        _ => app,
    };

    /// <summary>The apps given, in the suite's order (Projects, Orbit, Flow), each once.</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> apps)
    {
        var set = apps.Select(Clean).Where(a => a is not null).ToHashSet();
        return All.Where(set.Contains).ToList();
    }

    /// <summary>
    /// The app that counts seats against what was bought: Vantra Projects (seats, client seats, vendor seats). Orbit
    /// and Flow have no seat limits. Giving someone Projects always goes through Projects' own seat check.
    /// </summary>
    public static bool CountsSeats(string app) => app == Projects;
}

/// <summary>One role per person, used in every app: owner &gt; admin &gt; member &gt; guest.</summary>
public static class SharedRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public const string Guest = "guest";

    /// <summary>A guest's kind: Projects' client and vendor seats.</summary>
    public const string Client = "client";
    public const string Vendor = "vendor";

    public static readonly string[] All = [Owner, Admin, Member, Guest];

    public static bool IsValid(string? role) => role is Owner or Admin or Member or Guest;
    public static bool CanManage(string? role) => role is Owner or Admin;
    public static bool IsInvitable(string? role) => role is Admin or Member or Guest;
    public static bool IsGuestType(string? type) => type is Client or Vendor;

    public static string? Clean(string? role)
    {
        var r = role?.Trim().ToLowerInvariant();
        return IsValid(r) ? r : null;
    }

    public static string? CleanGuestType(string? type)
    {
        var t = type?.Trim().ToLowerInvariant();
        return IsGuestType(t) ? t : null;
    }
}

/// <summary>The codes the shared members endpoints answer with, the status each is sent as, and what it says.</summary>
public static class SharedCodes
{
    public const string NotShared = "not_shared";
    public const string NotMember = "not_member";
    public const string NotManager = "not_manager";
    public const string OwnerOnly = "owner_only";
    public const string NotPrimary = "not_primary";
    public const string LastOwner = "last_owner";
    public const string PrimaryOwner = "primary_owner";
    public const string Self = "self";
    public const string SelfChange = "self_change";
    public const string GuestChange = "guest_change";
    public const string GuestFlow = "guest_flow";
    public const string AppOff = "app_off";
    public const string NoApps = "no_apps";
    public const string TargetGuest = "target_guest";
    public const string TargetApps = "target_apps";
    public const string InvalidRole = "invalid_role";
    public const string InvalidApp = "invalid_app";
    public const string InvalidEmail = "invalid_email";
    public const string AlreadyMember = "already_member";
    public const string InviteLimit = "invite_limit";
    public const string InviteInvalid = "invite_invalid";
    public const string SeatsFull = "seats_full";
    public const string NotReady = "not_ready";
    public const string Unavailable = "unavailable";

    public static int StatusFor(string code) => code switch
    {
        NotShared or NotMember or InviteInvalid => StatusCodes.Status404NotFound,
        NotManager or OwnerOnly or NotPrimary => StatusCodes.Status403Forbidden,
        Self or SelfChange or InvalidRole or InvalidApp or InvalidEmail or NoApps => StatusCodes.Status400BadRequest,
        SeatsFull => StatusCodes.Status402PaymentRequired,
        InviteLimit => StatusCodes.Status429TooManyRequests,
        NotReady or Unavailable => StatusCodes.Status503ServiceUnavailable,
        _ => StatusCodes.Status409Conflict,
    };

    public static string MessageFor(string code) => code switch
    {
        NotShared => "This workspace's members aren't shared across the Vantra apps yet.",
        NotMember => "That person isn't in this workspace.",
        NotManager => "Only owners and admins can do that.",
        OwnerOnly => "Only an owner can make someone an owner, or change an owner.",
        NotPrimary => "Only the workspace's main owner can hand it over.",
        LastOwner => "A workspace needs at least one owner.",
        PrimaryOwner => "The main owner keeps every app and can't be removed. Hand the workspace over first.",
        Self => "To leave, use Leave workspace.",
        SelfChange => "You can't change your own role or remove your own access.",
        GuestChange => "Guests stay guests. To make someone a teammate, remove them and invite them again.",
        GuestFlow => "Guests can't use Vantra Flow.",
        AppOff => "This workspace doesn't have that app.",
        NoApps => "Choose at least one app.",
        TargetGuest => "A workspace can only be handed to a teammate, not a guest.",
        TargetApps => "Give them every app this workspace has first.",
        InvalidRole => "Choose admin, member or guest.",
        InvalidApp => "That isn't a Vantra app.",
        InvalidEmail => "Enter a valid email address.",
        AlreadyMember => "They're already in this workspace.",
        InviteLimit => "This workspace has invited 100 people today. Try again tomorrow.",
        InviteInvalid => "That invitation isn't open any more.",
        SeatsFull => "There's no seat free for that.",
        NotReady => "Shared members arrive with the next server update.",
        Unavailable => "Another Vantra app couldn't be reached. Nothing was changed. Try again.",
        _ => "That change isn't allowed.",
    };
}

/// <summary>An app's link to a shared workspace: its own (local) workspace id, and whether the workspace has it.</summary>
public sealed record SharedLink(string App, Guid LocalWorkspaceId, bool Enabled);

/// <summary>A person in a shared workspace: their one role, and the apps they may use there.</summary>
public sealed record SharedMember(string VantraId, string Role, string? GuestType, DateTime JoinedAt, IReadOnlyList<string> Apps)
{
    public bool Has(string app) => Apps.Contains(app, StringComparer.OrdinalIgnoreCase);
}

/// <summary>A shared workspace as the rules see it: its main owner, its apps and its members.</summary>
public sealed record SharedState(Guid WorkspaceId, string Name, string PrimaryOwner, IReadOnlyList<SharedLink> Links, IReadOnlyList<SharedMember> Members)
{
    /// <summary>The apps the workspace has (enabled links), in the suite's order.</summary>
    public IReadOnlyList<string> Apps => SharedApps.Order(Links.Where(l => l.Enabled).Select(l => l.App));

    public bool HasApp(string app) => Links.Any(l => l.Enabled && l.App == app);

    public Guid? LocalId(string app) => Links.FirstOrDefault(l => l.Enabled && l.App == app)?.LocalWorkspaceId;

    public SharedMember? Find(string? vantraId) =>
        string.IsNullOrWhiteSpace(vantraId) ? null
        : Members.FirstOrDefault(m => string.Equals(m.VantraId, vantraId.Trim(), StringComparison.OrdinalIgnoreCase));

    public bool IsPrimary(string? vantraId) => string.Equals(vantraId?.Trim(), PrimaryOwner, StringComparison.OrdinalIgnoreCase);

    public int OwnerCount => Members.Count(m => m.Role == SharedRoles.Owner);

    /// <summary>
    /// Whether this app takes its members from here: the workspace has the app and at least one person has access to
    /// it. Until the backfill gives someone access, the app keeps its own member list, exactly as before.
    /// </summary>
    public bool MembersShared(string app) => HasApp(app) && Members.Any(m => m.Has(app));
}

/// <summary>A new member, or a member's new role.</summary>
public sealed record SharedChange(string VantraId, string Role, string? GuestType);

/// <summary>One person's access to one app.</summary>
public sealed record SharedAccess(string VantraId, string App);

/// <summary>
/// What a change comes to: an error code, or the members to add, the roles to change, who to remove, the access to
/// give and take away, and the main owner afterwards.
/// </summary>
public sealed record SharedPlan(
    string? Error,
    IReadOnlyList<SharedChange> Adds,
    IReadOnlyList<SharedChange> Changes,
    IReadOnlyList<string> Removals,
    IReadOnlyList<SharedAccess> Grants,
    IReadOnlyList<SharedAccess> Revokes,
    string PrimaryOwner,
    string? Target = null)
{
    public bool Ok => Error is null;
    public bool Empty => Adds.Count == 0 && Changes.Count == 0 && Removals.Count == 0 && Grants.Count == 0 && Revokes.Count == 0;

    public static SharedPlan Fail(string code, SharedState s) => new(code, [], [], [], [], [], s.PrimaryOwner);
    public static SharedPlan None(SharedState s, string? target = null) => new(null, [], [], [], [], [], s.PrimaryOwner, target);
}

/// <summary>A <c>vantra.WorkspaceInvites</c> row with its apps. Only the token's SHA-256 is ever stored.</summary>
public sealed class SharedInviteRow
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public string? GuestType { get; set; }
    public string Status { get; set; } = "";
    public string InvitedByVantraId { get; set; } = "";
    public string? InvitedFromApp { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? AcceptedByVantraId { get; set; }
    public string? WorkspaceName { get; set; }
    public string? WorkspaceColor { get; set; }
    public string? WorkspaceLogo { get; set; }
    public IReadOnlyList<string> Apps { get; set; } = [];
}

/// <summary>Who did something, for the audit columns and the email.</summary>
public sealed record SharedActor(string VantraId, string Name, string? Email);

/// <summary>A person's name and email from the identity database.</summary>
public sealed record SharedPerson(string Name, string Email);

// ── Wire shapes ──

public sealed record SharedAppDto(string App, string Name);

public sealed record SharedMeDto(string VantraId, string Role, bool Primary, bool CanManage);

public sealed record SharedMemberDto(string VantraId, string Name, string? Email, string Role, string? GuestType, bool Primary,
    IReadOnlyList<string> Apps, string JoinedAt);

public sealed record SharedInviteDto(string Id, string Email, string Role, string? GuestType, IReadOnlyList<string> Apps, string Status,
    string CreatedAt, string ExpiresAt, string? SentAt, string? InvitedByName, string? Link = null);

/// <summary>
/// The Members page for a shared workspace: everyone in it, with their role and the apps they may use; the apps the
/// workspace has; and, for owners and admins, its open invitations.
/// </summary>
public sealed record SharedTeamDto(string WorkspaceId, string WorkspaceName, string App, IReadOnlyList<SharedAppDto> Apps, SharedMeDto Me,
    IReadOnlyList<SharedMemberDto> Members, IReadOnlyList<SharedInviteDto> Invites);

public sealed record SharedInvitePreviewDto(string WorkspaceName, string? WorkspaceColor, string? WorkspaceLogo, string Role, string? GuestType,
    IReadOnlyList<SharedAppDto> Apps, string? InvitedByName, string EmailHint, string ExpiresAt);

/// <summary>Joined (or already in): the workspace, this app's own id for it, the apps they may use now, and the apps invited to.</summary>
public sealed record SharedAcceptDto(string WorkspaceId, string WorkspaceName, Guid? LocalWorkspaceId, bool AlreadyMember, IReadOnlyList<string> Apps,
    IReadOnlyList<string> Invited);

public sealed record SharedInviteBody(string? Email, string? Role, string? GuestType, IReadOnlyList<string>? Apps);
public sealed record SharedRoleBody(string? Role, string? GuestType);
public sealed record SharedTransferBody(string? VantraId);
public sealed record SharedTokenBody(string? Token);

/// <summary>An answer to send: a status and a body (null for 204).</summary>
public sealed record SharedResult(int Status, object? Body)
{
    public bool Ok => Status is >= 200 and < 300;
    public static SharedResult NoContent { get; } = new(StatusCodes.Status204NoContent, null);
    public static SharedResult Okay(object body) => new(StatusCodes.Status200OK, body);
    public static SharedResult Error(string code, string? message = null, object? extra = null) =>
        new(SharedCodes.StatusFor(code), extra ?? new { error = message ?? SharedCodes.MessageFor(code), code });
}
