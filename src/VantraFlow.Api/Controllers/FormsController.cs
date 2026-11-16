using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Triggers;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// A form trigger's page (db/014), for anyone with the link or, for a team form, members of its workspace signed in:
/// <code>
/// GET  api/forms/{token}          → { workspace, title, description, questions, access, submitLabel, confirmation, me, stamp, captcha, open }
/// POST api/forms/{token}/submit   { answers, hp, stamp, captchaToken, submissionKey } → { ok, confirmation }
/// </code>
/// In order: the form and its flow (on, still a form) or 404 / 410; a team form needs a Flow session (401 <c>sign_in</c>)
/// of a member (403 <c>not_member</c>); the honeypot <c>hp</c> filled, or a stamp under two seconds old: a pretend success,
/// nothing started; a stamp over a day old: 400 <c>form_stale</c>; Turnstile when it's set up: 400 <c>captcha</c>; per address
/// (the rate limiter, and <see cref="PerAddressPerHour"/> per form) and per form (<see cref="PerFormPerHour"/>): 429
/// <c>form_busy</c>; the answers checked against the form as it is now: 400 <c>invalid</c> with <c>errors</c> by key; then one
/// run, counted against the workspace's run quota (over it: 503 <c>form_full</c>, "isn't taking answers right now"). The
/// same submission key twice starts one run. Answers are never logged.
/// </summary>
[ApiController]
[AllowAnonymous]
[ServiceFilter(typeof(FlowReadyFilter))]
[EnableRateLimiting(RateLimitPolicy)]
public sealed class FormsController(
    TriggersRepository repo, FlowsRepository flows, WorkspacesRepository workspaces, IUserDirectory users, FormGuard guard,
    TriggerRuns runs, Services.Billing.Plans plans, ILogger<FormsController> log) : ControllerBase
{
    public const string RateLimitPolicy = "forms";
    public const int PerFormPerHour = 300;
    public const int PerAddressPerHour = 20;
    public const int MaxBodyBytes = 64 * 1024;
    public const string Closed = "This form isn't taking answers right now.";

    private string? Sub => User.FindFirstValue("sub");

    private sealed record Resolved(FlowFormRow Form, FlowRow Flow, FormSpec Spec, WorkspaceRow Workspace, FormPerson? Person);

    /// <summary>The form behind a token, or the answer to give instead.</summary>
    private async Task<(Resolved? Ok, IActionResult? Refusal)> ResolveAsync(string token)
    {
        if (!await repo.ReadyAsync()) return (null, StatusCode(503, new { error = "Forms arrive with the next server update.", code = "not_ready" }));
        if (token.Length is < 8 or > 24 || !token.All(char.IsAsciiLetterOrDigit)) return (null, Unknown());
        if (await repo.FormByTokenAsync(token) is not { } form) return (null, Unknown());
        if (await flows.GetAsync(form.WorkspaceId, form.FlowId) is not { } flow || FlowDefinition.Parse(flow.Definition) is not { } def || !FormRules.IsForm(def.Trigger))
            return (null, Unknown());
        if (!flow.Enabled) return (null, StatusCode(410, new { error = Closed, code = "form_closed" }));
        if (await workspaces.GetAsync(form.WorkspaceId) is not { } ws) return (null, Unknown());
        var spec = FormRules.Read(def.Trigger.Config);
        FormPerson? person = null;
        if (!spec.Public)
        {
            if (string.IsNullOrEmpty(Sub)) return (null, StatusCode(401, new { error = "Sign in to fill in this form.", code = "sign_in", workspace = ws.Name, title = spec.Title }));
            if (await workspaces.RoleAsync(ws.Id, Sub) is null)
                return (null, StatusCode(403, new { error = $"This form is for the team at {ws.Name}.", code = "not_member", workspace = ws.Name, title = spec.Title }));
            var u = await users.FindByVantraIdAsync(Sub);
            var name = $"{u?.FirstName} {u?.LastName}".Trim();
            person = new FormPerson(Sub, name.Length > 0 ? name : User.FindFirstValue("name"), u?.Email ?? User.FindFirstValue("email"));
        }
        return (new Resolved(form, flow, spec, ws, person), null);
    }

    [HttpGet("api/forms/{token}")]
    public async Task<IActionResult> Get(string token)
    {
        var (r, refusal) = await ResolveAsync(token);
        if (r is null) return refusal!;
        Response.Headers.CacheControl = "no-store";
        WorkspaceLook? look = null;
        try { look = (await workspaces.LooksAsync([r.Workspace.Id])).GetValueOrDefault(r.Workspace.Id); }
        catch (Microsoft.Data.SqlClient.SqlException) { /* before db/005: no colour or logo */ }
        var open = !await OverQuotaAsync(r.Workspace);
        return Ok(new
        {
            workspace = new { name = r.Workspace.Name, color = look?.Color, logo = look?.Logo },
            title = r.Spec.Title,
            description = r.Spec.Description,
            questions = r.Spec.Questions.Select(q => new { key = q.Key, label = q.Label, type = q.Type, required = q.Required, options = q.Options }),
            access = r.Spec.Access,
            submitLabel = r.Spec.SubmitLabel,
            confirmation = r.Spec.Confirmation,
            me = r.Person is null ? null : new { name = r.Person.Name, email = r.Person.Email },
            stamp = guard.IssueStamp(r.Form.Token, DateTime.UtcNow),
            captcha = guard.Captcha,
            open,
            closedReason = open ? null : Closed,
        });
    }

    private async Task<bool> OverQuotaAsync(WorkspaceRow ws)
    {
        try
        {
            var (plan, _) = await plans.ForAsync(ws, DateTime.UtcNow);
            var quota = Services.Billing.PlanRules.RunQuota(ws, plan);
            var (used, _) = await workspaces.UsageAsync(ws.Id, DateTime.UtcNow);
            return used >= quota;
        }
        catch (Exception e) when (e is Microsoft.Data.SqlClient.SqlException or InvalidOperationException) { return false; }
    }

    [HttpPost("api/forms/{token}/submit")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Submit(string token, [FromBody] JsonElement body, CancellationToken ct)
    {
        var (r, refusal) = await ResolveAsync(token);
        if (r is null) return refusal!;
        if (body.ValueKind != JsonValueKind.Object) return BadRequest(new { error = "That couldn't be read.", code = "invalid" });
        var now = DateTime.UtcNow;
        var root = JsonNode.Parse(body.GetRawText()) as JsonObject ?? new JsonObject();
        var success = new { ok = true, confirmation = r.Spec.Confirmation };

        // A bot: the hidden field filled, or sent faster than a person could. It's told it worked; nothing starts.
        if (Tokens.Text(root["hp"]).Length > 0) return Ok(success);
        switch (guard.CheckStamp(r.Form.Token, Tokens.Text(root["stamp"]), now))
        {
            case "fast": return Ok(success);
            case "stale" or "bad": return BadRequest(new { error = "This page was open a long time. Reload it and send again.", code = "form_stale" });
        }
        var ip = HttpContext.Connection.RemoteIpAddress;
        if (r.Spec.Public && !await guard.VerifyCaptchaAsync(Tokens.Text(root["captchaToken"]), ip, ct))
            return BadRequest(new { error = "Please complete the check and try again.", code = "captcha" });

        var ipHash = guard.IpHash(ip);
        var (all, fromHere) = await repo.SubmissionsSinceAsync(r.Form.Id, ipHash, now.AddHours(-1));
        if (all >= PerFormPerHour || (r.Spec.Public && fromHere >= PerAddressPerHour))
            return StatusCode(429, new { error = "This form is busy. Try again in a little while.", code = "form_busy" });

        var check = FormRules.CheckAnswers(r.Spec, root["answers"] as JsonObject, r.Person);
        if (!check.Ok) return BadRequest(new { error = "Some answers need another look.", code = "invalid", errors = check.Errors });

        var key = Tokens.Text(root["submissionKey"]) is { Length: >= 8 and <= 64 } k && k.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? k : FlowSecrets.NewRowId("");
        var data = FormRules.TriggerData(r.Form.Id, r.Spec, check.Answers, r.Person, now);
        var started = await runs.StartAsync(r.Flow, data, $"form:{r.Form.Id}:{key}", now);
        if (started.Code == "duplicate") return Ok(success);
        if (!started.Started)
        {
            log.LogInformation("A form answer on flow {Flow} didn't start a run ({Code})", r.Flow.Id, started.Code);
            return started.Code is "busy"
                ? StatusCode(429, new { error = "This form is busy. Try again in a little while.", code = "form_busy" })
                : StatusCode(503, new { error = Closed, code = "form_full" });
        }
        var id = FlowSecrets.NewRowId("fsm_");
        if (await repo.InsertSubmissionAsync(id, r.Form.Id, r.Workspace.Id, r.Flow.Id, key, r.Spec.Public ? ipHash : null, r.Person?.Id, now))
            await repo.SubmissionRunAsync(id, started.RunId);
        return Ok(success);
    }

    private NotFoundObjectResult Unknown() => NotFound(new { error = "This form isn't here. Check the link.", code = "unknown" });
}
