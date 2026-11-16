using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Schedule;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The Schedule page: everything set to run on its own, across Flow, Orbit and Projects (<see cref="ScheduleBoard"/>).
/// <code>
/// GET  api/w/{ws}/schedule?from=&amp;to=&amp;zone=          → { items, paused, orbit: { connected, available, reason }, notices } (45 days at most)
/// POST api/w/{ws}/schedule/flows/{id}/run           → { runId } a schedule flow's run now (as the developer API's Run now: the plan applies)
/// POST api/w/{ws}/schedule/reports/{id}/enabled     { enabled } → { schedule } (Orbit's report: pause, resume)
/// POST api/w/{ws}/schedule/reports/{id}/send        → { status } (Orbit sends it to its recipients now; Orbit rate-limits it)
/// </code>
/// Members of the workspace see what Flow shows them elsewhere; Orbit's reports only as the person who connected Orbit sees
/// them there, and changing one needs their rights in Orbit (Orbit checks). Pausing a flow is the flows' own switch. Nothing
/// from a record, and no address, is logged or returned.
/// </summary>
public sealed class ScheduleController(
    IWorkspaceStore workspaces, ScheduleBoard board, ScheduleCache cache, FlowsRepository flows, EngineRepository engine, Services.Billing.Plans plans,
    FlowSignal signal, Connections connections, IConfiguration config, ILogger<ScheduleController> log) : FlowControllerBase(workspaces)
{
    private static DateTime? When(string? iso) =>
        DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    [HttpGet("api/w/{workspaceId:guid}/schedule")]
    public async Task<IActionResult> Get(Guid workspaceId, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] string? zone, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var ws = await Workspaces.GetAsync(workspaceId);
        if (ws is null) return NotFound(new { error = "That workspace isn't one of yours.", code = "no_workspace" });
        var now = DateTime.UtcNow;
        var a = When(from) ?? now;
        var b = When(to) ?? a.AddDays(14);
        var viewZone = Services.Triggers.DateRules.FindZone(zone) is not null ? zone! : ws.TimeZone;
        return Ok(await board.BuildAsync(workspaceId, ws, a, b, viewZone, now, ct));
    }

    [HttpPost("api/w/{workspaceId:guid}/schedule/flows/{id}/run")]
    public async Task<IActionResult> RunNow(Guid workspaceId, string id)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await flows.GetAsync(workspaceId, id) is not { } f) return NotFound(new { error = "That flow isn't there any more.", code = "no_flow" });
        if (FlowDefinition.Parse(f.Definition) is not { } def || def.Trigger.App != "schedule")
            return BadRequest(new { error = "Only a scheduled flow can be run from here.", code = "not_scheduled" });
        var errors = DefinitionRules.Validate(def, FlowLimits.From(config));
        if (errors.Count > 0) return UnprocessableEntity(new { error = "Fix this flow before running it.", errors });
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var now = DateTime.UtcNow;
        if (await plans.AdmitRunAsync(ws, def, now) is { } refused)
            return StatusCode(402, new { error = refused.Message, code = refused.Code, upgradeTo = refused.UpgradeTo });
        var run = EventProcessor.NewRun(f, def, ws, ApiV1Controller.Trigger(def, null, now, ws.TimeZone), now, null, null, test: false);
        await engine.InsertRunAsync(run);
        signal.Poke();
        return Ok(new { runId = run.Id });
    }

    public sealed record EnabledBody(bool? Enabled);

    private ObjectResult FromOrbit(Exception e) => e switch
    {
        AppRefusedException { Status: 409 } r when r.Code == "not_connected" => StatusCode(409, new { error = r.Message, code = "not_connected" }),
        AppRefusedException { Status: 403 } r => StatusCode(403, new { error = r.Message, code = "forbidden" }),
        AppRefusedException { Status: 404 } => StatusCode(404, new { error = "That report isn't there any more, or the person who connected Orbit can't see it.", code = "no_report" }),
        AppRefusedException r => StatusCode(400, new { error = r.Message, code = r.Code ?? "refused" }),
        AppUnavailableException { Status: 429 } => StatusCode(429, new { error = "It was sent a moment ago. Try again in a few minutes.", code = "rate_limited" }),
        AppRevokedException => StatusCode(409, new { error = e.Message, code = "not_connected" }),
        _ => StatusCode(502, new { error = "Vantra Orbit couldn't be reached. Try again in a minute.", code = "orbit_unavailable" }),
    };

    private static bool OrbitId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    [HttpPost("api/w/{workspaceId:guid}/schedule/reports/{id}/enabled")]
    public async Task<IActionResult> ReportEnabled(Guid workspaceId, string id, [FromBody] EnabledBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!OrbitId(id)) return NotFound(new { error = "That report isn't there any more.", code = "no_report" });
        if (body.Enabled is not { } enabled) return BadRequest(new { error = "Say whether it's on." });
        try
        {
            var res = await connections.CallAsync(workspaceId, "orbit", (api, key) => ((OrbitApi)api).SetReportEnabledAsync(key, id, enabled, ct));
            cache.Forget(workspaceId);
            log.LogInformation("Workspace {Workspace}: Orbit report {Id} {Change} from the Schedule page", workspaceId, id, enabled ? "resumed" : "paused");
            return Ok(new { schedule = res["schedule"]?.DeepClone() is JsonObject s ? ScheduleBoard.Report(s, TimeZoneInfo.Utc, DateTime.MinValue, DateTime.MaxValue) : null });
        }
        catch (Exception e) when (e is AppRefusedException or AppUnavailableException or AppRevokedException) { return FromOrbit(e); }
    }

    [HttpPost("api/w/{workspaceId:guid}/schedule/reports/{id}/send")]
    public async Task<IActionResult> ReportSend(Guid workspaceId, string id, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!OrbitId(id)) return NotFound(new { error = "That report isn't there any more.", code = "no_report" });
        try
        {
            var res = await connections.CallAsync(workspaceId, "orbit", (api, key) => ((OrbitApi)api).SendReportNowAsync(key, id, ct));
            log.LogInformation("Workspace {Workspace}: Orbit report {Id} sent now from the Schedule page", workspaceId, id);
            return Ok(new { status = res["status"]?.GetValue<string>() ?? "sent" });
        }
        catch (Exception e) when (e is AppRefusedException or AppUnavailableException or AppRevokedException) { return FromOrbit(e); }
    }
}
