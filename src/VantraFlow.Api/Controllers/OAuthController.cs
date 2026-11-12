using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.OAuth;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Outside apps: Slack, Microsoft 365 and Google (db/004; 503 <c>not_ready</c> before), and Stripe, HubSpot and Xero (db/007 too).
/// <code>
/// GET    api/w/{ws}/oauth                          → each provider: set up on this server, ready, and the connection
/// POST   api/w/{ws}/oauth/{provider}/start         → { url }: the provider's consent page (owner / admin)
/// GET    api/oauth/{provider}/callback             the provider sends the browser back here; on to the studio
/// POST   api/w/{ws}/oauth/{provider}/finish        { id } → the connection: the consent claimed by who started it
/// POST   api/w/{ws}/oauth/{provider}/key           { key } → the connection: a pasted API key or token (Stripe, a HubSpot private app)
/// DELETE api/w/{ws}/oauth/{provider}               → 204: revoked at the provider where it can be (owner / admin)
/// GET    api/w/{ws}/oauth/{provider}/lookup/{what} → a picker's choices (Slack channels, Teams, a sheet's tabs and columns)
/// POST   api/oauth/slack/events                    Slack's Events API: signed with Connectors:Slack:SigningSecret
/// </code>
/// A provider without its App Service settings (<c>Connectors:{Slack|Microsoft|Google}:ClientId</c> and
/// <c>…:ClientSecret</c>; Slack also <c>…:SigningSecret</c>) answers "not set up yet" and connects nothing.
/// </summary>
public sealed class OAuthController(
    WorkspacesRepository workspaces, OAuthRepository repo, OAuthProviders providers, OAuthConnections connections, OAuthActions actions,
    FlowSchema schema, EngineRepository engine, FlowsRepository flows, FlowSignal signal, ILogger<OAuthController> log) : FlowControllerBase(workspaces)
{
    private async Task<IActionResult?> NotReadyAsync(string? provider = null) =>
        await ReadyAsync(provider) ? null : StatusCode(503, new { error = "This app arrives with the next server update.", code = "not_ready" });

    /// <summary>db/004 for every outside app; db/007 too for Stripe, HubSpot and Xero.</summary>
    private async Task<bool> ReadyAsync(string? provider) =>
        await schema.ConnectorsReadyAsync() && (!OAuthProviders.NeedsSteps(provider) || await schema.StepsReadyAsync());

    [HttpGet("api/w/{workspaceId:guid}/oauth")]
    public async Task<IActionResult> List(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var ready = await schema.ConnectorsReadyAsync();
        var steps = ready && await schema.StepsReadyAsync();
        var live = ready ? await repo.LiveAsync(workspaceId) : [];
        return Ok(providers.All.Select(p => new OAuthAppDto(p.Key, p.Name, p.Configured, OAuthProviders.NeedsSteps(p.Key) ? steps : ready,
            live.FirstOrDefault(c => c.Provider == p.Key) is { } c ? connections.Dto(c) : null, p.ConnectMode, p.Premium)).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/oauth/{provider}/start")]
    public async Task<IActionResult> Start(Guid workspaceId, string provider)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync(provider) is { } nr) return nr;
        if (providers.For(provider) is not { } p) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        if (!p.CanSignIn) return StatusCode(503, new { error = $"{p.Name} isn't set up on this server yet.", code = "not_configured" });
        return Ok(new { url = connections.Start(p, workspaceId, Sub, DisplayName) });
    }

    /// <summary>Connects with a pasted API key or token (Stripe; a HubSpot private app), checked with the app first. Owners and admins.</summary>
    [HttpPost("api/w/{workspaceId:guid}/oauth/{provider}/key")]
    public async Task<IActionResult> Key(Guid workspaceId, string provider, [FromBody] OAuthKeyBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync(provider) is { } nr) return nr;
        if (providers.For(provider) is not { } p) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        if (p.ConnectMode == "oauth") return BadRequest(new { error = $"{p.Name} connects with a sign-in.", code = "no_key" });
        if (body.Key is not { Length: > 0 and <= 500 } key) return BadRequest(new { error = "Paste the key.", code = "bad_key" });
        try
        {
            var made = await connections.ConnectKeyAsync(p, workspaceId, key, Sub, DisplayName, ct);
            await LogAsync(workspaceId, Services.Team.ActivityKinds.ConnectionAdded, detail: $"{p.Name}: {made.AccountName}");
            return Ok(connections.Dto(made));
        }
        catch (AppUnavailableException e) { return StatusCode(502, new { error = e.Message, code = "app_unavailable" }); }
        catch (AppRefusedException e) { return BadRequest(new { error = e.Message, code = e.Code }); }
        catch (OAuthUnauthorizedException) { return BadRequest(new { error = $"{p.Name} didn't accept that key.", code = "bad_key" }); }
        catch (RateLimitedException e) { return StatusCode(429, new { error = e.Message, code = "rate_limited" }); }
    }

    /// <summary>Where the provider sends the browser back: never connects by itself (see <see cref="OAuthConnections"/>).</summary>
    [AllowAnonymous]
    [HttpGet("api/oauth/{provider}/callback")]
    [EnableRateLimiting(IncomingController.RateLimitPolicy)]
    public async Task<IActionResult> Callback(string provider, [FromQuery] string? code, [FromQuery] string? state, [FromQuery] string? error, CancellationToken ct)
    {
        if (providers.For(provider) is not { CanSignIn: true } p) return NotFound(new { error = "There's nothing here." });
        return Redirect(await connections.CallbackAsync(p, code, state, error, ct));
    }

    [HttpPost("api/w/{workspaceId:guid}/oauth/{provider}/finish")]
    public async Task<IActionResult> Finish(Guid workspaceId, string provider, [FromBody] OAuthFinishBody body, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync(provider) is { } nr) return nr;
        if (providers.For(provider) is not { } p) return NotFound(new { error = "Vantra Flow doesn't connect to that app." });
        if (body.Id is not { Length: > 0 and <= 64 } id) return BadRequest(new { error = "That sign-in can't be found." });
        var row = await connections.FinishAsync(p, workspaceId, id, Sub, DisplayName, ct);
        if (row is null) return StatusCode(410, new { error = $"That {p.Name} sign-in has expired. Connect again.", code = "expired" });
        await LogAsync(workspaceId, Services.Team.ActivityKinds.ConnectionAdded, detail: $"{p.Name}: {row.AccountName}");
        return Ok(connections.Dto(row));
    }

    [HttpDelete("api/w/{workspaceId:guid}/oauth/{provider}")]
    public async Task<IActionResult> Disconnect(Guid workspaceId, string provider, CancellationToken ct)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync(provider) is { } nr) return nr;
        if (await repo.LiveAsync(workspaceId, provider) is not { } row) return NoContent();
        await connections.DisconnectAsync(row, ct);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.ConnectionRemoved, detail: $"{providers.For(provider)?.Name ?? provider}: {row.AccountName}");
        return NoContent();
    }

    [HttpGet("api/w/{workspaceId:guid}/oauth/{provider}/lookup/{what}")]
    public async Task<IActionResult> Lookup(Guid workspaceId, string provider, string what, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync(provider) is { } nr) return nr;
        var q = Request.Query.ToDictionary(kv => kv.Key, kv => (string?)kv.Value.ToString());
        try { return Ok(await actions.LookupAsync(workspaceId, provider, what, q, ct)); }
        catch (AppUnavailableException e) { return StatusCode(502, new { error = e.Message, code = "app_unavailable" }); }
        catch (AppRevokedException e) { return StatusCode(409, new { error = e.Message, code = "connection_revoked" }); }
        catch (AppRefusedException e) { return StatusCode(e.Code == "not_connected" ? 409 : e.Status is >= 400 and < 500 ? e.Status : 400, new { error = e.Message, code = e.Code }); }
    }

    // ── Slack's Events API ──

    /// <summary>
    /// Slack's events, for "New message in channel": the request's signature checked (and its age), the URL check
    /// answered, each message queued for every Flow workspace connected to that Slack workspace (once per event id).
    /// Messages Flow's own app posted are marked as Flow's (they start only flows that opted in). An uninstall or a
    /// revoked token ends those connections.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("api/oauth/slack/events")]
    [EnableRateLimiting(IncomingController.RateLimitPolicy)]
    public async Task<IActionResult> SlackEvents()
    {
        if (providers.Slack.SigningSecret is not { } secret) return NotFound(new { error = "There's nothing here." });
        if (Request.ContentLength > IncomingController.MaxBodyBytes) return StatusCode(413, new { error = "Too large." });
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms);
        if (ms.Length > IncomingController.MaxBodyBytes) return StatusCode(413, new { error = "Too large." });
        var body = ms.ToArray();
        if (!SlackSignature.Verify(secret, Request.Headers["X-Slack-Signature"], Request.Headers["X-Slack-Request-Timestamp"], body, DateTime.UtcNow))
            return Unauthorized(new { error = "The signature doesn't match." });
        JsonObject root;
        try { root = JsonNode.Parse(body) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { return BadRequest(new { error = "The body must be JSON." }); }
        switch (Tokens.Text(root["type"]))
        {
            case "url_verification":
                return Ok(new { challenge = Tokens.Text(root["challenge"]) });
            case "event_callback":
                break;
            default:
                return Ok(new { ignored = true });
        }
        if (!await schema.ConnectorsReadyAsync()) return Ok(new { ignored = true });
        var team = Tokens.Text(root["team_id"]);
        var ev = root["event"] as JsonObject ?? new JsonObject();
        var type = Tokens.Text(ev["type"]);
        var conns = team.Length > 0 ? await repo.ByTeamAsync(team) : [];
        if (type is "app_uninstalled" or "tokens_revoked")
        {
            foreach (var c in conns) await connections.LostAsync(c, "Vantra Flow was removed from Slack.");
            return Ok(new { accepted = true });
        }
        if (SlackMessage(ev) is not { } data) return Ok(new { ignored = true });
        var eventId = Tokens.Text(root["event_id"]) is { Length: > 0 and <= 80 } e ? e : $"{Tokens.Text(ev["channel"])}:{Tokens.Text(ev["ts"])}";
        var queued = 0;
        foreach (var c in conns)
        {
            if (!await engine.ClaimKeyAsync(c.WorkspaceId, "", $"slack:{eventId}")) continue;
            if ((await flows.ScopesInUseAsync(c.WorkspaceId, "slack")).Count == 0) continue;
            await engine.EnqueueAsync(c.WorkspaceId, EventSources.Slack, "message", Tokens.Text(ev["channel"]), null, data.ToJsonString(), IsOwnMessage(ev, c.BotUserId), DateTime.UtcNow);
            queued++;
        }
        if (queued > 0) signal.Poke();
        log.LogDebug("Slack event {Event} queued for {Count} workspaces", eventId, queued);
        return Ok(new { accepted = true });
    }

    /// <summary>A Slack message event as a trigger's data; null for what isn't a new message (edits, deletes, joins).</summary>
    public static JsonObject? SlackMessage(JsonObject ev)
    {
        if (Tokens.Text(ev["type"]) != "message") return null;
        if (Tokens.Text(ev["subtype"]) is { Length: > 0 } sub && sub is not ("thread_broadcast" or "file_share" or "bot_message")) return null;
        var channel = Tokens.Text(ev["channel"]);
        var ts = Tokens.Text(ev["ts"]);
        if (channel.Length == 0 || ts.Length == 0) return null;
        return new JsonObject
        {
            ["text"] = Tokens.Text(ev["text"]),
            ["user"] = Tokens.Text(ev["user"]),
            ["channel"] = channel,
            ["ts"] = ts,
            ["threadTs"] = Tokens.Text(ev["thread_ts"]),
            ["isReply"] = Tokens.Text(ev["thread_ts"]) is { Length: > 0 } t && t != ts,
            ["isBot"] = ev["bot_id"] is not null,
            ["sentAt"] = double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var secs)
                ? Formatter.IsoUtc(DateTimeOffset.FromUnixTimeMilliseconds((long)(secs * 1000)).UtcDateTime) : "",
        };
    }

    /// <summary>The loop protection: a message Flow's own Slack app posted (its bot user, or its app id).</summary>
    public static bool IsOwnMessage(JsonObject ev, string? botUserAndApp)
    {
        if (botUserAndApp is not { Length: > 0 }) return false;
        var parts = botUserAndApp.Split(':');
        var botUser = parts[0];
        var app = parts.Length > 1 ? parts[1] : "";
        return (botUser.Length > 0 && Tokens.Text(ev["user"]) == botUser)
            || (app.Length > 0 && (Tokens.Text(ev["app_id"]) == app || Tokens.Text(ev["bot_profile"]?["app_id"]) == app));
    }
}
