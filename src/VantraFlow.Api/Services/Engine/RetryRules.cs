using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>Where a "Retry from here" run starts: the step, its position, and the data it starts with.</summary>
public sealed record RetryStart(string StepId, string Position, JsonObject Data);

/// <summary>
/// "Retry from here": a failed run is run again from the step it failed at, on the flow as it is now, reusing what
/// the steps before it handed on (their outputs, the variables, the trigger's data). The failed run noted where it
/// failed (<c>failure</c> in its data, <see cref="RunExecutor"/>); the step is found again by its id, so a fixed flow
/// still lines up.
/// <list type="bullet">
/// <item>A step inside an error path: the run starts again at the step whose error path it is.</item>
/// <item>A step inside a Repeat for each: the loop picks up at the item that failed, with the items before it as they
/// went; when the loop has changed since, the run starts again at the loop.</item>
/// </list>
/// A retry is a new run (it counts against the month, as a replay does), linked to the one it retries.
/// </summary>
public static class RetryRules
{
    /// <summary>The step a failed run would be retried from (its id), or null when it can't be.</summary>
    public static string? StepOf(FlowDefinition def, JsonObject? context)
    {
        if (context?["failure"] is not JsonObject failure || Tokens.Text(failure["stepId"]) is not { Length: > 0 } id) return null;
        if (Positions.PathOf(def, id) is not { } path) return null;
        if (Positions.ErrorOwnerOf(path) is { } owner) return Positions.StepAt(def, owner)?.Id;
        return id;
    }

    /// <summary>A retry's start from the failed run's data; null when the step it failed at isn't in <paramref name="def"/> any more.</summary>
    public static RetryStart? Resume(FlowDefinition def, JsonObject? context, string runId, string retryOf, DateTime now, bool test)
    {
        if (context?["failure"] is not JsonObject failure || Tokens.Text(failure["stepId"]) is not { Length: > 0 } failedId) return null;
        if (Positions.PathOf(def, failedId) is not { } path) return null;
        if (Positions.ErrorOwnerOf(path) is { } owner) path = owner;
        if (Positions.StepAt(def, path) is not { } step) return null;

        var data = (JsonObject)context.DeepClone();
        data.Remove("failure");
        data.Remove("now");
        data.Remove("loop");
        var steps = data["steps"] as JsonObject ?? (JsonObject)(data["steps"] = new JsonObject());

        // Inside a Repeat for each: the loop as it was at the failed item, when it's still the same loop; else the loop again.
        if (Positions.LoopOf(path) is { } loopPath && Positions.StepAt(def, loopPath) is { Type: StepTypes.Loop } loop)
        {
            if (Tokens.Text(failure["loopId"]) == loop.Id && failure["loop"] is JsonObject state && failure["item"] is JsonObject item
                && state["running"] is JsonValue r && r.TryGetValue<bool>(out var running) && running)
            {
                steps[loop.Id] = state.DeepClone();
                data["loop"] = item.DeepClone();
            }
            else
            {
                path = loopPath;
                step = loop;
            }
        }

        // The step (and anything inside it) runs again from scratch.
        foreach (var s in FlowDefinition.Walk([step])) steps.Remove(s.Id);
        var run = data["run"] as JsonObject ?? new JsonObject();
        data["run"] = new JsonObject
        {
            ["id"] = runId, ["startedAt"] = now.ToString("o"), ["test"] = test, ["retryOf"] = retryOf, ["retryFrom"] = step.Id,
            ["firstStartedAt"] = run["firstStartedAt"]?.DeepClone() ?? run["startedAt"]?.DeepClone(),
        };
        return new RetryStart(step.Id, path, data);
    }

    /// <summary>The step a run was retried from (its id), when it's a "Retry from here" run.</summary>
    public static string? RetriedFrom(JsonObject? context) =>
        Tokens.Text(context?["run"]?["retryFrom"]) is { Length: > 0 } id ? id : null;
}
