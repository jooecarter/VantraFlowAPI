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

public sealed record FlowStatsRow(string FlowId, int Runs, int Failures);

public sealed record RunRow(
    long Seq, string Id, Guid WorkspaceId, string FlowId, string FlowName, int FlowVersion, string Definition, string TriggerData,
    string? Context, string? Position, int Attempt, string Status, bool Test, string? ReplayOf, string? EventId, int StepCount,
    string? Error, DateTime StartedAt, DateTime? FinishedAt, int? DurationMs, DateTime NotBefore);

public sealed record RunStepRow(
    string RunId, int Seq, string StepId, string Type, string? App, string? StepAction, string Name, string Status, int Attempt,
    string? Input, string? Output, string? Error, DateTime StartedAt, int DurationMs);

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

/// <summary>A workspace; <c>AlertOnFailure</c> is the failure-alert default, null until db/002 has run (the studio hides it).</summary>
public sealed record WorkspaceDto(Guid Id, string Name, string TimeZone, string Role, int MonthlyRunQuota, int RunsThisMonth, int EmailsToday, int EmailsPerDay,
    bool? AlertOnFailure = null);

public sealed record MemberDto(string VantraId, string? Name, string? Email, string Role, string AddedAt);

public sealed record ConnectionDto(
    string Id, string App, Guid RemoteWorkspaceId, string RemoteWorkspaceName, string Status, string? LastError,
    string? ConnectedByName, string ConnectedAt, string? LastUsedAt, string AppUrl);

public sealed record AppDto(string App, string Name, bool Configured, ConnectionDto? Connection);

public sealed record FlowSummaryDto(
    string Id, string Name, string? Description, bool Enabled, string TriggerApp, string TriggerEvent, IReadOnlyList<string> Apps,
    int StepCount, string? LastRunAt, string? LastRunStatus, int Runs7d, int Failures7d, string UpdatedAt, string? PausedReason, string? TemplateId);

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
    int? DurationMs, string? Error, int StepCount, string? NextAt);

public sealed record RunStepDto(
    int Seq, string StepId, string Type, string? App, string? Action, string Name, string Status, int Attempt,
    JsonNode? Input, JsonNode? Output, string? Error, string StartedAt, int DurationMs);

public sealed record RunDetailDto(RunSummaryDto Run, JsonNode? Trigger, IReadOnlyList<RunStepDto> Steps, JsonNode? Definition);

// ── Bodies ────────────────────────────────────────────────────────────────

public sealed record CreateWorkspaceBody(string? Name, string? TimeZone);
public sealed record UpdateWorkspaceBody(string? Name, string? TimeZone);
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
