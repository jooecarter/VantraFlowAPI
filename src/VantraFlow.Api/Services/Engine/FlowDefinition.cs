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

    /// <summary>
    /// Every step, depth first in the order they run (a branch, then its "then" side, then its "else"; paths, each
    /// path's steps in turn, then "otherwise"; a loop, then the steps it repeats).
    /// </summary>
    public IEnumerable<StepDef> AllSteps() => Walk(Steps);

    public static IEnumerable<StepDef> Walk(IEnumerable<StepDef> steps)
    {
        foreach (var s in steps)
        {
            yield return s;
            if (s.Then is not null) foreach (var t in Walk(s.Then)) yield return t;
            if (s.Paths is not null) foreach (var p in s.Paths) foreach (var x in Walk(p.Steps)) yield return x;
            if (s.Else is not null) foreach (var e in Walk(s.Else)) yield return e;
            if (s.Do is not null) foreach (var d in Walk(s.Do)) yield return d;
            if (s.ErrorPath is not null) foreach (var x in Walk(s.ErrorPath)) yield return x;
        }
    }
}

public sealed class TriggerDef
{
    /// <summary><c>orbit</c>, <c>projects</c>, <c>schedule</c>, <c>webhook</c> or <c>flow</c> (called by another flow).</summary>
    public string App { get; set; } = "";
    /// <summary>The app's event, e.g. <c>record.created</c> (<see cref="FlowCatalog.Triggers"/>).</summary>
    public string Event { get; set; } = "";
    public JsonObject Config { get; set; } = new();
}

public sealed class StepDef
{
    public string Id { get; set; } = "";
    /// <summary>
    /// <c>action</c>, <c>delay</c>, <c>branch</c> (if / else), <c>paths</c>, <c>loop</c> ("Repeat for each"),
    /// <c>filter</c> ("Only continue if"), <c>formatter</c>, <c>variable</c>, <c>wait</c> ("Wait until") or
    /// <c>approval</c> (<see cref="StepTypes"/>).
    /// </summary>
    public string Type { get; set; } = StepTypes.Action;
    public string? App { get; set; }
    public string? Action { get; set; }
    public string? Name { get; set; }
    public JsonObject Config { get; set; } = new();
    public List<StepDef>? Then { get; set; }
    /// <summary>An if / else's "otherwise" side, and a paths step's "otherwise" path.</summary>
    public List<StepDef>? Else { get; set; }
    /// <summary>A paths step's paths, tried top to bottom (<see cref="PathRules"/>).</summary>
    public List<PathDef>? Paths { get; set; }
    /// <summary>A loop's steps, run once for each item (<see cref="LoopRules"/>).</summary>
    public List<StepDef>? Do { get; set; }
    /// <summary>"On error" (<see cref="ErrorRules"/>): how often a failed action is tried again, and how long it waits.</summary>
    public RetryDef? Retry { get; set; }
    /// <summary>"On error": how long one try may take (5 s to <see cref="FlowLimits.MaxStepTimeoutSeconds"/>); null, the server's.</summary>
    public int? TimeoutSeconds { get; set; }
    /// <summary>"On error", when it still fails: <c>stop</c> (null, the default), <c>continue</c> or <c>path</c> (<see cref="ErrorRules"/>).</summary>
    public string? OnError { get; set; }
    /// <summary>The error path: the steps that run when this step fails for good (<c>onError: path</c>).</summary>
    public List<StepDef>? ErrorPath { get; set; }

    /// <summary>The lists of steps inside this one: a branch's sides, each path and "otherwise", a loop's steps, an error path.</summary>
    public IEnumerable<List<StepDef>> Children()
    {
        if (Then is not null) yield return Then;
        if (Paths is not null) foreach (var p in Paths) if (p.Steps is not null) yield return p.Steps;
        if (Else is not null) yield return Else;
        if (Do is not null) yield return Do;
        if (ErrorPath is not null) yield return ErrorPath;
    }
}

/// <summary>One of a paths step's paths: its name, the condition that sends a run down it, and its steps.</summary>
public sealed class PathDef
{
    public string? Name { get; set; }
    public JsonObject? Condition { get; set; }
    public List<StepDef> Steps { get; set; } = [];
}

/// <summary>
/// How often a failed action is tried again (0: not at all), the first wait, whether the wait doubles each time
/// (<c>exponential</c>, the default) or stays the same (<c>fixed</c>), and which failures are tried again:
/// <c>transient</c> (the default: a timeout, a 5xx, a 429, the app unreachable) or <c>all</c>.
/// </summary>
public sealed class RetryDef
{
    public int Max { get; set; } = 2;
    public int BackoffSeconds { get; set; } = 30;
    public string? Backoff { get; set; }
    public string? On { get; set; }
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

/// <summary>
/// "Run another flow": a step starts a flow whose trigger is "Called by another flow", handing it <c>input</c> (its
/// <c>{{trigger.input…}}</c>) and who called (<c>{{trigger.caller…}}</c>). It doesn't wait for that run. Flows can call
/// each other at most <see cref="MaxDepth"/> deep, so a flow that calls itself stops.
/// </summary>
public static class SubFlowRules
{
    public const int MaxDepth = 3;

    /// <summary>How deep a run is: 0 for one an app, a schedule or a webhook started; its caller's + 1 for a called one.</summary>
    public static int DepthOf(JsonNode? data) =>
        Tokens.Resolve(data, "trigger.caller.depth") is JsonValue v && v.TryGetValue<int>(out var d) ? Math.Max(0, d) : 0;
}

public static class StepTypes
{
    public const string Action = "action";
    public const string Delay = "delay";
    public const string Branch = "branch";
    public const string Loop = "loop";
    public const string Paths = "paths";
    public const string Filter = "filter";
    public const string Formatter = "formatter";
    public const string Variable = "variable";
    /// <summary>"Wait until": re-checks a condition until it holds or a time passes (<see cref="WaitRules"/>).</summary>
    public const string Wait = "wait";
    /// <summary>"Approval": asks people to approve or reject, then goes down Approved, Rejected or Timed out (<see cref="ApprovalRules"/>).</summary>
    public const string Approval = "approval";
}

/// <summary>
/// Paths: up to <see cref="MaxPaths"/> paths, each with its own condition, tried top to bottom; the run goes down the
/// first that holds, else down "otherwise" (the step's <c>else</c>) when <c>config.otherwise</c> is on, else on past
/// the step. If / else is the two-path case and keeps its own shape, so existing flows run as they did.
/// </summary>
public static class PathRules
{
    public const int MaxPaths = 5;

    public static bool HasOtherwise(StepDef s) => s.Config["otherwise"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    public static string NameOf(PathDef p, int index) => p.Name?.Trim() is { Length: > 0 } n ? n : $"Path {index + 1}";
}

/// <summary>
/// "Repeat for each": a loop step's config is <c>{ items, max, onError }</c>. <c>items</c> is a token for a list from the
/// trigger or an earlier step (<c>{{steps.s1.records}}</c>, <c>{{trigger.body.items}}</c>); the steps in <c>do</c> run
/// once per item, top to bottom, and read the item as <c>{{loop.item…}}</c> (with <c>{{loop.number}}</c> and
/// <c>{{loop.count}}</c>). At most <c>max</c> items (1–<see cref="MaxItems"/>, default <see cref="MaxItems"/>); a test
/// goes through the first <see cref="TestItems"/>. <c>onError</c>: <c>stop</c> (the default: the run fails with the
/// item) or <c>continue</c> (the item is marked failed and the next one starts). Loops don't nest.
/// </summary>
public static class LoopRules
{
    public const int MaxItems = 100;
    public const int TestItems = 3;

    public static int MaxOf(JsonObject config)
    {
        var n = config["max"] is JsonValue v
            && double.TryParse(Tokens.Text(v), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : MaxItems;
        return (int)Math.Clamp(Math.Floor(n), 1, MaxItems);
    }

    public static bool Continues(JsonObject config) => Tokens.Text(config["onError"]) == "continue";

    /// <summary>
    /// The list a loop goes through: a list as it is; an object's first list among <c>items</c>, <c>records</c>,
    /// <c>tasks</c>, <c>results</c>, <c>data</c> (so a step's whole output works too); text that is a JSON list. Null
    /// for anything else.
    /// </summary>
    public static JsonArray? ListOf(JsonNode? node)
    {
        switch (node)
        {
            case JsonArray a: return a;
            case JsonObject o:
                foreach (var key in new[] { "items", "records", "tasks", "results", "data" })
                    if (Tokens.Resolve(o, key) is JsonArray inner) return inner;
                return null;
            case JsonValue v when v.TryGetValue<string>(out var s) && s.TrimStart().StartsWith('['):
                try { return JsonNode.Parse(s) as JsonArray; }
                catch (System.Text.Json.JsonException) { return null; }
            default: return null;
        }
    }

    /// <summary>A short name for an item in the run log: its name or title, else its id, else its text.</summary>
    public static string Label(JsonNode? item)
    {
        string? Pick(JsonObject o) =>
            new[] { "name", "title", "fields.Name", "email", "id" }.Select(k => Tokens.Resolve(o, k)).Where(n => n is JsonValue)
                .Select(n => Tokens.Text(n)).FirstOrDefault(t => t.Length > 0);
        var text = item is JsonObject o ? Pick(o) ?? "" : Tokens.Text(item);
        return text.Length <= 80 ? text : text[..80] + "…";
    }
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
    /// <summary>The longest a step's own timeout may be (the cap <c>Flow:StepTimeoutSeconds</c> is held to).</summary>
    public const int MaxStepTimeoutSeconds = 120;
    public const int MinStepTimeoutSeconds = 5;
    public const int MaxConfigBytes = 64 * 1024;

    public static FlowLimits From(IConfiguration? c)
    {
        int Get(string key, int fallback, int min, int max) =>
            int.TryParse(c?[$"Flow:{key}"], out var v) ? Math.Clamp(v, min, max) : fallback;
        return new FlowLimits(
            Get("MaxSteps", 25, 1, 100),
            3,
            Get("StepTimeoutSeconds", 30, MinStepTimeoutSeconds, MaxStepTimeoutSeconds),
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
        else if (def.Trigger.App != "schedule") // a schedule's own rules say what's missing, once
            foreach (var key in trigger.Required)
                if (Empty(def.Trigger.Config[key])) errors.Add(new(null, $"The trigger needs {trigger.Label(key)}."));
        if (trigger is not null && def.Trigger.App == "schedule") errors.AddRange(ScheduleRules.Validate(def.Trigger));

        var all = def.AllSteps().ToList();
        if (all.Count == 0) errors.Add(new(null, "Add a step after the trigger."));
        if (all.Count > limits.MaxSteps) errors.Add(new(null, $"A flow can have at most {limits.MaxSteps} steps."));
        if (System.Text.Encoding.UTF8.GetByteCount(def.ToJson()) > FlowLimits.MaxConfigBytes) errors.Add(new(null, "This flow is too large to save."));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var earlier = new List<string>();
        var vars = VariableRules.Names(def);
        CheckList(def.Steps, 1, errors, seen, earlier, limits, false, vars);
        // A step inside an error path can't have one of its own (at any depth inside it).
        foreach (var owner in all.Where(s => s.ErrorPath is { Count: > 0 }))
            foreach (var inner in FlowDefinition.Walk(owner.ErrorPath!).Where(x => x.OnError == ErrorRules.Path))
                errors.Add(new(inner.Id, "A step in an error path can't have an error path of its own."));
        return errors;
    }

    /// <summary>Every token path a step reads: its config's, and its paths' conditions.</summary>
    public static IEnumerable<string> ReferencesOf(StepDef s) =>
        Tokens.References(s.Config).Concat((s.Paths ?? []).SelectMany(p => Tokens.References(p.Condition)));

    private static void CheckList(List<StepDef> steps, int depth, List<DefinitionError> errors, HashSet<string> seen, List<string> earlier, FlowLimits limits, bool inLoop,
        IReadOnlySet<string> vars)
    {
        foreach (var s in steps)
        {
            if (!StepId().IsMatch(s.Id)) errors.Add(new(s.Id, "A step's id must be short lower-case letters and numbers."));
            else if (!seen.Add(s.Id)) errors.Add(new(s.Id, "Two steps share this id."));

            foreach (var token in ReferencesOf(s))
                if (token.StartsWith("steps.", StringComparison.Ordinal))
                {
                    var id = token.Split('.')[1];
                    if (!earlier.Contains(id)) errors.Add(new(s.Id, $"{{{{{token}}}}} isn't from an earlier step."));
                }
                else if ((token == "loop" || token.StartsWith("loop.", StringComparison.Ordinal)) && (!inLoop || s.Type == StepTypes.Loop))
                    errors.Add(new(s.Id, $"{{{{{token}}}}} is only there inside a Repeat for each."));
                else if (token.StartsWith("vars.", StringComparison.Ordinal) && !vars.Contains(token.Split('.')[1]))
                    errors.Add(new(s.Id, $"{{{{{token}}}}} isn't set by a Set variable step."));

            // "On error": its settings, and its error path, which sees the steps before it and the step itself (its
            // error); the steps after it may use the path's outputs (empty when it didn't run).
            errors.AddRange(ErrorRules.Check(s).Select(m => new DefinitionError(s.Id, m)));
            var errorPathMade = new List<string>();
            if (ErrorRules.Applies(s) && s.ErrorPath is { Count: > 0 } errorPath)
            {
                if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Error paths can be nested {limits.MaxDepth - 1} deep at most."));
                var before = earlier.Count;
                earlier.Add(s.Id);
                CheckList(errorPath, depth + 1, errors, seen, earlier, limits, inLoop, vars);
                errorPathMade.AddRange(earlier.Skip(before + 1));
                earlier.RemoveRange(before, earlier.Count - before);
            }

            switch (s.Type)
            {
                case StepTypes.Action:
                    var action = FlowCatalog.Action(s.App, s.Action);
                    if (action is null) { errors.Add(new(s.Id, "Choose what this step does.")); break; }
                    foreach (var key in action.Required)
                        if (Empty(s.Config[key])) errors.Add(new(s.Id, $"Fill in {action.Label(key)}."));
                    if (s.App == "email" && Mail.SenderRules.Check(s.Config["from"]) is { } fromWhy) errors.Add(new(s.Id, fromWhy));
                    if (s.App == Ai.AiStepRules.App) errors.AddRange(Ai.AiStepRules.Check(s.Action, s.Config).Select(m => new DefinitionError(s.Id, m)));
                    break;
                case StepTypes.Delay:
                    if (DelayRules.IsUntil(s.Config))
                    {
                        if (DelayRules.CheckUntil(s.Config) is { } untilWhy) errors.Add(new(s.Id, untilWhy));
                    }
                    else if (DelayOf(s.Config) is not { } d) errors.Add(new(s.Id, "Set how long to wait."));
                    else if (d > FlowLimits.MaxDelay) errors.Add(new(s.Id, "A delay can be at most 30 days."));
                    break;
                case StepTypes.Wait:
                    errors.AddRange(WaitRules.Check(s.Config, earlier).Select(m => new DefinitionError(s.Id, m)));
                    break;
                case StepTypes.Approval:
                    if (inLoop) errors.Add(new(s.Id, "An Approval can't be inside a Repeat for each."));
                    else if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Branches and approvals can be nested {limits.MaxDepth - 1} deep at most."));
                    errors.AddRange(ApprovalRules.Check(s).Select(m => new DefinitionError(s.Id, m)));
                    earlier.Add(s.Id);
                    Sides(s, ApprovalRules.Sides(s), depth, errors, seen, earlier, limits, inLoop, vars);
                    continue;
                case StepTypes.Filter:
                    if (Conditions.Parse(s.Config["condition"]) is not { } fc || fc.Count == 0) errors.Add(new(s.Id, "Add a condition."));
                    break;
                case StepTypes.Formatter:
                    if (Formatter.Check(s.Config) is { } why) errors.Add(new(s.Id, why));
                    break;
                case StepTypes.Variable:
                    errors.AddRange(VariableRules.Check(s.Config).Select(m => new DefinitionError(s.Id, m)));
                    break;
                case StepTypes.Branch:
                    if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Branches can be nested {limits.MaxDepth - 1} deep at most."));
                    if (Conditions.Parse(s.Config["condition"]) is not { } bc || bc.Count == 0) errors.Add(new(s.Id, "Add a condition."));
                    earlier.Add(s.Id);
                    Sides(s, [s.Then ??= [], s.Else ??= []], depth, errors, seen, earlier, limits, inLoop, vars);
                    continue;
                case StepTypes.Paths:
                    if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Paths can be nested {limits.MaxDepth - 1} deep at most."));
                    var paths = s.Paths ??= [];
                    if (paths.Count == 0) errors.Add(new(s.Id, "Add a path."));
                    if (paths.Count > PathRules.MaxPaths) errors.Add(new(s.Id, $"A step can have at most {PathRules.MaxPaths} paths."));
                    for (var i = 0; i < paths.Count; i++)
                        if (Conditions.Parse(paths[i].Condition) is not { } pc || pc.Count == 0)
                            errors.Add(new(s.Id, $"{PathRules.NameOf(paths[i], i)} needs a condition."));
                    earlier.Add(s.Id);
                    var sides = paths.Select(p => p.Steps ??= []).ToList();
                    if (PathRules.HasOtherwise(s)) sides.Add(s.Else ??= []);
                    Sides(s, sides, depth, errors, seen, earlier, limits, inLoop, vars);
                    continue;
                case StepTypes.Loop:
                    if (inLoop) errors.Add(new(s.Id, "A Repeat for each can't be inside another."));
                    else if (depth >= limits.MaxDepth) errors.Add(new(s.Id, $"Branches and loops can be nested {limits.MaxDepth - 1} deep at most."));
                    if (Empty(s.Config["items"])) errors.Add(new(s.Id, "Choose the list to go through."));
                    if (s.Config["max"] is { } max && !Empty(max)
                        && !(double.TryParse(Tokens.Text(max), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var given)
                             && given >= 1 && given <= LoopRules.MaxItems))
                        errors.Add(new(s.Id, $"Go through 1 to {LoopRules.MaxItems} items."));
                    if (s.Do is not { Count: > 0 }) errors.Add(new(s.Id, "Add a step to repeat."));
                    earlier.Add(s.Id);
                    CheckList(s.Do ??= [], depth + 1, errors, seen, earlier, limits, true, vars);
                    continue;
                default:
                    errors.Add(new(s.Id, "Unknown step."));
                    break;
            }
            earlier.Add(s.Id);
            earlier.AddRange(errorPathMade);
        }
    }

    /// <summary>
    /// A branch's or paths step's sides: each sees the steps before the step (and the step itself), not its sibling
    /// sides; the steps after it may use any side's outputs (empty when that side didn't run).
    /// </summary>
    private static void Sides(StepDef s, List<List<StepDef>> sides, int depth, List<DefinitionError> errors, HashSet<string> seen, List<string> earlier,
        FlowLimits limits, bool inLoop, IReadOnlySet<string> vars)
    {
        var before = earlier.Count;
        var made = new List<string>();
        foreach (var side in sides)
        {
            CheckList(side, depth + 1, errors, seen, earlier, limits, inLoop, vars);
            made.AddRange(earlier.Skip(before));
            earlier.RemoveRange(before, earlier.Count - before);
        }
        earlier.AddRange(made);
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
            : config["amount"] is JsonValue s && s.TryGetValue<string>(out var str) && double.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, out var p) ? p
            : Formatter.ToNumber(config["amount"]) ?? -1;
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
