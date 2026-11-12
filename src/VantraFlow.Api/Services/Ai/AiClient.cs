using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.RateLimiting;

namespace VantraFlow.Api.Services.Ai;

/// <summary>
/// The AI flow builder's model call, as Vantra Orbit's AI (its <c>AiController</c>): OpenAI chat completions with
/// <c>OpenAI:ApiKey</c> and <c>OpenAI:Model</c>, here with Structured Outputs (a strict JSON schema) and a timeout. Off
/// — "Not set up yet" — until both settings exist. Only the planner's instructions (the catalog, the workspace's apps,
/// tables and projects, never a secret or a connection's key) and the person's request are sent; neither is logged.
/// </summary>
public sealed class AiClient(IHttpClientFactory http, IConfiguration config, ILogger<AiClient> log)
{
    public const string ClientName = "openai";
    private const string Url = "https://api.openai.com/v1/chat/completions";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>The drafts each workspace gets a month when <c>Ai:MonthlyCap</c> isn't set.</summary>
    public const int DefaultMonthlyCap = 300;

    private string? Key => config["OpenAI:ApiKey"] is { Length: > 0 } k ? k : null;
    private string? Model => config["OpenAI:Model"] is { Length: > 0 } m ? m : null;

    /// <summary>Both settings exist.</summary>
    public bool Configured => Key is not null && Model is not null;

    /// <summary>The monthly draft cap per workspace (<c>Ai:MonthlyCap</c>, default <see cref="DefaultMonthlyCap"/>; 0 turns drafting off).</summary>
    public int MonthlyCap => int.TryParse(config["Ai:MonthlyCap"], out var cap) && cap >= 0 ? cap : DefaultMonthlyCap;

    /// <summary>The model's JSON answer, or null when the call failed (logged by status only).</summary>
    public async Task<string?> DraftAsync(string instructions, string request, JsonObject schema, CancellationToken ct)
    {
        if (!Configured) return null;
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = 0.2,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = instructions },
                new JsonObject { ["role"] = "user", ["content"] = "The flow to draft, in the person's words:\n" + request }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = "flow_draft", ["strict"] = true, ["schema"] = schema },
            },
        };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
        try
        {
            using var resp = await http.CreateClient(ClientName).SendAsync(req, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("The AI draft call answered {Status}", (int)resp.StatusCode);
                return null;
            }
            var parsed = await resp.Content.ReadFromJsonAsync<JsonObject>(cts.Token);
            var message = parsed?["choices"]?[0]?["message"];
            if (message?["refusal"] is JsonValue) return null;
            return message?["content"] is JsonValue c && c.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            log.LogWarning("The AI draft call timed out");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException)
        {
            log.LogWarning("The AI draft call failed: {Kind}", e.GetType().Name);
            return null;
        }
    }

    // ── Rate limits: per person (the controller's policy) and per workspace (here, across this instance) ──

    public const int PerWorkspacePerMinute = 20;

    private static readonly PartitionedRateLimiter<Guid> Workspaces = PartitionedRateLimiter.Create<Guid, Guid>(ws =>
        RateLimitPartition.GetFixedWindowLimiter(ws, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PerWorkspacePerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0,
        }));

    /// <summary>One draft for this workspace this minute; false when it has had its share.</summary>
    public static bool TakeWorkspaceTurn(Guid workspaceId)
    {
        using var lease = Workspaces.AttemptAcquire(workspaceId);
        return lease.IsAcquired;
    }
}
