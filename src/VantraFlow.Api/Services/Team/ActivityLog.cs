using VantraFlow.Api.Data;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Team;

/// <summary>What the activity log records (<c>flow.Activity.Kind</c>).</summary>
public static class ActivityKinds
{
    public const string FlowCreated = "flow.created";
    /// <summary>Saved: the version it became is on the line.</summary>
    public const string FlowEdited = "flow.edited";
    public const string FlowEnabled = "flow.enabled";
    public const string FlowDisabled = "flow.disabled";
    public const string FlowRenamed = "flow.renamed";
    public const string FlowDeleted = "flow.deleted";
    public const string FlowMoved = "flow.moved";
    public const string FlowOwner = "flow.owner";
    public const string ConnectionAdded = "connection.added";
    public const string ConnectionRemoved = "connection.removed";
    public const string MemberRole = "member.role";
    public const string SettingsChanged = "settings.changed";
    public const string FolderChanged = "folder.changed";

    public static readonly IReadOnlyList<string> All =
    [
        FlowCreated, FlowEdited, FlowEnabled, FlowDisabled, FlowRenamed, FlowDeleted, FlowMoved, FlowOwner,
        ConnectionAdded, ConnectionRemoved, MemberRole, SettingsChanged, FolderChanged,
    ];

    /// <summary>The filter's types: a group (<c>flow</c>, <c>connection</c>, <c>member</c>, <c>settings</c>, <c>folder</c>) or one kind.</summary>
    public static string? Prefix(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var t = type.Trim().ToLowerInvariant();
        if (All.Contains(t)) return t;
        return t is "flow" or "connection" or "member" or "settings" or "folder" ? t + "." : "";
    }
}

/// <summary>One change to record. <c>Version</c>: a save's version; <c>Detail</c>: a short line (an old name, an app, a role).</summary>
public sealed record ActivityEntry(Guid WorkspaceId, string ActorId, string? ActorName, string Kind, string? FlowId = null, string? FlowName = null,
    int? Version = null, string? Detail = null);

/// <summary>Writes the activity log (<see cref="ActivityLog"/>; a fake in tests).</summary>
public interface IActivityLog
{
    /// <summary>Records a change once it has been made. Never throws, and does nothing until db/011 has run.</summary>
    Task WriteAsync(ActivityEntry entry);
}

/// <summary>The activity log over <see cref="ITeamStore"/>: best effort, so a change never fails for want of its log line.</summary>
public sealed class ActivityLog(ITeamStore store, ILogger<ActivityLog> log) : IActivityLog
{
    public async Task WriteAsync(ActivityEntry e)
    {
        try
        {
            if (!await store.ReadyAsync()) return;
            await store.InsertActivityAsync(new ActivityRow(0, e.WorkspaceId, DateTime.UtcNow, Cut(e.ActorId, 64)!, Cut(e.ActorName, 160), e.Kind,
                Cut(e.FlowId, 40), Cut(e.FlowName, 160), e.Version, Cut(e.Detail, 400)));
        }
        catch (Exception x)
        {
            // Only the kind: never the names or details (personal data).
            log.LogWarning(x, "An activity line ({Kind}) wasn't written", e.Kind);
        }
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
