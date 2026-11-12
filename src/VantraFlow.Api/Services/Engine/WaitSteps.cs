using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// A delay's two modes. <c>{ amount, unit }</c> (or <c>mode: "for"</c>) waits that long; <c>{ mode: "until", at, zone }</c>
/// waits until a date and time (<c>at</c> may be a token), read in <c>zone</c> when it has no offset (else the workspace's
/// time zone). A time already past carries straight on; one more than <see cref="FlowLimits.MaxDelay"/> ahead fails the step.
/// Durable either way: the run is saved as waiting, with <c>NotBefore</c> at the time, and the worker picks it up then.
/// </summary>
public static class DelayRules
{
    public static bool IsUntil(JsonObject config) => Tokens.Text(config["mode"]) == "until";

    /// <summary>What's wrong with an "until" delay before it can run (null: nothing).</summary>
    public static string? CheckUntil(JsonObject config)
    {
        var at = Tokens.Text(config["at"]).Trim();
        if (at.Length == 0) return "Choose when to carry on.";
        var zone = Tokens.Text(config["zone"]).Trim();
        if (zone.Length > 0 && !zone.Contains("{{", StringComparison.Ordinal) && Formatter.FindZone(zone) is null) return $"“{zone}” isn't a time zone.";
        if (!at.Contains("{{", StringComparison.Ordinal) && Formatter.ParseDate(at, "auto", TimeZoneInfo.Utc) is null) return $"“{at}” isn't a date and time.";
        return null;
    }

    /// <summary>The time an "until" delay (its tokens filled in) waits for, in UTC; null when it isn't a date.</summary>
    public static DateTime? UntilOf(JsonObject rendered, string? workspaceZone)
    {
        var zoneId = Tokens.Text(rendered["zone"]).Trim();
        var zone = Formatter.FindZone(zoneId.Length > 0 ? zoneId : workspaceZone ?? "UTC") ?? TimeZoneInfo.Utc;
        return Formatter.ParseDate(Tokens.Text(rendered["at"]), "auto", zone);
    }
}

/// <summary>
/// "Wait until": <c>{ condition, every, amount, unit, refresh, onTimeout }</c>. The condition is checked when the step is
/// reached, then every <c>every</c> minutes (<see cref="MinEvery"/>–<see cref="MaxEvery"/>, default 15) until it holds or
/// <c>amount</c> <c>unit</c> have passed (at most 30 days, default 3 days). <c>refresh</c> names an earlier action step
/// (a Find records, a Stripe lookup, an HTTP GET) run again before each check, so the condition can read fresh data from
/// it. Its output: <c>{ met, timedOut, checks }</c>. When time runs out the run carries on (<c>onTimeout: continue</c>,
/// the default: later steps can branch on <c>met</c>) or stops there as a success (<c>stop</c>). A test checks once.
/// </summary>
public static class WaitRules
{
    public const int MinEvery = 5;
    public const int MaxEvery = 1440;
    public const int DefaultEvery = 15;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromDays(3);

    public static IEnumerable<string> Check(JsonObject config, IReadOnlyCollection<string> earlier)
    {
        if (Conditions.Parse(config["condition"]) is not { Count: > 0 }) yield return "Add a condition to wait for.";
        if (config["every"] is { } e && !DefinitionRules.Empty(e)
            && !(Formatter.ToNumber(e) is { } every && every >= MinEvery && every <= MaxEvery))
            yield return $"Check every {MinEvery} to {MaxEvery} minutes.";
        if (!DefinitionRules.Empty(config["amount"]))
        {
            if (DefinitionRules.DelayOf(config) is not { } t) yield return "Set how long to wait at most.";
            else if (t > FlowLimits.MaxDelay) yield return "It can wait at most 30 days.";
        }
        if (RefreshOf(config) is { } r && !earlier.Contains(r)) yield return "The step to run again must come before this one.";
    }

    public static TimeSpan EveryOf(JsonObject config) =>
        TimeSpan.FromMinutes(Formatter.ToNumber(config["every"]) is { } n ? Math.Clamp(n, MinEvery, MaxEvery) : DefaultEvery);

    public static TimeSpan TimeoutOf(JsonObject config) =>
        DefinitionRules.DelayOf(config) is { } t && t <= FlowLimits.MaxDelay ? t : DefaultTimeout;

    public static string? RefreshOf(JsonObject config) => Tokens.Text(config["refresh"]).Trim() is { Length: > 0 } r ? r : null;

    public static bool Stops(JsonObject config) => Tokens.Text(config["onTimeout"]) == "stop";
}

/// <summary>
/// "Approval": <c>{ title, details, approvers: [vantraId…], amount, unit }</c> and three fixed paths, in this order:
/// Approved, Rejected and Timed out (<see cref="StepDef.Paths"/>, no conditions). The approvers (workspace members, up to
/// <see cref="MaxApprovers"/>) are emailed signed, single-use Approve and Reject links and see it in the studio's approval
/// inbox; the first decision counts. The run waits (durably) for it, at most <c>amount</c> <c>unit</c> (default 3 days,
/// at most 30), then takes the matching path. Its output: <c>{ decision, decidedBy, decidedAt, comment }</c>. A test
/// takes Approved without asking anyone. Not inside a Repeat for each.
/// </summary>
public static class ApprovalRules
{
    public const int MaxApprovers = 10;
    public const int Approved = 0, Rejected = 1, TimedOut = 2;
    public static readonly string[] PathNames = ["Approved", "Rejected", "Timed out"];
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromDays(3);

    public static IEnumerable<string> Check(StepDef s)
    {
        if (Tokens.Text(s.Config["title"]).Trim().Length == 0) yield return "Say what needs approving.";
        var who = ApproversOf(s.Config);
        if (who.Count == 0) yield return "Choose who can approve.";
        else if (who.Count > MaxApprovers) yield return $"Choose at most {MaxApprovers} people.";
        if (!DefinitionRules.Empty(s.Config["amount"]))
        {
            if (DefinitionRules.DelayOf(s.Config) is not { } t) yield return "Set how long to wait for a decision.";
            else if (t > FlowLimits.MaxDelay) yield return "It can wait at most 30 days for a decision.";
        }
    }

    /// <summary>Its three sides, made when missing, in path order.</summary>
    public static List<List<StepDef>> Sides(StepDef s)
    {
        s.Paths ??= [];
        for (var i = s.Paths.Count; i < PathNames.Length; i++) s.Paths.Add(new PathDef { Name = PathNames[i] });
        return s.Paths.Take(PathNames.Length).Select(p => p.Steps ??= []).ToList();
    }

    public static IReadOnlyList<string> ApproversOf(JsonObject config) =>
        config["approvers"] is JsonArray a ? a.Select(Tokens.Text).Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList() : [];

    public static TimeSpan TimeoutOf(JsonObject config) =>
        DefinitionRules.DelayOf(config) is { } t && t <= FlowLimits.MaxDelay ? t : DefaultTimeout;

    /// <summary>The path a decision takes.</summary>
    public static int PathOf(string decision) => decision switch { "approved" => Approved, "rejected" => Rejected, _ => TimedOut };
}

/// <summary>An approval to ask for: the run and step it's for, what it says, who may decide, and until when.</summary>
public sealed record ApprovalAsk(Guid WorkspaceId, string FlowId, string RunId, string StepId, string Title, string? Details, IReadOnlyList<string> Approvers, DateTime ExpiresAt);

/// <summary>Where an approval is: <c>pending</c>, <c>approved</c>, <c>rejected</c>, <c>expired</c> or <c>cancelled</c>, and who decided.</summary>
public sealed record ApprovalState(
    string Id, string Status, string? DecidedByName, DateTime? DecidedAt, string? Comment, string? Via, IReadOnlyList<string> ApproverNames, DateTime ExpiresAt,
    int Notified = 0);

/// <summary>The approval can't be asked for (no approvers left in the workspace, db/007 not run): the step fails with why.</summary>
public sealed class ApprovalUnavailableException(string message) : Exception(message);

/// <summary>Asks for approvals and reads their decisions (<see cref="Connectors.ApprovalGate"/>; tests use a fake).</summary>
public interface IApprovalGate
{
    /// <summary>Records the request and notifies the approvers; the one already made when asked again for the same run and step.</summary>
    Task<ApprovalState> RequestAsync(ApprovalAsk ask, CancellationToken ct);
    /// <summary>The request now; null when it's gone.</summary>
    Task<ApprovalState?> StateAsync(Guid workspaceId, string approvalId, CancellationToken ct);
    /// <summary>A pending request whose time ran out.</summary>
    Task ExpireAsync(string approvalId, DateTime now);
}
