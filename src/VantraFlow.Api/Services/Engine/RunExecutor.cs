using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>One action to carry out: which step, its input with the tokens filled in, and for which run.</summary>
/// <summary><c>TimeoutSeconds</c>: how long this try may take (the step's own "On error" timeout, or the server's).</summary>
public sealed record StepCall(Guid WorkspaceId, string RunId, string FlowId, StepDef Step, JsonObject Input, bool Test, int Depth = 0, string? TimeZone = null,
    int? TimeoutSeconds = null);

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
        ["vars"] = new JsonObject(),
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
public sealed class RunExecutor(IStepActions actions, FlowLimits limits, Func<DateTime>? clock = null, IApprovalGate? approvals = null)
{
    private readonly Func<DateTime> now = clock ?? (() => DateTime.UtcNow);

    public const int DefaultRetries = ErrorRules.DefaultRetries;
    public const int DefaultBackoffSeconds = ErrorRules.DefaultBackoffSeconds;

    /// <summary>The wait before try <paramref name="attempt"/> + 1: the base, doubling, at most an hour.</summary>
    public static TimeSpan Backoff(int baseSeconds, int attempt) =>
        TimeSpan.FromSeconds(Math.Min(Math.Max(5, baseSeconds) * Math.Pow(2, Math.Max(0, attempt - 1)), 3600));

    public static string NameOf(StepDef s) => !string.IsNullOrWhiteSpace(s.Name) ? s.Name!.Trim() : s.Type switch
    {
        StepTypes.Delay => "Delay",
        StepTypes.Branch => "If / else",
        StepTypes.Loop => "Repeat for each",
        StepTypes.Paths => "Paths",
        StepTypes.Filter => "Only continue if",
        StepTypes.Formatter => "Format",
        StepTypes.Variable => "Set variable",
        StepTypes.Wait => "Wait until",
        StepTypes.Approval => "Approval",
        _ => FlowCatalog.Action(s.App, s.Action)?.Title ?? "Step",
    };

    /// <param name="slice">
    /// How long a go may keep starting steps (the worker's, well inside its lease: a step may take up to
    /// <see cref="FlowLimits.MaxStepTimeoutSeconds"/>); the rest is picked up straight after. Null: no limit but <paramref name="maxSteps"/>.
    /// </param>
    public async Task<IReadOnlyList<StepLog>> ExecuteAsync(RunState s, CancellationToken ct, int maxSteps = 50, TimeSpan? slice = null)
    {
        var logs = await GoAsync(s, ct, maxSteps, slice);
        if (s.Status == RunStatuses.Failed && Positions.StepAt(s.Definition, s.Position) is { } failed) Remember(s, failed);
        return logs;
    }

    private async Task<List<StepLog>> GoAsync(RunState s, CancellationToken ct, int maxSteps, TimeSpan? slice)
    {
        var logs = new List<StepLog>();
        s.Status = RunStatuses.Running;
        var elapsed = Stopwatch.StartNew();
        for (var n = 0; n < maxSteps; n++)
        {
            ct.ThrowIfCancellationRequested();
            if (n > 0 && slice is { } budget && elapsed.Elapsed >= budget) break;
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
                case StepTypes.Paths:
                    Paths(s, step, started, logs);
                    break;
                case StepTypes.Filter:
                    if (!Filter(s, step, started, logs)) return logs;
                    break;
                case StepTypes.Formatter:
                case StepTypes.Variable:
                    if (!Transform(s, step, started, logs)) return logs;
                    break;
                case StepTypes.Wait:
                    if (!await WaitAsync(s, step, started, logs, ct)) return logs;
                    break;
                case StepTypes.Approval:
                    if (!await ApprovalAsync(s, step, started, logs, ct)) return logs;
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

    /// <summary>
    /// A failed run notes where it failed (<c>failure</c> in its data: the step and its position; inside a Repeat for
    /// each, also the loop as it was and the item), so "Retry from here" can pick up there (<see cref="RetryRules"/>).
    /// </summary>
    private static void Remember(RunState s, StepDef step)
    {
        if (s.Data["failure"] is JsonObject) return;
        s.Data["failure"] = new JsonObject { ["stepId"] = step.Id, ["position"] = s.Position };
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
        if (DelayRules.IsUntil(step.Config))
        {
            var input = Tokens.RenderNode(step.Config, s.Data) as JsonObject ?? new JsonObject();
            string? why = null;
            if (DelayRules.UntilOf(input, ZoneOf(s)) is not { } at) why = $"“{Tokens.Text(input["at"])}” isn't a date and time.";
            else if (at - started > FlowLimits.MaxDelay) why = "A delay can wait at most 30 days.";
            else if (s.Test || at <= started)
            {
                s.Steps[step.Id] = s.Test ? new JsonObject { ["skipped"] = true, ["until"] = at.ToString("o") } : new JsonObject { ["waitedUntil"] = at.ToString("o") };
                logs.Add(Log(step, name, s.Test ? "skipped" : "succeeded", 0, input, s.Steps[step.Id],
                    s.Test ? "Delays are skipped in a test." : "That time had already passed, so the run carried straight on.", started, 0));
                s.Position = Positions.After(s.Definition, s.Position!);
                return true;
            }
            else
            {
                s.Steps[step.Id] = new JsonObject { ["until"] = at.ToString("o") };
                logs.Add(Log(step, name, "waiting", 0, input, s.Steps[step.Id], null, started, 0));
                s.Status = RunStatuses.Waiting;
                s.NotBefore = at;
                return false;
            }
            logs.Add(Log(step, name, "failed", 0, input, null, why, started, 0));
            if (ItemFailed(s, why!, started, logs)) return true;
            s.Status = RunStatuses.Failed;
            s.Error = $"{name}: {why}";
            return false;
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
        s.Steps[step.Id] = new JsonObject { ["result"] = result, ["path"] = result ? "then" : "else" };
        logs.Add(Log(step, NameIn(s, step), "succeeded", 0, Conditions.Explain(cond, s.Data), s.Steps[step.Id], null, started, 0));
        s.Position = Positions.Enter(s.Definition, s.Position!, result);
    }

    /// <summary>Paths: down the first path whose condition holds, else "otherwise" (when on), else on past the step.</summary>
    private void Paths(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var paths = step.Paths ?? [];
        int? chosen = null;
        var tried = new JsonArray();
        for (var i = 0; i < paths.Count; i++)
        {
            var cond = Conditions.Parse(paths[i].Condition);
            var hit = cond is not null && Conditions.Evaluate(cond, s.Data);
            var explained = Conditions.Explain(cond, s.Data);
            explained["path"] = PathRules.NameOf(paths[i], i);
            explained["passed"] = hit;
            tried.Add(explained);
            if (hit) { chosen = i; break; }
        }
        var otherwise = chosen is null && PathRules.HasOtherwise(step);
        var name = chosen is { } c ? PathRules.NameOf(paths[c], c) : otherwise ? "Otherwise" : null;
        s.Steps[step.Id] = new JsonObject { ["path"] = name, ["number"] = chosen is { } n ? n + 1 : null, ["matched"] = chosen is not null };
        logs.Add(Log(step, NameIn(s, step), "succeeded", 0, new JsonObject { ["paths"] = tried }, s.Steps[step.Id],
            name is null ? "No path matched, so the run went on past them." : null, started, 0));
        s.Position = Positions.EnterPath(s.Definition, s.Position!, chosen ?? (otherwise ? -1 : null));
    }

    /// <summary>
    /// "Only continue if": when the condition holds the run goes on; when it doesn't, the run stops here and succeeds
    /// (inside a Repeat for each, just this item stops and the next one starts). False when the run stops.
    /// </summary>
    private bool Filter(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var cond = Conditions.Parse(step.Config["condition"]);
        var passed = cond is not null && Conditions.Evaluate(cond, s.Data);
        s.Steps[step.Id] = new JsonObject { ["passed"] = passed };
        var loopPath = Positions.LoopOf(s.Position);
        var note = passed ? null : loopPath is null ? "It didn't pass, so the run stopped here." : "It didn't pass, so this item stopped here.";
        logs.Add(Log(step, NameIn(s, step), "succeeded", 0, Conditions.Explain(cond, s.Data), s.Steps[step.Id], note, started, 0));
        if (passed) { s.Position = Positions.After(s.Definition, s.Position!); return true; }
        if (loopPath is not null && Positions.StepAt(s.Definition, loopPath) is { Type: StepTypes.Loop } loop && s.Steps[loop.Id] is JsonObject st && Running(st))
        {
            st["itemSkipped"] = true;
            s.Position = loopPath;
            return true;
        }
        s.Position = null;
        s.Status = RunStatuses.Succeeded;
        return false;
    }

    /// <summary>A formatter or a Set variable step: worked out here, from the run's data. False when it fails the run.</summary>
    private bool Transform(RunState s, StepDef step, DateTime started, List<StepLog> logs)
    {
        var input = Tokens.RenderNode(step.Config, s.Data) as JsonObject ?? new JsonObject();
        JsonObject? output;
        string? error;
        if (step.Type == StepTypes.Formatter)
            (output, error) = Formatter.Run(input, Tokens.Text(s.Data["workspace"]?["timeZone"]) is { Length: > 0 } tz ? tz : null);
        else
            (output, error) = VariableRules.Apply(input, s.Data["vars"] as JsonObject ?? (JsonObject)(s.Data["vars"] = new JsonObject()));
        if (output is not null)
        {
            s.Steps[step.Id] = output;
            logs.Add(Log(step, NameIn(s, step), "succeeded", 0, input, output, null, started, 0));
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        logs.Add(Log(step, NameIn(s, step), "failed", 0, input, null, error, started, 0));
        if (ItemFailed(s, error ?? "Failed.", started, logs)) return true;
        s.Status = RunStatuses.Failed;
        s.Error = $"{NameIn(s, step)}: {error}";
        return false;
    }

    /// <summary>
    /// False when the run stops here (failed, or waiting to retry). Its "On error" settings (<see cref="ErrorRules"/>)
    /// say how long a try may take, how often a failure is tried again, and what happens when it still fails.
    /// </summary>
    private async Task<bool> ActionAsync(RunState s, StepDef step, DateTime started, List<StepLog> logs, CancellationToken ct)
    {
        var name = NameIn(s, step);
        // Back from its error path: the step failed for good, so the run stops here (inside a Repeat for each, the item).
        if (ErrorRules.Handling(s.Steps[step.Id]) is { } handled)
        {
            handled.Remove("handling");
            var why = Tokens.Text(handled["error"]?["message"]);
            if (ItemFailed(s, why, started, logs)) return true;
            s.Status = RunStatuses.Failed;
            s.Error = $"{name}: {why}";
            return false;
        }
        var htmlKeys = step.App == "email" ? new HashSet<string> { "body" } : null;
        var input = Tokens.RenderNode(step.Config, s.Data, htmlKeys) as JsonObject ?? new JsonObject();
        // An AI step writes its answer as it goes, so unless its "On error" sets its own timeout it gets longer than an app's call.
        var seconds = step.TimeoutSeconds is null && Ai.AiStepRules.IsAi(step)
            ? Math.Max(limits.StepTimeoutSeconds, Ai.AiStepRules.TimeoutSeconds) : ErrorRules.TimeoutOf(step, limits);
        var clock = Stopwatch.StartNew();
        StepOutcome outcome;
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                outcome = await actions.RunAsync(new StepCall(s.WorkspaceId, s.Id, s.FlowId, step, input, s.Test, SubFlowRules.DepthOf(s.Data),
                    Tokens.Text(s.Data["workspace"]?["timeZone"]) is { Length: > 0 } tz ? tz : null, seconds), timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                outcome = StepOutcome.Fail($"No answer within {seconds} seconds.", retryable: true);
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
        var policy = ErrorRules.RetryOf(step);
        var retries = s.Test ? 0 : Math.Min(policy.Retries, Math.Max(0, limits.MaxAttempts - 1));
        if (ErrorRules.Retries(policy, outcome) && s.Attempt < retries)
        {
            s.Attempt++;
            var wait = ErrorRules.WaitBefore(policy, s.Attempt);
            logs.Add(Log(step, name, "retrying", s.Attempt - 1, input, null, $"{outcome.Error} Trying again in {Words(wait)}.", started, ms));
            s.Status = RunStatuses.Queued;
            s.NotBefore = now().Add(wait);
            return false;
        }
        var error = outcome.Error ?? "Failed.";
        var attempts = s.Attempt + 1;
        var mode = ErrorRules.ModeOf(step);
        var failure = mode == ErrorRules.Stop ? null : ErrorRules.Failure(error, name, attempts, started);
        // The log line says what happened next: carried on, or down the error path.
        var logged = failure is null ? null : new JsonObject { ["onError"] = mode, ["error"] = failure["error"]!.DeepClone() };
        logs.Add(Log(step, name, "failed", s.Attempt, input, logged, outcome.Error, started, ms));
        s.Attempt = 0;
        if (mode == ErrorRules.Continue)
        {
            s.Steps[step.Id] = failure;
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        if (mode == ErrorRules.Path && Positions.EnterErrorPath(s.Definition, s.Position!) is { } into)
        {
            failure!["handling"] = true;
            s.Steps[step.Id] = failure;
            s.Position = into;
            return true;
        }
        if (ItemFailed(s, error, started, logs)) return true;
        s.Status = RunStatuses.Failed;
        s.Error = $"{name}: {outcome.Error}";
        return false;
    }

    private static string? ZoneOf(RunState s) => Tokens.Text(s.Data["workspace"]?["timeZone"]) is { Length: > 0 } tz ? tz : null;

    // ── Wait until ──

    /// <summary>
    /// "Wait until": runs the refresh step again (when one is named), checks the condition, and carries on when it holds;
    /// otherwise waits until the next check, or past the deadline goes on (or stops) with <c>met: false</c>. False when the
    /// run now waits or stops.
    /// </summary>
    private async Task<bool> WaitAsync(RunState s, StepDef step, DateTime started, List<StepLog> logs, CancellationToken ct)
    {
        var name = NameIn(s, step);
        var prior = s.Steps[step.Id] as JsonObject;
        var first = prior is null || !(prior["waiting"] is JsonValue w && w.TryGetValue<bool>(out var on) && on);
        var deadline = !first && Utc(prior!["deadline"]) is { } d ? d : started.Add(WaitRules.TimeoutOf(step.Config));
        var checks = first ? 0 : Int(prior!["checks"]);
        string? refreshError = null;
        if (WaitRules.RefreshOf(step.Config) is { } rid && s.Definition.AllSteps().FirstOrDefault(x => x.Id == rid) is { Type: StepTypes.Action } again)
        {
            var rin = Tokens.RenderNode(again.Config, s.Data) as JsonObject ?? new JsonObject();
            StepOutcome o;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(limits.StepTimeoutSeconds));
                try { o = await actions.RunAsync(new StepCall(s.WorkspaceId, s.Id, s.FlowId, again, rin, s.Test, SubFlowRules.DepthOf(s.Data), ZoneOf(s)), timeout.Token); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { o = StepOutcome.Fail($"No answer within {limits.StepTimeoutSeconds} seconds.", true); }
            }
            s.StepCount++;
            if (o.Ok) s.Steps[rid] = o.Output?.DeepClone() ?? new JsonObject();
            else refreshError = o.Error;
        }
        checks++;
        var cond = Conditions.Parse(step.Config["condition"]);
        var met = cond is not null && Conditions.Evaluate(cond, s.Data);
        var explained = Conditions.Explain(cond, s.Data);
        if (met || s.Test || started >= deadline)
        {
            var timedOut = !met && !s.Test;
            var output = new JsonObject { ["met"] = met, ["timedOut"] = timedOut, ["checks"] = checks, ["checkedAt"] = started.ToString("o") };
            s.Steps[step.Id] = output;
            var note = met ? null
                : s.Test ? "A test checks once and doesn't wait."
                : WaitRules.Stops(step.Config) ? "It didn't hold in time, so the run stopped here." : "It didn't hold in time, so the run carried on.";
            if (refreshError is not null && !met) note = $"{note} The step it runs again failed: {refreshError}";
            logs.Add(Log(step, name, "succeeded", 0, explained, output, note, started, 0));
            if (timedOut && WaitRules.Stops(step.Config))
            {
                s.Position = null;
                s.Status = RunStatuses.Succeeded;
                return false;
            }
            s.Position = Positions.After(s.Definition, s.Position!);
            return true;
        }
        var next = started.Add(WaitRules.EveryOf(step.Config));
        if (next > deadline) next = deadline;
        var state = new JsonObject { ["waiting"] = true, ["deadline"] = deadline.ToString("o"), ["checks"] = checks, ["nextCheck"] = next.ToString("o") };
        if (refreshError is not null) state["lastError"] = refreshError.Length > 300 ? refreshError[..300] : refreshError;
        s.Steps[step.Id] = state;
        if (first) logs.Add(Log(step, name, "waiting", 0, explained, state, null, started, 0));
        s.Status = RunStatuses.Waiting;
        s.NotBefore = next;
        return false;
    }

    // ── Approval ──

    /// <summary>How often a run waiting on an approval looks again, should the decision's wake-up be missed.</summary>
    public static readonly TimeSpan ApprovalRecheck = TimeSpan.FromHours(1);

    /// <summary>
    /// "Approval": the first time, asks the approvers and waits; each time it comes round, reads the decision and goes
    /// down Approved or Rejected, or past the deadline Timed out. False when the run now waits or fails.
    /// </summary>
    private async Task<bool> ApprovalAsync(RunState s, StepDef step, DateTime started, List<StepLog> logs, CancellationToken ct)
    {
        var name = NameIn(s, step);
        ApprovalRules.Sides(step);
        if (s.Steps[step.Id] is JsonObject st && st["pending"] is JsonValue pv && pv.TryGetValue<bool>(out var pending) && pending
            && Tokens.Text(st["approvalId"]) is { Length: > 0 } id)
        {
            var until = Utc(st["until"]) ?? started;
            var now = approvals is null ? null : await approvals.StateAsync(s.WorkspaceId, id, ct);
            if (now is null) return FailApproval(s, step, name, "The approval request is gone.", started, logs);
            if (now.Status == "cancelled") return FailApproval(s, step, name, "The approval request was cancelled.", started, logs);
            string decision;
            if (now.Status == "pending")
            {
                if (started < until)
                {
                    s.Status = RunStatuses.Waiting;
                    s.NotBefore = started.Add(ApprovalRecheck) < until ? started.Add(ApprovalRecheck) : until;
                    return false;
                }
                await approvals!.ExpireAsync(id, started);
                decision = "timed_out";
            }
            else decision = now.Status switch { "approved" => "approved", "rejected" => "rejected", _ => "timed_out" };
            var output = new JsonObject
            {
                ["decision"] = decision,
                ["approved"] = decision == "approved",
                ["decidedBy"] = decision == "timed_out" ? null : now.DecidedByName,
                ["decidedAt"] = (now.DecidedAt ?? started).ToString("o"),
                ["comment"] = now.Comment,
                ["via"] = now.Via,
                ["approvalId"] = id,
                ["requestedAt"] = st["requestedAt"]?.DeepClone(),
                ["approvers"] = st["approvers"]?.DeepClone(),
            };
            s.Steps[step.Id] = output;
            var path = ApprovalRules.PathOf(decision);
            logs.Add(Log(step, name, "succeeded", 0, new JsonObject { ["title"] = Tokens.Text(st["title"]), ["approvers"] = st["approvers"]?.DeepClone() }, output,
                decision == "timed_out" ? "No one decided in time." : null, started, 0));
            s.Position = Positions.EnterPath(s.Definition, s.Position!, path);
            return true;
        }

        var input = Tokens.RenderNode(step.Config, s.Data) as JsonObject ?? new JsonObject();
        var title = Tokens.Text(input["title"]).Trim();
        if (title.Length > 200) title = title[..200];
        var details = Tokens.Text(input["details"]).Trim();
        if (details.Length > 4000) details = details[..4000];
        if (s.Test)
        {
            var output = new JsonObject
            {
                ["decision"] = "approved", ["approved"] = true, ["decidedBy"] = "A test", ["decidedAt"] = started.ToString("o"), ["test"] = true,
            };
            s.Steps[step.Id] = output;
            logs.Add(Log(step, name, "succeeded", 0, new JsonObject { ["title"] = title, ["details"] = details }, output,
                "A test takes the Approved path without asking anyone.", started, 0));
            s.Position = Positions.EnterPath(s.Definition, s.Position!, ApprovalRules.Approved);
            return true;
        }
        if (approvals is null) return FailApproval(s, step, name, "Approvals aren't available on this server yet.", started, logs);
        var expires = started.Add(ApprovalRules.TimeoutOf(step.Config));
        ApprovalState asked;
        try
        {
            asked = await approvals.RequestAsync(new ApprovalAsk(s.WorkspaceId, s.FlowId, s.Id, step.Id, title, details.Length > 0 ? details : null,
                ApprovalRules.ApproversOf(step.Config), expires), ct);
        }
        catch (ApprovalUnavailableException e) { return FailApproval(s, step, name, e.Message, started, logs); }
        var state = new JsonObject
        {
            ["pending"] = true, ["approvalId"] = asked.Id, ["until"] = asked.ExpiresAt.ToString("o"), ["requestedAt"] = started.ToString("o"), ["title"] = title,
            ["approvers"] = new JsonArray(asked.ApproverNames.Select(n => (JsonNode)n).ToArray()), ["notified"] = asked.Notified,
        };
        s.Steps[step.Id] = state;
        logs.Add(Log(step, name, "waiting", 0, new JsonObject { ["title"] = title, ["details"] = details }, state, null, started, 0));
        s.Status = RunStatuses.Waiting;
        s.NotBefore = started.Add(ApprovalRecheck) < asked.ExpiresAt ? started.Add(ApprovalRecheck) : asked.ExpiresAt;
        return false;
    }

    private static bool FailApproval(RunState s, StepDef step, string name, string why, DateTime started, List<StepLog> logs)
    {
        logs.Add(Log(step, name, "failed", 0, null, null, why, started, 0));
        s.Status = RunStatuses.Failed;
        s.Error = $"{name}: {why}";
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
            var skipped = st["itemSkipped"] is not null;
            st.Remove("itemError");
            st.Remove("itemSkipped");
            Note(st, index, error, skipped);
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
    private static void Note(JsonObject state, int index, string? error, bool skipped = false)
    {
        var items = state["items"] as JsonArray ?? [];
        var results = state["results"] as JsonArray ?? (JsonArray)(state["results"] = new JsonArray());
        var r = new JsonObject
        {
            ["number"] = index + 1,
            ["label"] = index < items.Count ? LoopRules.Label(items[index]) : "",
            ["status"] = error is not null ? "failed" : skipped ? "skipped" : "succeeded",
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
        var skipped = results.Count(r => Tokens.Text(r?["status"]) == "skipped");
        var output = new JsonObject
        {
            ["count"] = count,
            ["succeeded"] = results.Count - failed - skipped,
            ["failed"] = failed,
            ["skipped"] = skipped,
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
        // "Retry from here" picks up at this item: the loop as it was, and the item.
        if (Positions.StepAt(s.Definition, s.Position) is { } failedStep && s.Data["failure"] is null)
            s.Data["failure"] = new JsonObject
            {
                ["stepId"] = failedStep.Id, ["position"] = s.Position, ["loopId"] = loop.Id, ["loop"] = state.DeepClone(), ["item"] = s.Data["loop"]?.DeepClone(),
            };
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
