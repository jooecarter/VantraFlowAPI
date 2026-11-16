using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Triggers;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Orbit and Projects asking Flow for someone signed in there, server to server (never a browser: no CORS), with a fresh
/// assertion each time (<c>X-App-Assertion</c>, <see cref="AppAssertion"/>):
/// <code>
/// POST api/apps/{orbit|projects}/v1/buttons   { target } → { flows: [{ id, name, label, subtitle }], newUrl }
/// POST api/apps/{orbit|projects}/v1/forms     → { forms: [{ title, description, url }] } (Requests)
/// POST api/apps/{orbit|projects}/v1/run       { flowId, target, recordIds (1–50) } → { flowName, runs: [{ recordId, runId, runUrl, error }] }
/// </code>
/// <c>target</c>: an Orbit table id, or <c>task</c> in Projects. Flow checks the assertion (signature, audience, expiry, a
/// nonce used once), that a Flow workspace links that app workspace and has the person as a member, and for a run that
/// the flow is on and is that app's button for that target (<see cref="AppLinks"/>). The app has checked that the person
/// can see each record. The record is read fresh through the workspace's connection, and the run happens as that
/// connection, as every Flow run does, with <c>trigger.runBy</c> saying who started it.
/// </summary>
[ApiController]
[AllowAnonymous]
[DisableCors]
[ServiceFilter(typeof(FlowReadyFilter))]
[EnableRateLimiting(RateLimitPolicy)]
[Route("api/apps/{app}/v1")]
public sealed class AppLinksController(
    AppAssertion assertion, AppLinks links, TriggersRepository triggers, FormLinks forms, Connections connections, IUserDirectory users,
    TriggerRuns runs, ILogger<AppLinksController>? log = null) : ControllerBase
{
    public const string RateLimitPolicy = "apps";

    private async Task<(AppCaller? Caller, IActionResult? Refusal)> CallerAsync(string app)
    {
        if (app is not ("orbit" or "projects")) return (null, NotFound(new { error = "There's nothing here.", code = "unknown" }));
        var (caller, why) = await assertion.ReadAsync(app, Request.Headers[AppAssertion.HeaderName]);
        if (caller is null)
        {
            log?.LogWarning("An app assertion from {App} didn't verify ({Reason})", app, why);
            return (null, Unauthorized(new { error = "The request couldn't be verified.", code = "assertion_invalid", reason = why }));
        }
        return (caller, null);
    }

    public sealed record ButtonsBody(string? Target);
    public sealed record RunBody(string? FlowId, string? Target, IReadOnlyList<string>? RecordIds);

    [HttpPost("buttons")]
    public async Task<IActionResult> Buttons(string app, [FromBody] ButtonsBody? body)
    {
        var (c, refusal) = await CallerAsync(app);
        if (c is null) return refusal!;
        var target = Target(app, body?.Target);
        if (target is null) return BadRequest(new { error = "Say which table.", code = "invalid" });
        var list = await links.ButtonsAsync(c, target);
        var query = app == "orbit" ? $"app=orbit&tableId={Uri.EscapeDataString(target)}" : "app=projects&target=task";
        return Ok(new
        {
            linked = list.Count > 0 || (await links.WorkspacesAsync(c)).Count > 0,
            flows = list.Select(f => new { id = f.Id, name = f.Name, label = f.Label, subtitle = f.Subtitle }),
            newUrl = $"{forms.AppBase}/flows/new?trigger=button.record&{query}",
        });
    }

    [HttpPost("forms")]
    public async Task<IActionResult> Forms(string app)
    {
        var (c, refusal) = await CallerAsync(app);
        if (c is null) return refusal!;
        if (!await triggers.ReadyAsync()) return Ok(new { forms = Array.Empty<object>() });
        var mine = await links.WorkspacesAsync(c);
        var rows = await triggers.RequestFormsAsync(mine);
        return Ok(new { forms = rows.Select(f => new { id = f.Id, title = f.Title, url = forms.Url(f) }) });
    }

    [HttpPost("run")]
    public async Task<IActionResult> Run(string app, [FromBody] RunBody? body, CancellationToken ct)
    {
        var (c, refusal) = await CallerAsync(app);
        if (c is null) return refusal!;
        var target = Target(app, body?.Target);
        var ids = (body?.RecordIds ?? []).Where(x => !string.IsNullOrWhiteSpace(x) && x.Length <= 80).Select(x => x.Trim()).Distinct().ToList();
        if (target is null || ids.Count == 0) return BadRequest(new { error = "Choose a record.", code = "invalid" });
        if (ids.Count > ButtonRules.MaxBulk) return BadRequest(new { error = $"Run a flow on at most {ButtonRules.MaxBulk} records at once.", code = "too_many" });
        var (flow, code) = await links.RunnableAsync(c, body?.FlowId ?? "", target);
        if (flow is null)
            return code == "not_linked"
                ? StatusCode(403, new { error = AppLinks.Message(code), code })
                : NotFound(new { error = AppLinks.Message(code!), code });

        var u = await users.FindByVantraIdAsync(c.VantraId);
        var name = $"{u?.FirstName} {u?.LastName}".Trim();
        var by = new FormPerson(c.VantraId, name.Length > 0 ? name : c.Name, u?.Email);
        var now = DateTime.UtcNow;
        JsonObject? table = null;
        var results = new JsonArray();
        foreach (var id in ids)
        {
            JsonObject? record;
            try
            {
                if (app == "orbit")
                {
                    table ??= await connections.CallAsync(flow.WorkspaceId, "orbit", async (api, key) =>
                    {
                        var t = await ((OrbitApi)api).TableAsync(key, target, ct);
                        return new JsonObject { ["id"] = target, ["name"] = t["name"]?.DeepClone() };
                    });
                    record = await connections.CallAsync(flow.WorkspaceId, "orbit", (api, key) => ((OrbitApi)api).RecordAsync(key, target, id, ct));
                }
                else
                {
                    record = Guid.TryParse(id, out var taskId)
                        ? await connections.CallAsync(flow.WorkspaceId, "projects", (api, key) => ((ProjectsApi)api).TaskAsync(key, taskId, ct))
                        : null;
                }
            }
            catch (AppRefusedException e) when (e.Status == 404) { record = null; }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException)
            {
                results.Add(new JsonObject { ["recordId"] = id, ["error"] = e.Message });
                continue;
            }
            if (record is null) { results.Add(new JsonObject { ["recordId"] = id, ["error"] = "That record isn't there any more." }); continue; }
            var data = ButtonRules.TriggerData(app, record, table, by, now);
            if (app == "projects") data["task"] = record.DeepClone();
            var started = await runs.StartAsync(flow, data, $"btn:{c.Nonce}:{id}", now);
            results.Add(started.Started
                ? new JsonObject { ["recordId"] = id, ["runId"] = started.RunId, ["runUrl"] = $"{forms.AppBase}/runs/{started.RunId}?ws={flow.WorkspaceId}" }
                : new JsonObject { ["recordId"] = id, ["error"] = started.Message, ["code"] = started.Code });
        }
        return Ok(new { flowName = flow.Name, runs = results });
    }

    /// <summary>An Orbit table id (as Orbit's ids are), or Projects' <c>task</c>.</summary>
    private static string? Target(string app, string? target)
    {
        if (app == "projects") return "task";
        var t = target?.Trim();
        return t is { Length: > 0 and <= 64 } && t.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-') ? t : null;
    }
}
