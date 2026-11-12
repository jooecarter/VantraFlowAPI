using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// A step's "On error" settings, for every step that calls out (an app's action, and any new kind of step that does —
/// not the built-in logic steps):
/// <list type="bullet">
/// <item><b>Retries</b> (<see cref="StepDef.Retry"/>): off, or 1–5 more tries after a wait that stays the same
/// (<c>fixed</c>) or doubles (<c>exponential</c>), for timeouts, 5xx and 429 (<c>transient</c>) or any failure
/// (<c>all</c>). No setting: 2 more tries, 30 s doubling, transient failures only — as every flow ran before.</item>
/// <item><b>Timeout</b> (<see cref="StepDef.TimeoutSeconds"/>): how long one try may take, 5 s to
/// <see cref="FlowLimits.MaxStepTimeoutSeconds"/>; no setting, the server's <c>Flow:StepTimeoutSeconds</c>.</item>
/// <item><b>If it still fails</b> (<see cref="StepDef.OnError"/>): <c>stop</c> the run (the default); <c>continue</c>
/// with the next step; or run its <c>path</c> (<see cref="StepDef.ErrorPath"/>), after which the run stops as it would
/// have (inside a Repeat for each, the loop's own "if an item fails" decides).</item>
/// </list>
/// A step that failed and went on (or down its error path) hands on <c>{ failed: true, error: { message, step, attempts } }</c>,
/// so <c>{{steps.s2.error.message}}</c>, <c>{{steps.s2.error.step}}</c> and <c>{{steps.s2.error.attempts}}</c> work.
/// Nothing here needs a migration: a definition without these fields runs as before.
/// </summary>
public static class ErrorRules
{
    public const string Stop = "stop";
    public const string Continue = "continue";
    public const string Path = "path";
    public const string Fixed = "fixed";
    public const string Exponential = "exponential";
    public const string Transient = "transient";
    public const string All = "all";

    public const int DefaultRetries = 2;
    public const int DefaultBackoffSeconds = 30;
    public const int MinBackoffSeconds = 5;
    public const int MaxBackoffSeconds = 3600;

    /// <summary>Whether a step has "On error" settings: anything but the built-in logic steps (so new kinds of action get them too).</summary>
    public static bool Applies(StepDef s) => s.Type is not (StepTypes.Delay or StepTypes.Branch or StepTypes.Loop or StepTypes.Paths or StepTypes.Filter
        or StepTypes.Formatter or StepTypes.Variable or StepTypes.Wait or StepTypes.Approval);

    /// <summary>What happens when the step still fails: <see cref="Stop"/>, <see cref="Continue"/> or <see cref="Path"/> (only with steps on it).</summary>
    public static string ModeOf(StepDef s) => s.OnError switch
    {
        Continue => Continue,
        Path when s.ErrorPath is { Count: > 0 } => Path,
        _ => Stop,
    };

    /// <summary>How a failed try is tried again.</summary>
    public sealed record RetryPolicy(int Retries, int BaseSeconds, bool Exponential, bool AnyError);

    public static RetryPolicy RetryOf(StepDef s) => s.Retry is { } r
        ? new(Math.Clamp(r.Max, 0, FlowLimits.MaxRetries), Math.Clamp(r.BackoffSeconds, MinBackoffSeconds, MaxBackoffSeconds), r.Backoff != Fixed, r.On == All)
        : new(DefaultRetries, DefaultBackoffSeconds, true, false);

    /// <summary>The wait before try <paramref name="attempt"/> + 1: the base each time (fixed), or doubling from it (exponential), at most an hour.</summary>
    public static TimeSpan WaitBefore(RetryPolicy p, int attempt) =>
        p.Exponential ? RunExecutor.Backoff(p.BaseSeconds, attempt) : TimeSpan.FromSeconds(Math.Clamp(p.BaseSeconds, MinBackoffSeconds, MaxBackoffSeconds));

    /// <summary>Whether a failure is tried again: a transient one always, any one with <c>on: all</c>.</summary>
    public static bool Retries(RetryPolicy p, StepOutcome outcome) => outcome.Retryable || p.AnyError;

    /// <summary>How long one try may take, in seconds: the step's own (held to the engine's caps), else the server's.</summary>
    public static int TimeoutOf(StepDef s, FlowLimits limits) =>
        s.TimeoutSeconds is { } t ? Math.Clamp(t, FlowLimits.MinStepTimeoutSeconds, FlowLimits.MaxStepTimeoutSeconds) : limits.StepTimeoutSeconds;

    /// <summary>What a step that failed for good hands on: the error's tokens.</summary>
    public static JsonObject Failure(string message, string stepName, int attempts, DateTime at) => new()
    {
        ["failed"] = true,
        ["error"] = new JsonObject
        {
            ["message"] = message.Length > 1000 ? message[..1000] : message,
            ["step"] = stepName,
            ["attempts"] = attempts,
            ["at"] = at.ToString("o"),
        },
    };

    /// <summary>A step whose error path is running (its output carries <c>handling: true</c>); null otherwise.</summary>
    public static JsonObject? Handling(JsonNode? output) =>
        output is JsonObject o && o["handling"] is JsonValue v && v.TryGetValue<bool>(out var b) && b ? o : null;

    /// <summary>What's wrong with a step's "On error" settings (a step inside an error path is checked by <see cref="DefinitionRules"/>).</summary>
    public static IEnumerable<string> Check(StepDef s)
    {
        if (!Applies(s))
        {
            if (s.ErrorPath is { Count: > 0 } || s.OnError is Continue or Path) yield return "Only steps that use an app can go on or take an error path.";
            yield break;
        }
        if (s.Retry is { } r)
        {
            if (r.Max < 0 || r.Max > FlowLimits.MaxRetries || r.BackoffSeconds < MinBackoffSeconds || r.BackoffSeconds > MaxBackoffSeconds)
                yield return $"Retries: up to {FlowLimits.MaxRetries}, waiting 5 seconds to an hour.";
            if (r.Backoff is not (null or Fixed or Exponential)) yield return "Choose a fixed or a growing wait between retries.";
            if (r.On is not (null or Transient or All)) yield return "Choose which errors are retried.";
        }
        if (s.TimeoutSeconds is { } t && (t < FlowLimits.MinStepTimeoutSeconds || t > FlowLimits.MaxStepTimeoutSeconds))
            yield return $"A step's timeout can be {FlowLimits.MinStepTimeoutSeconds} to {FlowLimits.MaxStepTimeoutSeconds} seconds.";
        if (s.OnError is not (null or Stop or Continue or Path)) yield return "Choose what happens if it still fails.";
        if (s.OnError == Path)
        {
            if (s.ErrorPath is not { Count: > 0 }) yield return "Add a step to its error path.";
        }
        else if (s.ErrorPath is { Count: > 0 }) yield return "Its error path only runs with “Run an error path” chosen.";
    }
}
