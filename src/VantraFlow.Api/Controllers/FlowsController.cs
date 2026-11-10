using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Flows: the list, the builder, switching on and off, and testing with sample data.
/// <code>
/// GET    api/w/{ws}/flows                         → summaries with last run and 7-day counts
/// POST   api/w/{ws}/flows                         { name, description, definition, templateId } → 201
/// GET    api/w/{ws}/flows/{id}
/// PUT    api/w/{ws}/flows/{id}                    { name, description, definition } → { flow, errors }
/// POST   api/w/{ws}/flows/{id}/enable             { enabled } → the flow, or 422 { errors }
/// DELETE api/w/{ws}/flows/{id}
/// POST   api/w/{ws}/flows/{id}/hook-secret        { signed } → { secret } once (a webhook trigger's HMAC secret)
/// POST   api/w/{ws}/flows/{id}/sample             { definition } → { trigger, real } — data to test with
/// POST   api/w/{ws}/flows/{id}/test               { definition, trigger? } → the test run, every step for real
/// POST   api/w/{ws}/flows/{id}/test-step          { definition, stepId, data } → that step's log line
/// </code>
/// </summary>
public sealed class FlowsController(
    WorkspacesRepository workspaces, FlowsRepository flows, EngineRepository engine, Connections connections, IStepActions actions,
    FlowSecrets secrets, IConfiguration config) : FlowControllerBase(workspaces)
{
    private FlowLimits Limits => FlowLimits.From(config);

    [HttpGet("api/w/{workspaceId:guid}/flows")]
    public async Task<ActionResult<IReadOnlyList<FlowSummaryDto>>> List(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var stats = (await flows.StatsAsync(workspaceId, DateTime.UtcNow.AddDays(-7))).ToDictionary(s => s.FlowId);
        return Ok((await flows.ListAsync(workspaceId)).Select(f => Summary(f, stats.GetValueOrDefault(f.Id))).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/flows")]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] SaveFlowBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var def = Definition(body.Definition) ?? new FlowDefinition();
        var name = Name(body.Name) ?? "Untitled flow";
        var now = DateTime.UtcNow;
        var row = Shape(new FlowRow(FlowSecrets.NewRowId("flw_"), workspaceId, name, Desc(body.Description), false, def.ToJson(), 1, "", "", null, null, null,
            body.TemplateId is { Length: <= 40 } t ? t : null, null, Sub, now, Sub, now, null, null), def);
        await flows.InsertAsync(row);
        var saved = (await flows.GetAsync(workspaceId, row.Id))!;
        return StatusCode(201, new { flow = await DtoAsync(saved), errors = DefinitionRules.Validate(def, Limits) });
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Get(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        return await flows.GetAsync(workspaceId, id) is { } f ? Ok(await DtoAsync(f)) : NoFlow();
    }

    [HttpPut("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Save(Guid workspaceId, string id, [FromBody] SaveFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var errors = DefinitionRules.Validate(def, Limits);
        if (f.Enabled && errors.Count > 0)
            return UnprocessableEntity(new { error = "Fix these before saving, or switch the flow off first.", errors });
        var updated = Shape(f with { Name = Name(body.Name) ?? f.Name, Description = body.Description is null ? f.Description : Desc(body.Description), UpdatedBy = Sub, UpdatedAt = DateTime.UtcNow }, def);
        await flows.UpdateAsync(updated);
        if (f.Enabled)
        {
            // The trigger may now listen elsewhere: keep the apps' webhooks in step.
            try { await SyncAsync(workspaceId, f.TriggerApp, updated.TriggerApp, ct); }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
            {
                await flows.SetEnabledAsync(workspaceId, id, false, Sub);
                return StatusCode(502, new { error = $"Saved, but switched off: {e.Message}", code = "subscribe_failed" });
            }
        }
        return Ok(new { flow = await DtoAsync((await flows.GetAsync(workspaceId, id))!), errors });
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/enable")]
    public async Task<IActionResult> Enable(Guid workspaceId, string id, [FromBody] ToggleFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        if (body.Enabled)
        {
            var errors = DefinitionRules.Validate(def, Limits).ToList();
            foreach (var app in FlowCatalog.AppsNeeded(def))
                if (await connections.OpenAsync(workspaceId, app) is null)
                    errors.Add(new DefinitionError(null, $"Connect {(app == "orbit" ? "Vantra Orbit" : "Vantra Projects")} first."));
            if (errors.Count > 0) return UnprocessableEntity(new { error = "This flow can't run yet.", errors });
        }
        await flows.SetEnabledAsync(workspaceId, id, body.Enabled, Sub);
        try { await SyncAsync(workspaceId, f.TriggerApp, f.TriggerApp, ct); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
        {
            if (body.Enabled)
            {
                await flows.SetEnabledAsync(workspaceId, id, false, Sub);
                return StatusCode(502, new { error = e.Message, code = "subscribe_failed" });
            }
        }
        return Ok(await DtoAsync((await flows.GetAsync(workspaceId, id))!));
    }

    [HttpDelete("api/w/{workspaceId:guid}/flows/{id}")]
    public async Task<IActionResult> Delete(Guid workspaceId, string id, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoContent();
        await flows.DeleteAsync(workspaceId, id);
        try { await SyncAsync(workspaceId, f.TriggerApp, f.TriggerApp, ct); }
        catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException) { /* tidied on the next change */ }
        return NoContent();
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/hook-secret")]
    public async Task<IActionResult> HookSecret(Guid workspaceId, string id, [FromBody] HookSecretBody body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { HookId: not null } f) return NotFound(new { error = "This flow has no webhook trigger." });
        string? secret = body.Signed ? FlowSecrets.NewSecret() : null;
        await flows.UpdateAsync(f with { HookSecretCipher = secret is null ? null : secrets.Protect(secret, workspaceId, f.HookId), UpdatedBy = Sub, UpdatedAt = DateTime.UtcNow });
        return Ok(new { secret });
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/sample")]
    public async Task<IActionResult> Sample(Guid workspaceId, string id, [FromBody] TestFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var real = await connections.SampleAsync(workspaceId, def.Trigger, ct);
        return Ok(new { trigger = real ?? FlowCatalog.Sample(def.Trigger, DateTime.UtcNow), real = real is not null });
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/test")]
    public async Task<IActionResult> Test(Guid workspaceId, string id, [FromBody] TestFlowBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NoFlow();
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var errors = DefinitionRules.Validate(def, Limits);
        if (errors.Count > 0) return UnprocessableEntity(new { error = "Fix these to test the flow.", errors });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        var trigger = body.Trigger as JsonObject ?? await connections.SampleAsync(workspaceId, def.Trigger, ct) ?? FlowCatalog.Sample(def.Trigger, now);
        var run = EventProcessor.NewRun(f, def, ws, trigger, now, null, null, test: true);
        await engine.InsertRunAsync(run);
        var state = FlowWorker.State(run)!;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(60));
        IReadOnlyList<StepLog> logs;
        try { logs = await new RunExecutor(actions, Limits).ExecuteAsync(state, budget.Token, maxSteps: Limits.MaxSteps + 5); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logs = [];
            state.Status = RunStatuses.Failed;
            state.Error = "The test took longer than a minute.";
        }
        await engine.SaveSliceAsync(state, logs, now, DateTime.UtcNow);
        return Ok(await RunsController.DetailAsync(engine, workspaceId, run.Id));
    }

    [HttpPost("api/w/{workspaceId:guid}/flows/{id}/test-step")]
    public async Task<IActionResult> TestStep(Guid workspaceId, string id, [FromBody] TestStepBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (Definition(body.Definition) is not { } def) return BadRequest(new { error = "That flow couldn't be read." });
        var step = def.AllSteps().FirstOrDefault(s => s.Id == body.StepId);
        if (step is null) return NotFound(new { error = "That step isn't in this flow." });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var runId = FlowSecrets.NewRowId("tst_");
        var data = body.Data as JsonObject ?? RunState.NewData(FlowCatalog.Sample(def.Trigger, DateTime.UtcNow), runId, DateTime.UtcNow, ws.Name, ws.TimeZone, true);
        // Just this step: a one-step flow over the data the earlier steps' tests left.
        var single = new FlowDefinition { Trigger = def.Trigger, Steps = [Clone(step)], Options = def.Options };
        var state = new RunState
        {
            Id = runId, WorkspaceId = workspaceId, FlowId = id, Definition = single, Data = (JsonObject)data.DeepClone(),
            Position = Positions.First, Test = true, NotBefore = DateTime.UtcNow,
        };
        var logs = await new RunExecutor(actions, Limits).ExecuteAsync(state, ct, maxSteps: 1);
        var log = logs.FirstOrDefault();
        return Ok(new
        {
            status = log?.Status ?? state.Status,
            input = Parse(log?.Input),
            output = state.Steps[step.Id]?.DeepClone(),
            error = log?.Error ?? state.Error,
            durationMs = log?.DurationMs ?? 0,
        });
    }

    // ── Helpers ──

    /// <summary>Sync the apps whose webhooks a change may affect (the trigger's app before and after).</summary>
    private async Task SyncAsync(Guid workspaceId, string before, string after, CancellationToken ct)
    {
        foreach (var app in new[] { before, after }.Distinct())
            if (app is "orbit" or "projects") await connections.SyncSubscriptionsAsync(workspaceId, app, ct);
    }

    /// <summary>The row's trigger columns from its definition, and a webhook trigger's address made when it first needs one.</summary>
    private static FlowRow Shape(FlowRow f, FlowDefinition def)
    {
        var t = def.Trigger;
        var scope = t.App == "orbit" ? Tokens.Text(t.Config["tableId"]) is { Length: > 0 and <= 64 } s ? s : null : null;
        var hook = t.App == "webhook" ? f.HookId ?? FlowSecrets.NewId("fhk_") : null;
        return f with
        {
            Definition = def.ToJson(), TriggerApp = t.App.Length > 0 ? t.App : "none", TriggerEvent = t.Event.Length > 0 ? t.Event : "none",
            TriggerScope = scope, HookId = hook, HookSecretCipher = hook is null ? null : f.HookSecretCipher,
        };
    }

    private async Task<FlowDto> DtoAsync(FlowRow f)
    {
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        var tz = (await Workspaces.GetAsync(f.WorkspaceId))?.TimeZone;
        var next = f.Enabled && def.Trigger.App == "schedule" ? ScheduleRules.Next(def.Trigger, tz, DateTime.UtcNow) : null;
        return new FlowDto(f.Id, f.Name, f.Description, f.Enabled, JsonSerializer.SerializeToNode(def, FlowDefinition.Json)!, f.Version,
            f.HookId is null ? null : $"{connections.ApiBase}/api/hooks/{f.HookId}", f.HookSecretCipher is not null, f.PausedReason, f.TemplateId,
            Iso(next), Iso(f.UpdatedAt), Iso(f.LastRunAt), f.LastRunStatus);
    }

    private static FlowSummaryDto Summary(FlowRow f, FlowStatsRow? s)
    {
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        var apps = new List<string> { def.Trigger.App };
        apps.AddRange(def.AllSteps().Where(x => x.Type == StepTypes.Action && x.App is not null).Select(x => x.App!));
        return new FlowSummaryDto(f.Id, f.Name, f.Description, f.Enabled, f.TriggerApp, f.TriggerEvent, apps.Where(a => a.Length > 0).Distinct().ToList(),
            def.AllSteps().Count(), Iso(f.LastRunAt), f.LastRunStatus, s?.Runs ?? 0, s?.Failures ?? 0, Iso(f.UpdatedAt), f.PausedReason, f.TemplateId);
    }

    private static FlowDefinition? Definition(JsonNode? node)
    {
        if (node is not JsonObject) return null;
        try { return node.Deserialize<FlowDefinition>(FlowDefinition.Json); }
        catch (JsonException) { return null; }
    }

    private static StepDef Clone(StepDef s) => JsonSerializer.Deserialize<StepDef>(JsonSerializer.Serialize(s, FlowDefinition.Json), FlowDefinition.Json)!;

    private static JsonNode? Parse(string? json)
    {
        if (json is null) return null;
        try { return JsonNode.Parse(json); } catch (JsonException) { return JsonValue.Create(json); }
    }

    private static string? Name(string? n) => n?.Trim() is { Length: > 0 } s ? (s.Length > 160 ? s[..160] : s) : null;
    private static string? Desc(string? d) => d?.Trim() is { Length: > 0 } s ? (s.Length > 400 ? s[..400] : s) : null;
    private NotFoundObjectResult NoFlow() => NotFound(new { error = "That flow doesn't exist any more.", code = "no_flow" });
}
