using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// A flow as the studio saves it: one trigger, then steps run top to bottom. A step is an action (an app's
/// operation), a delay, or a branch (if / else on a condition, each side its own list of steps). Configs are
/// JSON objects whose strings may hold tokens — <c>{{trigger.record.fields.Email}}</c>,
/// <c>{{steps.s2.id}}</c> — filled in from the trigger and earlier steps' outputs when the step runs.
/// <para>Adapted from Vantra Orbit's automation definition (AutoDefinition), which is one table's trigger and a
/// flat list of actions; here triggers and actions come from any connected app and steps feed each other.</para>
/// </summary>
public sealed class FlowDefinition
{
    public TriggerDef Trigger { get; set; } = new();
    public List<StepDef> Steps { get; set; } = [];
    public FlowOptions Options { get; set; } = new();

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static FlowDefinition? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<FlowDefinition>(json, Json); }
        catch (JsonException) { return null; }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Every step, depth first in the order they run (a branch, then its "then" side, then its "else").</summary>
    public IEnumerable<StepDef> AllSteps() => Walk(Steps);

    private static IEnumerable<StepDef> Walk(IEnumerable<StepDef> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            if (s.Then is not null) foreach (var t in Walk(s.Then)) yield return t;
            if (s.Else is not null) foreach (var e in Walk(s.Else)) yield return e;
        }
    }
}

public sealed class TriggerDef
{
    /// <summary><c>orbit</c>, <c>projects</c>, <c>schedule</c> or <c>webhook</c>.</summary>
    public string App { get; set; } = "";
    /// <summary>The app's event, e.g. <c>record.created</c> (<see cref="FlowCatalog.Triggers"/>).</summary>
    public string Event { get; set; } = "";
    public JsonObject Config { get; set; } = new();
}

public sealed class StepDef
{
    public string Id { get; set; } = "";
    /// <summary><c>action</c>, <c>delay</c> or <c>branch</c>.</summary>
    public string Type { get; set; } = StepTypes.Action;
    public string? App { get; set; }
    public string? Action { get; set; }
    public string? Name { get; set; }
    public JsonObject Config { get; set; } = new();
    public List<StepDef>? Then { get; set; }
    public List<StepDef>? Else { get; set; }
    public RetryDef? Retry { get; set; }
}

/// <summary>How often a failed action is tried again, and the first wait (it doubles each time).</summary>
public sealed class RetryDef
{
    public int Max { get; set; } = 2;
    public int BackoffSeconds { get; set; } = 30;
}

public sealed class FlowOptions
{
    /// <summary>
    /// Off (the default): a change Flow itself made through this workspace's connection doesn't start this flow,
    /// so a flow that writes to an Orbit table can't trigger itself in a loop. On: it does, and the loop breaker
    /// (<c>Flow:FlowRunsPerMinute</c>) still pauses a flow that runs away.
    /// </summary>
    public bool RunOnFlowChanges { get; set; }
}

public static class StepTypes
{
    public const string Action = "action";
    public const string Delay = "delay";
    public const string Branch = "branch";
}

/// <summary>A problem with a definition, for the studio to show next to the step (or the trigger: StepId null).</summary>
public sealed record DefinitionError(string? StepId, string Message);

/// <summary>Limits a definition is held to (config <c>Flow:*</c>).</summary>
public sealed record FlowLimits(
    int MaxSteps = 25,
    int MaxDepth = 3,
    int StepTimeoutSeconds = 30,
    int MaxAttempts = 5,
    int MonthlyRuns = 5000,
    int RunsPerMinute = 120,
    int FlowRunsPerMinute = 60,
    int EmailsPerDay = 300)
{
    public static readonly TimeSpan MaxDelay = TimeSpan.FromDays(30);
    public const int MaxRetries = 5;
    public const int MaxConfigBytes = 64 * 1024;

    public static FlowLimits From(IConfiguration? c)
    {
        int Get(string key, int fallback, int min, int max) =>
            int.TryParse(c?[$"Flow:{key}"], out var v) ? Math.Clamp(v, min, max) : fallback;
        return new FlowLimits(
            Get("MaxSteps", 25, 1, 100),
            3,
            Get("StepTimeoutSeconds", 30, 5, 120),
            Get("MaxAttempts", 5, 1, 20),
            Get("MonthlyRuns", 5000, 1, 10_000_000),
            Get("RunsPerMinute", 120, 1, 100_000),
            Get("FlowRunsPerMinute", 60, 1, 100_000),
            Get("EmailsPerDay", 300, 0, 100_000));
    }
}

public static partial class DefinitionRules
{
    [GeneratedRegex("^[a-z][a-z0-9_]{0,23}$")]
    public static partial Regex StepId();

    /// <summary>Everything wrong with <paramref name="def"/> (empty when it can be saved and switched on).</summary>
    public static IReadOnlyList<DefinitionError> Validate(FlowDefinition def, FlowLimits limits)
    {
        var errors = new List<DefinitionError>();
        var trigger = FlowCatalog.Trigger(def.Trigger.App, def.Trigger.Event);
        if (trigger is null) errors.Add(new(null, "Choose a trigger."));
        else
            foreach (var key in trigger.Required)
                if (Empty(def.Trigger.Config[key])) errors.Add(new(null, $"The trigger needs {trigger.Label(key)}."));
        if (trigger is not null && def.Trigger.App == "schedule") errors.AddRange(ScheduleRules.Validate(def.Trigger));

        var all = def.AllSteps().ToList();
        if (all.Count == 0) errors.Add(new(null, "Add a step after the trigger."));
        if (all.Count > limits.MaxSteps) errors.Add(new(null, $"A flow can have at most {limits.MaxSteps} steps."));
        if (System.Text.Encoding.UTF8.GetByteCount(def.ToJson()) > FlowLimits.MaxConfigBytes) errors.Add(new(null, "This flow is too large to save."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var earlier = new List<string>();
        CheckList(def.Steps, 1, errors, seen, earlier, limits);
        return errors;
    }

    private static void CheckList(List<StepDef> steps, int depth, List<DefinitionError> errors, HashSet<string> seen, List<string> earlier, FlowLimits limits)
    {
        foreach (var s in steps)
        {
            if (!StepId().IsMatch(s.Id)) errors.Add(new(s.Id, "A step's id must be short lower-case letters and numbers."));
            else if (!seen.Add(s.Id)) errors.Add(new(s.Id, "Two steps share this id."));

            foreach (var token in Tokens.References(s.Config))
                if (token.StartsWith("steps.", StringComparison.Ordinal))
                {
                    var id = token.Split('.')[1];
                    if (!earlier.Contains(id)) errors.Add(new(s.Id, $"{{{{{token}}}}} isn't from an earlier step."));
                }

            switch (s.Type)
            {
                case StepTypes.Action:
                    var action = FlowCatalog.Action(s.App, s.Action);
                    if (action is null) { errors.Add(new(s.Id, "Choose what this step does.")); break; }
                    foreach (var key in action.Required)
                        if (Empty(s.Config[key])) errors.Add(new(s.Id, $"Fill in {action.Label(key)}."));
                    if (s.Retry is { } r && (r.Max < 0 || r.Max > FlowLimits.MaxRetries || r.BackoffSeconds < 5 || r.BackoffSeconds > 3600))
                        errors.Add(new(s.Id, $"Retries: up to {FlowLimits.MaxRetries}, waiting 5 seconds to an hour."));
                    break;
                case StepTypes.Delay:
                    if (DelayOf(s.Config) is not { } d) errors.Add(new(s.Id, "Set how long to wait."));
                    else if (d > FlowLimits.MaxDelay) errors.Add(new(s.Id, "A delay can be at most 30 days."));
                    break;
                case StepTypes.Branch:
                    if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Branches can be nested {limits.MaxDepth - 1} deep at most."));
                    if (Conditions.Parse(s.Config["condition"]) is not { Rules.Count: > 0 }) errors.Add(new(s.Id, "Add a condition."));
                    earlier.Add(s.Id);
                    var before = earlier.Count;
                    CheckList(s.Then ??= [], depth + 1, errors, seen, earlier, limits);
                    // The else side can't see the then side's outputs.
                    var thenIds = earlier.Skip(before).ToList();
                    earlier.RemoveRange(before, earlier.Count - before);
                    CheckList(s.Else ??= [], depth + 1, errors, seen, earlier, limits);
                    // After the branch, either side's outputs may be there (or empty): allow both.
                    earlier.AddRange(thenIds);
                    continue;
                default:
                    errors.Add(new(s.Id, "Unknown step."));
                    break;
            }
            earlier.Add(s.Id);
        }
    }

    public static bool Empty(JsonNode? n) => n switch
    {
        null => true,
        JsonValue v when v.TryGetValue<string>(out var s) => string.IsNullOrWhiteSpace(s),
        JsonArray a => a.Count == 0,
        JsonObject o => o.Count == 0,
        _ => false,
    };

    /// <summary>A delay step's wait: <c>{ amount, unit: minutes | hours | days }</c>.</summary>
    public static TimeSpan? DelayOf(JsonObject config)
    {
        var amount = config["amount"] is JsonValue v && v.TryGetValue<double>(out var a) ? a
            : config["amount"] is JsonValue s && s.TryGetValue<string>(out var str) && double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : -1;
        if (amount <= 0) return null;
        return (config["unit"]?.GetValue<string>() ?? "minutes") switch
        {
            "minutes" => TimeSpan.FromMinutes(amount),
            "hours" => TimeSpan.FromHours(amount),
            "days" => TimeSpan.FromDays(amount),
            _ => null,
        };
    }
}
