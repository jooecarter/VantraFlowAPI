using System.Text.Json.Nodes;

namespace VantraFlow.Api.Models;

// ── Rows ──────────────────────────────────────────────────────────────────

public sealed record WorkspaceRow(Guid Id, string Name, string TimeZone, string OwnerVantraId, int? MonthlyRunQuota, DateTime CreatedAt);

public sealed record MemberRow(Guid WorkspaceId, string VantraId, string Role, string? Email, string? Name, DateTime AddedAt);

public sealed record ConnectionRow(
    Guid Id, Guid WorkspaceId, string App, Guid RemoteWorkspaceId, string RemoteWorkspaceName, string KeyId, byte[] TokenCipher,
    string Status, string? LastError, string ConnectedBy, string? ConnectedByName, DateTime ConnectedAt, DateTime? LastUsedAt);

public sealed record SubscriptionRow(
    string Id, Guid WorkspaceId, Guid ConnectionId, string App, string Scope, string RemoteId, byte[] SecretCipher, string Events,
    DateTime CreatedAt, DateTime? LastEventAt);

public sealed record FlowRow(
    string Id, Guid WorkspaceId, string Name, string? Description, bool Enabled, string Definition, int Version,
    string TriggerApp, string TriggerEvent, string? TriggerScope, string? HookId, byte[]? HookSecretCipher, string? TemplateId,
    string? PausedReason, string CreatedBy, DateTime CreatedAt, string UpdatedBy, DateTime UpdatedAt, DateTime? LastRunAt, string? LastRunStatus);

/// <summary>A saved version of a flow (db/003).</summary>
public sealed record FlowVersionRow(
    Guid WorkspaceId, string FlowId, int Version, string Name, string Definition, int StepCount, string? Note, string SavedBy, string? SavedByName,
    DateTime SavedAt);

public sealed record FlowStatsRow(string FlowId, int Runs, int Failures);

/// <summary>A flow's runs and failures on one day (UTC), for the list's sparkline.</summary>
public sealed record FlowDayRow(string FlowId, DateTime Day, int Runs, int Failures);

/// <summary>The last seven days' runs and failures per flow, oldest day first, today last.</summary>
public static class FlowDays
{
    public const int Count = 7;

    /// <summary>The first day counted: six days before <paramref name="nowUtc"/>'s, at midnight UTC.</summary>
    public static DateTime Since(DateTime nowUtc) => nowUtc.Date.AddDays(-(Count - 1));

    public static Dictionary<string, (int[] Runs, int[] Failures)> Spread(IEnumerable<FlowDayRow> rows, DateTime nowUtc)
    {
        var first = Since(nowUtc);
        var byFlow = new Dictionary<string, (int[] Runs, int[] Failures)>();
        foreach (var r in rows)
        {
            var i = (int)(r.Day.Date - first).TotalDays;
            if (i < 0 || i >= Count) continue;
            if (!byFlow.TryGetValue(r.FlowId, out var days)) byFlow[r.FlowId] = days = (new int[Count], new int[Count]);
            days.Runs[i] += r.Runs;
            days.Failures[i] += r.Failures;
        }
        return byFlow;
    }
}

public sealed record RunRow(
    long Seq, string Id, Guid WorkspaceId, string FlowId, string FlowName, int FlowVersion, string Definition, string TriggerData,
    string? Context, string? Position, int Attempt, string Status, bool Test, string? ReplayOf, string? EventId, int StepCount,
    string? Error, DateTime StartedAt, DateTime? FinishedAt, int? DurationMs, DateTime NotBefore);

public sealed record RunStepRow(
    string RunId, int Seq, string StepId, string Type, string? App, string? StepAction, string Name, string Status, int Attempt,
    string? Input, string? Output, string? Error, DateTime StartedAt, int DurationMs);

public sealed record OAuthConnectionRow(
    Guid Id, Guid WorkspaceId, string Provider, string AccountId, string AccountName, string? AccountEmail, string? TeamId, string? BotUserId,
    string Scopes, byte[] AccessCipher, byte[]? RefreshCipher, DateTime? ExpiresAt, string Status, string? LastError, string ConnectedBy,
    string? ConnectedByName, DateTime ConnectedAt, DateTime? LastUsedAt);

/// <summary>An enabled flow with a polling trigger, and where its poll is up to (null until its first poll).</summary>
public sealed record PollDueRow(
    string FlowId, Guid WorkspaceId, string Provider, string TriggerEvent, string Definition, DateTime UpdatedAt, string? Fingerprint, string? Bookmark,
    DateTime? NextPollAt, int? Failures);

// ── Roles ─────────────────────────────────────────────────────────────────

public static class FlowRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";
    public static bool IsValid(string? r) => r is Owner or Admin or Member;
    /// <summary>Connect apps, manage members and workspace settings.</summary>
    public static bool CanManage(string? r) => r is Owner or Admin;
}

// ── Wire shapes (camelCase) ───────────────────────────────────────────────

/// <summary>
/// A workspace; <c>AlertOnFailure</c> is the failure-alert default, null until db/002 has run (the studio hides it).
/// <c>Color</c> and <c>Logo</c> are its look (db/005): <c>Color</c> is null until db/005 has run, and the studio then
/// hides the look and invitations. <c>MemberCount</c>, <c>CreatedAt</c> and <c>IsPrimaryOwner</c> describe it for the switcher.
/// </summary>
public sealed record WorkspaceDto(Guid Id, string Name, string TimeZone, string Role, int MonthlyRunQuota, int RunsThisMonth, int EmailsToday, int EmailsPerDay,
    bool? AlertOnFailure = null, string? Color = null, string? Logo = null, int MemberCount = 0, string? CreatedAt = null, bool IsPrimaryOwner = false);

public sealed record MemberDto(string VantraId, string? Name, string? Email, string Role, string AddedAt);

public sealed record ConnectionDto(
    string Id, string App, Guid RemoteWorkspaceId, string RemoteWorkspaceName, string Status, string? LastError,
    string? ConnectedByName, string ConnectedAt, string? LastUsedAt, string AppUrl);

public sealed record AppDto(string App, string Name, bool Configured, ConnectionDto? Connection);

public sealed record FlowSummaryDto(
    string Id, string Name, string? Description, bool Enabled, string TriggerApp, string TriggerEvent, IReadOnlyList<string> Apps,
    int StepCount, string? LastRunAt, string? LastRunStatus, int Runs7d, int Failures7d, string UpdatedAt, string? PausedReason, string? TemplateId,
    IReadOnlyList<int>? Daily7d = null, IReadOnlyList<int>? DailyFailures7d = null);

public sealed record FlowDto(
    string Id, string Name, string? Description, bool Enabled, JsonNode Definition, int Version, string? HookUrl, bool HookSigned,
    string? PausedReason, string? TemplateId, string? NextRunAt, string UpdatedAt, string? LastRunAt, string? LastRunStatus,
    FlowAlertsDto? Alerts = null);

/// <summary>
/// A flow's failure alert: whether it emails (<c>On</c>: its own choice, <c>Own</c>, else the workspace's default), and
/// to whom. Null on the flow until db/002 has run.
/// </summary>
public sealed record FlowAlertsDto(bool On, bool? Own, bool WorkspaceDefault, string? ToName, bool ToMe);

public sealed record RunSummaryDto(
    string Id, string FlowId, string FlowName, string Status, bool Test, string? ReplayOf, string StartedAt, string? FinishedAt,
    int? DurationMs, string? Error, int StepCount, string? NextAt, int? FlowVersion = null);

/// <summary>A saved version in the list: <c>Current</c> is the flow as it is now.</summary>
public sealed record FlowVersionDto(int Version, string Name, string SavedAt, string? SavedByName, int StepCount, string? Note, bool Current);

/// <summary>One version, with what it was.</summary>
public sealed record FlowVersionDetailDto(int Version, string Name, string SavedAt, string? SavedByName, string? Note, JsonNode Definition);

public sealed record RunStepDto(
    int Seq, string StepId, string Type, string? App, string? Action, string Name, string Status, int Attempt,
    JsonNode? Input, JsonNode? Output, string? Error, string StartedAt, int DurationMs);

public sealed record RunDetailDto(RunSummaryDto Run, JsonNode? Trigger, IReadOnlyList<RunStepDto> Steps, JsonNode? Definition);

/// <summary>An outside app (Slack, Microsoft 365, Google): set up on this server (<c>Configured</c>), db/004 in place (<c>Ready</c>), and the workspace's connection.</summary>
public sealed record OAuthAppDto(string Provider, string Name, bool Configured, bool Ready, OAuthConnectionDto? Connection);

public sealed record OAuthConnectionDto(
    string Id, string Provider, string AccountName, string? AccountEmail, string Status, string? LastError, string? ConnectedByName, string ConnectedAt,
    string? LastUsedAt, IReadOnlyList<string> Scopes);

// ── Bodies ────────────────────────────────────────────────────────────────

public sealed record CreateWorkspaceBody(string? Name, string? TimeZone, string? Color = null, string? Logo = null);
/// <summary>Left out (null): unchanged. <c>Color</c> or <c>Logo</c> <c>""</c>: back to the default tile / no logo.</summary>
public sealed record UpdateWorkspaceBody(string? Name, string? TimeZone, string? Color = null, string? Logo = null);
public sealed record AddMemberBody(string? Email, string? Role);
public sealed record UpdateMemberBody(string? Role);
public sealed record ConnectBody(Guid? WorkspaceId);
public sealed record SaveFlowBody(string? Name, string? Description, JsonNode? Definition, string? TemplateId);
public sealed record ToggleFlowBody(bool Enabled);
public sealed record TestStepBody(JsonNode? Definition, string? StepId, JsonNode? Data);
public sealed record TestFlowBody(JsonNode? Definition, JsonNode? Trigger);
public sealed record HookSecretBody(bool Signed);
/// <summary>A flow's alert: true (email me), false (never), null (the workspace's default).</summary>
public sealed record FlowAlertsBody(bool? On);
public sealed record WorkspaceAlertsBody(bool On);
public sealed record OAuthFinishBody(string? Id);
