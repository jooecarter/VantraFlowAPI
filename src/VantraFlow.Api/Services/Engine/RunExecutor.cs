using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>One action to carry out: which step, its input with the tokens filled in, and for which run.</summary>
public sealed record StepCall(Guid WorkspaceId, string RunId, string FlowId, StepDef Step, JsonObject Input, bool Test);

/// <summary>
/// How an action went. <see cref="Retryable"/>: worth trying again (a timeout, a 5xx, a 429, the app unreachable);
/// otherwise the run fails at once (a refused value, a missing record, a disconnected app).
/// </summary>
public sealed record StepOutcome(bool Ok, JsonNode? Output, string? Error = null, bool Retryable = false)
{
    public static StepOutcome Done(JsonNode? output) => new(true, output);
    public static StepOutcome Fail(string error, bool retryable = false) => new(false, null, error, retryable);
}

/// <summary>Carries out the actions of every app (<see cref="Connectors.StepActions"/>; tests use a fake).</summary>
public interface IStepActions
{
    Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct);
}

/// <summary>A run as the executor sees it (a flow.Runs row, its definition and data parsed).</summary>
public sealed class RunState
{
    public required string Id { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required string FlowId { get; init; }
    public required FlowDefinition Definition { get; init; }
    /// <summary><c>{ trigger, steps: { id: output }, run, workspace, now }</c>: what tokens read.</summary>
    public required JsonObject Data { get; init; }
    public string? Position { get; set; }
    public int Attempt { get; set; }
    public string Status { get; set; } = RunStatuses.Queued;
    public DateTime NotBefore { get; set; }
    public int StepCount { get; set; }
    public string? Error { get; set; }
    public bool Test { get; init; }

    public JsonObject Steps => Data["steps"] as JsonObject ?? (JsonObject)(Data["steps"] = new JsonObject());

    /// <summary>A new run's data: the trigger's output, and what the run and workspace are.</summary>
    public static JsonObject NewData(JsonNode? trigger, string runId, DateTime startedAt, string workspaceName, string timeZone, bool test) => new()
    {
        ["trigger"] = trigger?.DeepClone() ?? new JsonObject(),
        ["steps"] = new JsonObject(),
        ["run"] = new JsonObject { ["id"] = runId, ["startedAt"] = startedAt.ToString("o"), ["test"] = test },
        ["workspace"] = new JsonObject { ["name"] = workspaceName, ["timeZone"] = timeZone },
    };
}

public static class RunStatuses
{
    public const string Queued = "queued";
    public const string Running = "running";
    public const string Waiting = "waiting";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Cancelled = "cancelled";

    public static bool Finished(string s) => s is Succeeded or Failed or Skipped or Cancelled;
}

/// <summary>One line of the run log (a flow.RunSteps row).</summary>
public sealed record StepLog(
    string StepId, string Type, string? App, string? Action, string Name, string Status, int Attempt,
    string? Input, string? Output, string? Error, DateTime StartedAt, int DurationMs);

/// <summary>
/// Runs a flow's steps from where the run is, until it finishes, waits (a delay, or a retry's backoff), or has run
/// <c>maxSteps</c> in this go (the rest is picked up straight after). Each step's input, output, error and duration
/// go to the log; each output joins the run's data under <c>steps.{id}</c>, for later steps' tokens.
/// <para>
/// Adapted from Vantra Orbit's AutomationEngine (one pass over a flat list of actions, all or nothing) into a
/// resumable walk: delays, if / else branches, outputs between steps, and retries with backoff. A test run
/// (<see cref="RunState.Test"/>) skips delays and doesn't retry. Pure apart from <see cref="IStepActions"/>, so the
/// tests drive it directly.
/// </para>
/// </summary>
public sealed class RunExecutor(IStepActions actions, FlowLimits limits, Func<DateTime>? clock = null)
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    public const int DefaultRetries = 2;
    public const int DefaultBackoffSeconds = 30;

    /// <summary>The wait before try <paramref name="attempt"/> + 1: the base, doubling, at most an hour.</summary>
    public static TimeSpan Backoff(int baseSeconds, int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(5, baseSeconds) * Math.Pow(2, Math.Max(0, attempt - 1)), 3600));

    public static string NameOf(StepDef s) => !string.IsNullOrWhiteSpace(s.Name) ? s.Name!.Trim() : s.Type switch
    {
        StepTypes.Delay => "Delay",
        StepTypes.Branch => "If / else",
        StepTypes.Loop => "Repeat for each",
        _ => FlowCatalog.Action(s.App, s.Action)?.Title ?? "Step",
    };

    public async Task<IReadOnlyList<StepLog>> ExecuteAsync(RunState s, CancellationToken ct, int maxSteps = 50)
    {
        var logs = new List<StepLog>();
        s.Status = RunStatuses.Running;
        for (var n = 0; n < maxSteps; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (s.Position is null) { s.Status = RunStatuses.Succeeded; return logs; }
            var step = Positions.StepAt(s.Definition, s.Position);
            if (step is null)
            {
                s.Status = RunStatuses.Failed;
                s.Error = "The flow's steps couldn't be followed.";
                return logs;
            }
            var started = now();
            s.Data["now"] = started.ToString("o");
            switch (step.Type)
            {
                case StepTypes.Delay:
                    if (!Delay(s, step, started, logs)) return logs;
                    break;
                case StepTypes.Branch:
                    Branch(s, step, started, logs);
                    break;
                case StepTypes.Loop:
                    if (!Loop(s, step, started, logs)) return logs;
                    break;
                default:
                    if (!await ActionAsync(s, step, started, logs, ct)) return logs;
                    break;
            }
        }
        // Plenty left: carry on in the next go, straight away.
        s.Status = RunStatuses.Queued;
        s.NotBefore = now();
        return logs;
    }

    /// <summary>False when the run now waits.</summary>
    private bool Delay(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var name = NameIn(s, step);
        if (s.Steps[step.Id] is JsonObject prior && Utc(prior["until"]) is { } until)
        {
            if (started < until)
            {
                s.Status = RunStatuses.Waiting;
                s.NotBefore = until;
                return false;
            }
            s.Steps[step.Id] = new JsonObject { ["waitedUntil"] = until.ToString("o") };
            logs.Add(Log(step, name, "succeeded", s.Attempt, null, s.Steps[step.Id], null, started, 0));
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        var wait = DefinitionRules.DelayOf(step.Config) ?? TimeSpan.Zero;
        if (s.Test || wait <= TimeSpan.Zero)
        {
            s.Steps[step.Id] = new JsonObject { ["skipped"] = true };
            logs.Add(Log(step, name, "skipped", 0, step.Config, s.Steps[step.Id], s.Test ? "Delays are skipped in a test." : null, started, 0));
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        var due = started.Add(wait);
        s.Steps[step.Id] = new JsonObject { ["until"] = due.ToString("o") };
        logs.Add(Log(step, name, "waiting", 0, step.Config, s.Steps[step.Id], null, started, 0));
        s.Status = RunStatuses.Waiting;
        s.NotBefore = due;
        return false;
    }

    private void Branch(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var cond = Conditions.Parse(step.Config["condition"]);
        var result = cond is not null && Conditions.Evaluate(cond, s.Data);
        var input = new JsonArray((cond?.Rules ?? []).Select(r => (JsonNode)new JsonObject
        {
            ["left"] = Tokens.Render(r.Left, s.Data),
            ["op"] = r.Op,
            ["right"] = r.Right is null ? null : Tokens.Render(r.Right, s.Data),
        }).ToArray());
        s.Steps[step.Id] = new JsonObject { ["result"] = result, ["path"] = result ? "then" : "else" };
        logs.Add(Log(step, NameIn(s, step), "succeeded", 0, new JsonObject { ["match"] = cond?.Match ?? "all", ["rules"] = input }, s.Steps[step.Id], null, started, 0));
        s.Position = Positions.Enter(s.Definition, s.Position!, result);
    }

    /// <summary>False when the run stops here (failed, or waiting to retry).</summary>
    private async Task<bool> ActionAsync(RunState s, StepDef step, DateTime started, List<StepLog> logs, CancellationToken ct)
    {
        var name = NameIn(s, step);
        var htmlKeys = step.App == "email" ? new HashSet<string> { "body" } : null;
        var input = Tokens.RenderNode(step.Config, s.Data, htmlKeys) as JsonObject ?? new JsonObject();
        var clock = Stopwatch.StartNew();
        StepOutcome outcome;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(limits.StepTimeoutSeconds));
            try
            {
                outcome = await actions.RunAsync(new StepCall(s.WorkspaceId, s.Id, s.FlowId, step, input, s.Test), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = StepOutcome.Fail($"No answer within {limits.StepTimeoutSeconds} seconds.", retryable: true);
            }
        }
        var ms = (int)Math.Min(int.MaxValue, clock.ElapsedMilliseconds);
        s.StepCount++;
        if (outcome.Ok)
        {
            s.Steps[step.Id] = outcome.Output?.DeepClone() ?? new JsonObject();
            logs.Add(Log(step, name, "succeeded", s.Attempt, input, s.Steps[step.Id], null, started, ms));
            s.Attempt = 0;
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        var retries = s.Test ? 0 : Math.Min(step.Retry?.Max ?? DefaultRetries, Math.Max(0, limits.MaxAttempts - 1));
        if (outcome.Retryable && s.Attempt < retries)
        {
            s.Attempt++;
            var wait = Backoff(step.Retry?.BackoffSeconds ?? DefaultBackoffSeconds, s.Attempt);
            logs.Add(Log(step, name, "retrying", s.Attempt - 1, input, null, $"{outcome.Error} Trying again in {Words(wait)}.", started, ms));
            s.Status = RunStatuses.Queued;
            s.NotBefore = now().Add(wait);
            return false;
        }
        logs.Add(Log(step, name, "failed", s.Attempt, input, null, outcome.Error, started, ms));
        s.Attempt = 0;
        if (ItemFailed(s, outcome.Error ?? "Failed.", started, logs)) return true;
        s.Status = RunStatuses.Failed;
        s.Error = $"{name}: {outcome.Error}";
        return false;
    }

    // ── Repeat for each ──

    /// <summary>A step's name in the log; inside a loop, with the item it ran for ("Create task · item 3").</summary>
    private static string NameIn(RunState s, StepDef step)
    {
        var name = NameOf(step);
        return Positions.LoopOf(s.Position) is not null && s.Data["loop"] is JsonObject l && l["number"] is JsonValue n
            ? $"{name} · item {Tokens.Text(n)}" : name;
    }

    private static int Int(JsonNode? n) => n is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    private static bool Running(JsonNode? state) => state is JsonObject o && o["running"] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    /// <summary>
    /// A loop step: the first time, it reads the list (at most <see cref="LoopRules.MaxOf"/> items; a test, the first
    /// <see cref="LoopRules.TestItems"/>) and starts the first item; each time its steps come back round, it notes how
    /// that item went and starts the next, until the last, when it logs every item's result and the run goes on.
    /// While it runs, <c>loop</c> in the run's data is the current item: <c>{ item, index, number, count }</c>.
    /// False when the run stops here (the list isn't a list).
    /// </summary>
    private bool Loop(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var path = s.Position!;
        if (s.Steps[step.Id] is JsonObject st && Running(st))
        {
            var items = st["items"] as JsonArray ?? [];
            var index = Int(st["index"]);
            var error = st["itemError"] is JsonValue ev && ev.TryGetValue<string>(out var e) ? e : null;
            st.Remove("itemError");
            Note(st, index, error);
            if (index + 1 < items.Count)
            {
                StartItem(s, st, index + 1);
                s.Position = Positions.EnterLoop(s.Definition, path);
                return true;
            }
            Finish(s, step, st, started, logs, failedRun: false);
            s.Position = Positions.After(s.Definition, path);
            return true;
        }

        var input = Tokens.RenderNode(step.Config, s.Data) as JsonObject ?? new JsonObject();
        var list = LoopRules.ListOf(input["items"]);
        if (list is null)
        {
            var why = "The list to go through isn't a list.";
            logs.Add(Log(step, NameOf(step), "failed", 0, input, null, why, started, 0));
            s.Status = RunStatuses.Failed;
            s.Error = $"{NameOf(step)}: {why}";
            return false;
        }
        var max = Math.Min(LoopRules.MaxOf(step.Config), s.Test ? LoopRules.TestItems : LoopRules.MaxItems);
        var take = new JsonArray(list.Take(max).Select(x => x?.DeepClone()).ToArray());
        var state = new JsonObject
        {
            ["running"] = true, ["items"] = take, ["total"] = list.Count, ["index"] = 0, ["results"] = new JsonArray(),
        };
        s.Steps[step.Id] = state;
        if (take.Count == 0 || step.Do is not { Count: > 0 })
        {
            Finish(s, step, state, started, logs, failedRun: false);
            s.Position = Positions.After(s.Definition, path);
            return true;
        }
        StartItem(s, state, 0);
        s.Position = Positions.EnterLoop(s.Definition, path);
        return true;
    }

    private static void StartItem(RunState s, JsonObject state, int index)
    {
        var items = state["items"] as JsonArray ?? [];
        state["index"] = index;
        s.Data["loop"] = new JsonObject
        {
            ["item"] = items[index]?.DeepClone(), ["index"] = index, ["number"] = index + 1, ["count"] = items.Count,
        };
    }

    /// <summary>How the item at <paramref name="index"/> went, in the loop's results.</summary>
    private static void Note(JsonObject state, int index, string? error)
    {
        var items = state["items"] as JsonArray ?? [];
        var results = state["results"] as JsonArray ?? (JsonArray)(state["results"] = new JsonArray());
        var r = new JsonObject
        {
            ["number"] = index + 1,
            ["label"] = index < items.Count ? LoopRules.Label(items[index]) : "",
            ["status"] = error is null ? "succeeded" : "failed",
        };
        if (error is not null) r["error"] = error.Length > 300 ? error[..300] : error;
        results.Add(r);
    }

    /// <summary>The loop's log line and output: how many items, how many failed, and each item's result.</summary>
    private void Finish(RunState s, StepDef step, JsonObject state, DateTime started, List<StepLog> logs, bool failedRun)
    {
        var results = state["results"] as JsonArray ?? [];
        var count = (state["items"] as JsonArray)?.Count ?? 0;
        var total = Int(state["total"]);
        var failed = results.Count(r => Tokens.Text(r?["status"]) == "failed");
        var output = new JsonObject
        {
            ["count"] = count,
            ["succeeded"] = results.Count - failed,
            ["failed"] = failed,
            ["total"] = total,
            ["results"] = results.DeepClone(),
        };
        s.Steps[step.Id] = output;
        s.Data.Remove("loop");
        var note = total > count
            ? s.Test && count == LoopRules.TestItems ? $"A test goes through the first {count} of {total} items." : $"Only the first {count} of {total} items."
            : null;
        if (!failedRun && failed > 0) note = $"{failed} of {count} items failed." + (note is null ? "" : " " + note);
        var input = new JsonObject { ["items"] = total, ["max"] = LoopRules.MaxOf(step.Config), ["onError"] = LoopRules.Continues(step.Config) ? "continue" : "stop" };
        logs.Add(Log(step, NameOf(step), failedRun ? "failed" : "succeeded", 0, input, output, note, started, 0));
    }

    /// <summary>
    /// A step inside a loop failed for good. "Continue": the item is marked failed and the loop goes on to the next
    /// (true). "Stop" (the default): the loop logs its results so far and the run fails (false).
    /// </summary>
    private bool ItemFailed(RunState s, string error, DateTime started, List<StepLog> logs)
    {
        if (Positions.LoopOf(s.Position) is not { } loopPath || Positions.StepAt(s.Definition, loopPath) is not { Type: StepTypes.Loop } loop
            || s.Steps[loop.Id] is not JsonObject state || !Running(state))
            return false;
        if (LoopRules.Continues(loop.Config))
        {
            state["itemError"] = error;
            s.Position = loopPath;
            return true;
        }
        Note(state, Int(state["index"]), error);
        Finish(s, loop, state, started, logs, failedRun: true);
        return false;
    }

    private static StepLog Log(StepDef step, string name, string status, int attempt, JsonNode? input, JsonNode? output, string? error, DateTime started, int ms) =>
        new(step.Id, step.Type, step.App, step.Action, name, status, attempt,
            input is null ? null : Tokens.Clip(input), output is null ? null : Tokens.Clip(output),
            error is null ? null : error.Length > 1000 ? error[..1000] : error, started, ms);

    private static DateTime? Utc(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var str)
        && DateTime.TryParse(str, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var d)
            ? DateTime.SpecifyKind(d, DateTimeKind.Utc) : null;

    public static string Words(TimeSpan t) =>
        t.TotalMinutes >= 60 ? $"{Math.Round(t.TotalHours, 1):0.#} h"
        : t.TotalSeconds >= 60 ? $"{Math.Round(t.TotalMinutes):0} min"
        : $"{Math.Round(t.TotalSeconds):0} s";
}
