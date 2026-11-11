namespace VantraFlow.Api.Models;

// ── Workspaces as a whole: their look, invitations, leaving, handing over and deleting (db/005) ──

/// <summary>A workspace's look (db/005): its tile colour and its logo (a data URL), either null when unset.</summary>
public sealed record WorkspaceLook(Guid WorkspaceId, string? Color, string? Logo);

/// <summary>An <c>flow.Invites</c> row (db/005). The token itself is never stored: only its SHA-256.</summary>
public sealed class InviteRow
{
    public Guid Id { get; set; }
    public Guid WorkspaceId { get; set; }
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public string Status { get; set; } = "";
    public string InvitedBy { get; set; } = "";
    public string? InvitedByName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? SentAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public string? AcceptedBy { get; set; }
    public DateTime? AcceptedAt { get; set; }
    /// <summary>The workspace's name, colour and logo, where it was joined in.</summary>
    public string? WorkspaceName { get; set; }
    public string? WorkspaceColor { get; set; }
    public string? WorkspaceLogo { get; set; }
}

/// <summary>
/// An invitation as owners and admins see it. <c>Status</c>: pending (not emailed), sent, or expired (open but past
/// its date). <c>Link</c> is there only in the answer that made the link (create and resend): the token is kept
/// hashed, so it can't be shown again.
/// </summary>
public sealed record InviteDto(string Id, string Email, string Role, string Status, string CreatedAt, string ExpiresAt, string? SentAt,
    string? InvitedByName, string? Link = null);

/// <summary>
/// What inviting an address came to: an existing Vantra account is <c>added</c> straight away (<c>Member</c>, and
/// <c>Emailed</c> says whether the note went out); anyone else is <c>invited</c> (<c>Invite</c>, with its link).
/// </summary>
public sealed record InviteResultDto(string Kind, MemberDto? Member = null, InviteDto? Invite = null, bool Emailed = false);

/// <summary>The public preview behind a link: what it's for, with the invited address masked.</summary>
public sealed record InvitePreviewDto(string WorkspaceName, string WorkspaceColor, string? WorkspaceLogo, string Role, string? InvitedByName,
    string EmailHint, string ExpiresAt);

/// <summary>Joined (or already in): the workspace to open.</summary>
public sealed record AcceptInviteDto(string WorkspaceId, string WorkspaceName, bool AlreadyMember);

public sealed record CreateInviteBody(string? Email, string? Role);
public sealed record InviteTokenBody(string? Token);
public sealed record TransferWorkspaceBody(string? VantraId);
public sealed record DeleteWorkspaceBody(string? Confirm);
