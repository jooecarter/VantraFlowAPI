using System.Text.Json.Nodes;

namespace VantraFlow.Api.Models;

// ── Team and organisation (db/011): folders, tags, owners, comments and the activity log ──

/// <summary>A folder of flows (one level).</summary>
public sealed record FolderRow(string Id, Guid WorkspaceId, string Name, string CreatedBy, DateTime CreatedAt);

/// <summary>A workspace's tag and its colour.</summary>
public sealed record TagRow(string Id, Guid WorkspaceId, string Name, string Color, string CreatedBy, DateTime CreatedAt);

/// <summary>Where a flow is filed and whose it is: <c>OwnerId</c> null means whoever created it.</summary>
public sealed record FlowPlacementRow(string Id, string Name, string? FolderId, string? OwnerId, string CreatedBy, int Comments)
{
    public string Owner => OwnerId ?? CreatedBy;
}

public sealed record FlowTagRow(string FlowId, string TagId);

public sealed record CommentRow(string Id, Guid WorkspaceId, string FlowId, string AuthorId, string? AuthorName, string Body, DateTime CreatedAt, DateTime? EditedAt);

/// <summary>One line of the activity log.</summary>
public sealed record ActivityRow(
    long Id, Guid WorkspaceId, DateTime At, string ActorId, string? ActorName, string Kind, string? FlowId, string? FlowName, int? Version, string? Detail);

/// <summary>Which lines to read: newest first, before <c>Before</c> (an id), at most <c>Limit</c>.</summary>
public sealed record ActivityQuery(string? ActorId = null, string? FlowId = null, string? KindPrefix = null, long? Before = null, int Limit = 50);

/// <summary>An app the workspace has connected (Orbit and Projects, and the outside apps), by name only.</summary>
public sealed record ConnectedAppRow(string App, string Name);

/// <summary>A secret's name (never its value).</summary>
public sealed record SecretNameRow(string Id, string Name);

// ── Wire shapes ──

public sealed record FolderDto(string Id, string Name, string CreatedAt);
public sealed record TagDto(string Id, string Name, string Color);
public sealed record FlowPlacementDto(string FlowId, string? FolderId, string OwnerId, IReadOnlyList<string> TagIds, int Comments);

/// <summary>
/// How a workspace's flows are organised. <c>Can</c>: what the caller may do (the server checks again on every change).
/// </summary>
public sealed record OrganiseDto(IReadOnlyList<FolderDto> Folders, IReadOnlyList<TagDto> Tags, IReadOnlyList<FlowPlacementDto> Flows, TeamCanDto Can);

public sealed record TeamCanDto(bool Edit, bool ManageFolders, bool ManageTags, bool ChangeOwner, bool Audit);

public sealed record CommentDto(string Id, string FlowId, string AuthorId, string? AuthorName, string Body, IReadOnlyList<string> Mentions, string CreatedAt,
    string? EditedAt, bool CanEdit, bool CanDelete);

public sealed record ActivityDto(string Id, string At, string ActorId, string? ActorName, string Kind, string? FlowId, string? FlowName, int? Version, string? Detail);
public sealed record ActivityPageDto(IReadOnlyList<ActivityDto> Items, string? Next);

// ── Bodies ──

public sealed record FolderBody(string? Name);
public sealed record TagBody(string? Name, string? Color);
public sealed record FlowFolderBody(string? FolderId);
public sealed record FlowTagsBody(IReadOnlyList<string>? TagIds);
public sealed record FlowOwnerBody(string? VantraId);
public sealed record CommentBody(string? Body);
public sealed record DuplicateBody(Guid? WorkspaceId, string? Name);
/// <summary>An import: the exported file, and how to map what it needs (secret names to this workspace's secret ids).</summary>
public sealed record ImportBody(JsonNode? Document, string? Name, string? FolderId, IReadOnlyDictionary<string, string?>? Secrets);
