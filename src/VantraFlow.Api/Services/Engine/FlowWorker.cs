using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Engine;

/// <summary>The in-process nudge an incoming event gives the worker (lossy: the queue is in the database).</summary>
public sealed class FlowSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Poke() => channel.Writer.TryWrite(true);

    public async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        if (channel.Reader.TryRead(out _) || delay <= TimeSpan.Zero) return;
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timer.CancelAfter(delay);
        try
        {
            await channel.Reader.WaitToReadAsync(timer.Token);
            channel.Reader.TryRead(out _);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }
}

/// <summary>
/// Runs Flow's engine (db/001), modelled on Vantra Orbit's AutomationWorker. Every tick (2 s, or sooner when an
/// event arrives):
/// <list type="number">
/// <item><b>Events</b>: claim due events under a lease, start their runs (<see cref="EventProcessor"/>), delete
/// them — or put them back with backoff, and 'dead' with the error after the last try.</item>
/// <item><b>Runs</b>: claim due runs (new, past a delay, due a retry, or whose instance died), run their next
/// steps (<see cref="RunExecutor"/>) and save the log.</item>
/// <item><b>Each minute</b>: fire the schedule slots that have come due, each once (flow.FiredKeys), and send the
/// failure alerts that are due (<see cref="FailureAlerts"/>, db/002).</item>
/// <item><b>Every five minutes</b>: the plans sweep (<see cref="Billing.Plans.SweepAsync"/>, db/008).</item>
/// <item><b>Hourly</b>: prune old claims, dead events, and runs older than <c>Flow:KeepRunsDays</c>.</item>
/// </list>
/// Before db/001 it checks once a minute and does nothing else. Multi-instance safe; App Service needs
/// "Always On" so it keeps running with nobody signed in.
/// </summary>
public sealed class FlowWorker(IServiceScopeFactory scopes, FlowSignal signal, IConfiguration config, ILogger<FlowWorker> log) : BackgroundService
{
    public static readonly TimeSpan Tick = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan IdleTick = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ScheduleWindow = TimeSpan.FromMinutes(3);
    private readonly string me = Id();
    private DateTime nextMinute = DateTime.MinValue;
    private DateTime nextPrune = DateTime.MinValue;
    private DateTime nextPlans = DateTime.MinValue;
    private bool waitingLogged;

    private static string Id()
    {
        var s = $"{Environment.MachineName}:{Guid.NewGuid():N}";
        return s.Length <= 64 ? s : s[^64..];
    }

    /// <summary>The wait before retrying an event that failed <paramref name="attempts"/> times: 30 s, doubling, at most 30 minutes.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Math.Max(0, attempts - 1)), 1800));

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        await Task.Yield();
        var failures = 0;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan delay;
            try
            {
                using var scope = scopes.CreateScope();
                var sp = scope.ServiceProvider;
                if (!await sp.GetRequiredService<FlowSchema>().ReadyAsync())
                {
                    if (!waitingLogged) log.LogInformation("Flow worker: waiting for db/001 (checking once a minute)");
                    waitingLogged = true;
                    delay = IdleTick;
                }
                else
                {
                    if (waitingLogged) log.LogInformation("Flow worker: db/001 is in place, running flows");
                    waitingLogged = false;
                    var busy = await RunOnceAsync(sp, ct);
                    failures = 0;
                    delay = busy ? TimeSpan.FromMilliseconds(100) : Tick;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                failures++;
                if (failures == 1 || failures % 30 == 0) log.LogWarning(ex, "Flow worker tick failed ({Failures} in a row)", failures);
                delay = TimeSpan.FromMilliseconds(Math.Min(Tick.TotalMilliseconds * Math.Pow(2, Math.Min(failures - 1, 10)), IdleTick.TotalMilliseconds));
            }
            try { await signal.WaitAsync(delay, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> RunOnceAsync(IServiceProvider sp, CancellationToken ct)
    {
        var engine = sp.GetRequiredService<EngineRepository>();
        var limits = FlowLimits.From(config);
        var now = DateTime.UtcNow;

        // 1. Events → runs.
        var processor = new EventProcessor(sp.GetRequiredService<SqlEngineStore>(), limits);
        var events = await engine.ClaimEventsAsync(me, now, 25, Lease);
        foreach (var ev in events)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var outcome = await processor.ProcessAsync(ev);
                if (outcome.Done) await engine.CompleteEventAsync(ev.Seq);
                else await engine.ReleaseEventAsync(ev.Seq, outcome.DeferUntil ?? DateTime.UtcNow.AddMinutes(1), null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (ev.Attempts >= limits.MaxAttempts)
                {
                    log.LogWarning(ex, "Flow event {Event} failed {Attempts} times; left as dead", ev.Id, ev.Attempts);
                    await engine.DeadEventAsync(ev.Seq, ex.Message);
                }
                else
                {
                    log.LogInformation(ex, "Flow event {Event} failed (try {Attempts}); retrying", ev.Id, ev.Attempts);
                    await engine.ReleaseEventAsync(ev.Seq, DateTime.UtcNow.Add(Backoff(ev.Attempts)), ex.Message);
                }
            }
        }

        // 2. Runs.
        var executor = new RunExecutor(sp.GetRequiredService<IStepActions>(), limits, approvals: sp.GetService<IApprovalGate>());
        var runs = await engine.ClaimRunsAsync(me, DateTime.UtcNow, 10, Lease);
        foreach (var run in runs)
        {
            if (ct.IsCancellationRequested) break;
            var state = State(run);
            try
            {
                var logs = state is null ? [] : await executor.ExecuteAsync(state, ct, maxSteps: 10);
                state ??= Broken(run);
                await engine.SaveSliceAsync(state, logs, run.StartedAt, DateTime.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                // Something below the steps (the database) failed: the lease runs out and the run is picked up again.
                log.LogWarning(ex, "Flow run {Run} couldn't be saved; it will be picked up again", run.Id);
            }
        }

        // 3. Schedules, once a minute.
        if (now >= nextMinute)
        {
            nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc).AddMinutes(1).AddSeconds(2);
            try { await ScheduleAsync(sp, now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Schedule tick failed"); }
            try { await sp.GetRequiredService<FailureAlerts>().SendDueAsync(now, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Failure alerts tick failed"); }
            // Polling triggers (Outlook, Gmail, Google Sheets), once db/004 has run.
            try { await sp.GetRequiredService<OAuth.PollTriggers>().RunDueAsync(DateTime.UtcNow, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Poll tick failed"); }
        }

        // Plans (db/008), every five minutes: trials start once billing is live, owners hear before one ends, and
        // flows a smaller plan doesn't cover are switched off (never deleted).
        if (now >= nextPlans)
        {
            nextPlans = now.AddMinutes(5);
            try { await sp.GetRequiredService<Billing.Plans>().SweepAsync(DateTime.UtcNow, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { log.LogWarning(ex, "Plans sweep failed"); }
        }

        // 4. Housekeeping.
        if (now >= nextPrune)
        {
            nextPrune = now.AddHours(1);
            try
            {
                // Once db/006 has run, the runs it prunes are kept as each flow's daily totals (Insights reaches back 90 days).
                var keepDays = await sp.GetRequiredService<FlowSchema>().InsightsReadyAsync();
                await engine.PruneAsync(now, int.TryParse(config["Flow:KeepRunsDays"], out var d) ? Math.Clamp(d, 7, 365) : 30, keepDays);
            }
            catch (Exception ex) { log.LogInformation(ex, "Flow prune failed"); }
            try { if (await sp.GetRequiredService<FlowSchema>().ConnectorsReadyAsync()) await sp.GetRequiredService<OAuthRepository>().PruneAsync(now); }
            catch (Exception ex) { log.LogInformation(ex, "Connector prune failed"); }
            try
            {
                if (await sp.GetRequiredService<FlowSchema>().StepsReadyAsync())
                    await sp.GetRequiredService<StepsRepository>().PruneAsync(now, int.TryParse(config["Flow:KeepRunsDays"], out var keep) ? Math.Clamp(keep, 7, 365) : 30);
            }
            catch (Exception ex) { log.LogInformation(ex, "Approvals prune failed"); }
            try { if (await sp.GetRequiredService<FlowSchema>().PlansReadyAsync()) await sp.GetRequiredService<PlansRepository>().PruneAsync(now.AddDays(-30)); }
            catch (Exception ex) { log.LogInformation(ex, "Billing prune failed"); }
        }
        if (events.Count > 0 || runs.Count > 0) sp.GetRequiredService<FlowSignal>().Poke();
        return events.Count > 0 || runs.Count > 0;
    }

    private async Task ScheduleAsync(IServiceProvider sp, DateTime now, CancellationToken ct)
    {
        var flows = sp.GetRequiredService<FlowsRepository>();
        var engine = sp.GetRequiredService<EngineRepository>();
        foreach (var (flow, tz) in await flows.SchedulesAsync())
        {
            if (ct.IsCancellationRequested) break;
            var def = FlowDefinition.Parse(flow.Definition);
            if (def is null) continue;
            foreach (var slot in ScheduleRules.DueSlots(def.Trigger, tz, now, ScheduleWindow))
            {
                if (slot < flow.UpdatedAt.AddSeconds(-59)) continue; // only slots since it was last saved / switched on
                if (!await engine.ClaimKeyAsync(flow.WorkspaceId, flow.Id, ScheduleRules.Key(slot))) continue;
                var payload = new JsonObject { ["scheduledFor"] = slot.ToString("o"), ["timeZone"] = tz };
                await engine.EnqueueAsync(flow.WorkspaceId, EventSources.Schedule, flow.TriggerEvent, null, flow.Id, payload.ToJsonString(), false, now);
            }
        }
    }

    /// <summary>A claimed run as the executor needs it; null when its definition can't be read.</summary>
    public static RunState? State(RunRow run)
    {
        var def = FlowDefinition.Parse(run.Definition);
        if (def is null) return null;
        JsonObject data;
        try { data = JsonNode.Parse(run.Context ?? "{}") as JsonObject ?? new JsonObject(); }
        catch (JsonException) { data = new JsonObject(); }
        return new RunState
        {
            Id = run.Id, WorkspaceId = run.WorkspaceId, FlowId = run.FlowId, Definition = def, Data = data,
            Position = run.Position, Attempt = run.Attempt, StepCount = run.StepCount, Test = run.Test, NotBefore = run.NotBefore,
        };
    }

    private static RunState Broken(RunRow run) => new()
    {
        Id = run.Id, WorkspaceId = run.WorkspaceId, FlowId = run.FlowId, Definition = new FlowDefinition(), Data = new JsonObject(),
        Status = RunStatuses.Failed, Error = "This run's flow couldn't be read.", NotBefore = run.NotBefore,
    };
}
