using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Ai;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.OAuth;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// The AI flow builder: describe an automation in plain words, get a flow to review.
/// <code>
/// GET  api/w/{ws}/ai          → { configured, ready, used, cap, month }
/// POST api/w/{ws}/ai/draft    { prompt, definition? } → { name, description, explanation, notes, definition, errors, apps, usage }
/// </code>
/// A draft is never saved or switched on here: the studio shows it for review, and saving it is an ordinary create (or,
/// with <c>definition</c> — "Ask AI" in the builder — an edit the person applies and saves). The definition comes back
/// held to the catalog and checked as a save would be (<c>errors</c>: what's left to fill in); <c>apps</c> are the apps it
/// needs, connected or not. Any member may draft (members make flows). Until <c>OpenAI:ApiKey</c> and <c>OpenAI:Model</c>
/// are set: 503 <c>not_configured</c>; until db/006 has run: 503 <c>not_ready</c>. Each draft counts against the
/// workspace's month: its plan's AI credits (db/008; Free and Starter have none), under <c>Ai:MonthlyCap</c> when set
/// (402 <c>plan_ai</c> with <c>upgradeTo</c>, or <c>ai_cap</c>, when spent; refunded when the call fails), and drafting is
/// rate-limited per person (<see cref="RateLimitPolicy"/>) and per workspace (<see cref="AiClient.PerWorkspacePerMinute"/>).
/// </summary>
public sealed class AiController(
    WorkspacesRepository workspaces, AiRepository usage, AiClient ai, FlowSchema schema, ConnectionsRepository connectionRows, Connections connections,
    OAuthRepository oauth, OAuthProviders providers, Services.Billing.Plans plans, IConfiguration config, ILogger<AiController> log) : FlowControllerBase(workspaces)
{
    public const string RateLimitPolicy = "ai";

    [HttpGet("api/w/{workspaceId:guid}/ai")]
    public async Task<IActionResult> Status(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        var ready = await schema.InsightsReadyAsync();
        var now = DateTime.UtcNow;
        var (cap, upgradeTo) = await CapAsync(workspaceId, now);
        var used = ready ? await usage.UsedAsync(workspaceId, now) : 0;
        return Ok(new AiStatusDto(ai.Configured, ready, used, cap, Services.Insights.InsightsReport.UsageMonth(now), used >= cap ? upgradeTo : null));
    }

    /// <summary>
    /// The workspace's drafts this month (its plan's AI credits, <see cref="AiClient.CapFor"/>), and the cheapest tier with
    /// more, for the upgrade prompt (null before plans are in force, or when no tier has more).
    /// </summary>
    private async Task<(int Cap, string? UpgradeTo)> CapAsync(Guid workspaceId, DateTime now)
    {
        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var (plan, _) = await plans.ForAsync(ws, now);
        var cap = AiClient.CapFor(plan.Enforced, plan.Limits.AiCredits, ai.ConfiguredCap);
        if (!plan.Enforced) return (cap, null);
        var to = plans.Catalog.Cheapest(l => AiClient.CapFor(true, l.AiCredits, ai.ConfiguredCap) > cap);
        return (cap, to != plan.Tier && AiClient.CapFor(true, plans.Catalog.For(to).AiCredits, ai.ConfiguredCap) > cap ? to : null);
    }

    [HttpPost("api/w/{workspaceId:guid}/ai/draft")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Draft(Guid workspaceId, [FromBody] AiDraftBody body, CancellationToken ct)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!ai.Configured) return StatusCode(503, new { error = "The AI builder isn't set up yet.", code = "not_configured" });
        if (!await schema.InsightsReadyAsync()) return StatusCode(503, new { error = "The AI builder arrives with the next server update.", code = "not_ready" });
        var prompt = body.Prompt?.Trim() ?? "";
        if (prompt.Length < 3) return BadRequest(new { error = "Describe the flow you'd like.", code = "empty" });
        if (prompt.Length > FlowPlanner.MaxPromptChars) return BadRequest(new { error = $"Keep it under {FlowPlanner.MaxPromptChars:N0} characters.", code = "too_long" });
        FlowDefinition? current = null;
        if (body.Definition is JsonObject given)
        {
            try { current = given.Deserialize<FlowDefinition>(FlowDefinition.Json); }
            catch (JsonException) { return BadRequest(new { error = "That flow couldn't be read." }); }
        }
        if (!AiClient.TakeWorkspaceTurn(workspaceId))
            return StatusCode(429, new { error = "This workspace is drafting a lot right now. Try again in a minute.", code = "rate_limited" });

        var now = DateTime.UtcNow;
        var (cap, upgradeTo) = await CapAsync(workspaceId, now);
        var (ok, used) = await usage.TryCountAsync(workspaceId, now, cap);
        if (!ok)
            return StatusCode(402, new
            {
                error = cap == 0 ? $"Drafting with AI comes with {Services.Billing.Tiers.Name(upgradeTo ?? Services.Billing.Tiers.Pro)}."
                    : $"This workspace has used its {cap:N0} AI credits for the month. They reset on the 1st.",
                code = upgradeTo is null ? "ai_cap" : "plan_ai", upgradeTo,
                usage = new AiUsageDto(used, cap, Services.Insights.InsightsReport.UsageMonth(now)),
            });

        var ws = (await Workspaces.GetAsync(workspaceId))!;
        var context = await ContextAsync(workspaceId, ws.TimeZone, current, ct);
        var limits = FlowLimits.From(config);
        var answer = await ai.DraftAsync(FlowPlanner.Instructions(context, now), prompt, FlowPlanner.Schema(), ct);
        var read = FlowPlanner.Read(answer, limits, current);
        if (read is null)
        {
            await usage.RefundAsync(workspaceId, now);
            return StatusCode(502, new { error = "The AI couldn't draft that just now. Try again, or say it another way.", code = "ai_unavailable" });
        }
        // An Approval the planner left without approvers asks whoever drafted it, so it can run as drafted.
        var draft = FlowPlanner.WithDefaultApprovers(read, Sub);

        var errors = DefinitionRules.Validate(draft.Definition, limits);
        var apps = FlowCatalog.AppsNeeded(draft.Definition).Select(a => new AiAppDto(a, a == "orbit" ? "Vantra Orbit" : "Vantra Projects", context.Connected.Contains(a), true))
            .Concat(FlowCatalog.ProvidersNeeded(draft.Definition).Select(p =>
                new AiAppDto(p, providers.For(p)?.Name ?? p, context.Connected.Contains(p), context.Offered.Contains(p))))
            .ToList();
        log.LogInformation("Workspace {Workspace} drafted a flow with AI ({Steps} steps)", workspaceId, draft.Definition.AllSteps().Count());
        return Ok(new AiDraftDto(draft.Name, draft.Description, draft.Explanation, draft.Notes,
            JsonSerializer.SerializeToNode(draft.Definition, FlowDefinition.Json)!, errors, apps, new AiUsageDto(used, cap, Services.Insights.InsightsReport.UsageMonth(now))));
    }

    /// <summary>
    /// What the planner is told about the workspace: its connected and offered apps, and (best effort, a few seconds at
    /// most) its Orbit tables and Projects projects. Never a key, a token or a secret.
    /// </summary>
    private async Task<PlannerContext> ContextAsync(Guid workspaceId, string timeZone, FlowDefinition? current, CancellationToken ct)
    {
        var connected = (await connectionRows.LiveAsync(workspaceId)).Select(c => c.App).ToHashSet(StringComparer.Ordinal);
        var offered = new List<string>();
        if (await schema.ConnectorsReadyAsync())
        {
            foreach (var c in await oauth.LiveAsync(workspaceId)) connected.Add(c.Provider);
            offered.AddRange(providers.All.Where(p => p.Configured).Select(p => p.Key));
        }
        var tables = new List<PlannerTable>();
        var projects = new List<PlannerProject>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(6));
        if (connected.Contains("orbit"))
        {
            try
            {
                var found = await connections.CallAsync(workspaceId, "orbit", (api, key) => api.SchemaAsync(key, cts.Token));
                foreach (var t in (found["tables"] as JsonArray ?? []).OfType<JsonObject>().Take(40))
                    tables.Add(new PlannerTable(Text(t["id"]), Text(t["name"]),
                        (t["fields"] as JsonArray ?? []).OfType<JsonObject>().Select(f => Text(f["name"])).Where(n => n.Length > 0).Take(40).ToList()));
            }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException or OperationCanceledException) { /* drafted without them */ }
        }
        if (connected.Contains("projects"))
        {
            try
            {
                var found = await connections.CallAsync(workspaceId, "projects", (api, key) => api.SchemaAsync(key, cts.Token));
                foreach (var p in (found["projects"] as JsonArray ?? []).OfType<JsonObject>().Take(60))
                    projects.Add(new PlannerProject(Text(p["id"]), Text(p["name"])));
            }
            catch (Exception e) when (e is AppUnavailableException or AppRefusedException or AppRevokedException or OperationCanceledException) { /* drafted without them */ }
        }
        return new PlannerContext(connected, offered, tables.Where(t => t.Id.Length > 0).ToList(), projects.Where(p => p.Id.Length > 0).ToList(), timeZone, current);
    }

    private static string Text(JsonNode? n) => n is JsonValue v ? v.ToString() : "";
}

public sealed record AiDraftBody(string? Prompt, JsonNode? Definition);

/// <summary><c>UpgradeTo</c>: the credits are spent (or the plan has none) and this tier has more.</summary>
public sealed record AiStatusDto(bool Configured, bool Ready, int Used, int Cap, string Month, string? UpgradeTo = null);

public sealed record AiUsageDto(int Used, int Cap, string Month);

/// <summary>An app a draft needs: connected or not, and whether this server offers it at all.</summary>
public sealed record AiAppDto(string App, string Name, bool Connected, bool Configured);

public sealed record AiDraftDto(string Name, string? Description, string Explanation, IReadOnlyList<string> Notes, JsonNode Definition,
    IReadOnlyList<DefinitionError> Errors, IReadOnlyList<AiAppDto> Apps, AiUsageDto Usage);
