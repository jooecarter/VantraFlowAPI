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
/// AI steps call it too (<see cref="CompleteAsync"/>): the step's own inputs only, retried on a 5xx, a 429 or a timeout.
/// </summary>
public sealed class AiClient(IHttpClientFactory http, IConfiguration config, ILogger<AiClient> log) : IAiModel
{
    public const string ClientName = "openai";
    private const string Url = "https://api.openai.com/v1/chat/completions";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    /// <summary>The drafts each workspace gets a month before plans are in force, when <c>Ai:MonthlyCap</c> isn't set (Pro's credits).</summary>
    public const int DefaultMonthlyCap = 200;

    private string? Key => config["OpenAI:ApiKey"] is { Length: > 0 } k ? k : null;
    private string? Model => config["OpenAI:Model"] is { Length: > 0 } m ? m : null;

    /// <summary>Both settings exist.</summary>
    public bool Configured => Key is not null && Model is not null;

    /// <summary><c>Ai:MonthlyCap</c> when set: a ceiling on every workspace's drafts a month (0 turns drafting off).</summary>
    public int? ConfiguredCap => int.TryParse(config["Ai:MonthlyCap"], out var cap) && cap >= 0 ? cap : null;

    /// <summary>
    /// A workspace's drafts a month: its plan's AI credits once plans are in force (db/008), held under
    /// <c>Ai:MonthlyCap</c> when that's set; before, <c>Ai:MonthlyCap</c> or <see cref="DefaultMonthlyCap"/>.
    /// </summary>
    public static int CapFor(bool planEnforced, int planCredits, int? configured) =>
        planEnforced ? Math.Min(planCredits, configured ?? int.MaxValue) : configured ?? DefaultMonthlyCap;

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

    // ── AI steps ──

    /// <summary>Tries per AI step call (a 5xx, a 429 or a timeout is tried again at once, briefly; then the engine's retries take over).</summary>
    public const int StepAttempts = 3;
    public static readonly TimeSpan StepAttemptTimeout = TimeSpan.FromSeconds(20);
    /// <summary>The longest a retry waits, whatever Retry-After asks.</summary>
    public static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(8);

    /// <summary>How the wait before another try is made (tests skip it).</summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; set; } = Task.Delay;

    /// <summary>The wait before try <paramref name="attempt"/> + 1: Retry-After when given (at most 8 s), else 1 s, 2 s, 4 s….</summary>
    public static TimeSpan RetryWait(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } r && r > TimeSpan.Zero ? (r < MaxRetryWait ? r : MaxRetryWait)
            : TimeSpan.FromSeconds(Math.Min(MaxRetryWait.TotalSeconds, Math.Pow(2, Math.Max(0, attempt - 1))));

    /// <summary>
    /// An AI step's call: the step's instructions and input, a strict JSON schema for the answer. Logged by status only:
    /// never the input, the answer or the key.
    /// </summary>
    public async Task<AiReply> CompleteAsync(AiAsk ask, CancellationToken ct)
    {
        if (!Configured) return AiReply.Fail(AiStepRules.NotSetUp);
        var body = new JsonObject
        {
            ["model"] = Model,
            ["temperature"] = ask.Temperature,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = ask.System },
                new JsonObject { ["role"] = "user", ["content"] = ask.User }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject { ["name"] = ask.SchemaName, ["strict"] = true, ["schema"] = ask.Schema.DeepClone() },
            },
        };
        for (var attempt = 1; ; attempt++)
        {
            int status;
            TimeSpan? retryAfter = null;
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(StepAttemptTimeout);
                using var req = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(body) };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);
                try
                {
                    using var resp = await http.CreateClient(ClientName).SendAsync(req, cts.Token);
                    status = (int)resp.StatusCode;
                    if (resp.IsSuccessStatusCode)
                    {
                        var parsed = await resp.Content.ReadFromJsonAsync<JsonObject>(cts.Token);
                        var message = parsed?["choices"]?[0]?["message"];
                        if (message?["refusal"] is JsonValue) return AiReply.Fail("The AI declined to work on this text.");
                        if (message?["content"] is JsonValue c && c.TryGetValue<string>(out var text) && text.Length > 0) return new AiReply(text);
                        status = 502;
                    }
                    else retryAfter = resp.Headers.RetryAfter?.Delta;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { status = 408; }
                catch (Exception e) when (e is HttpRequestException or JsonException or NotSupportedException) { status = 503; }
            }
            var retryable = status is 408 or 429 || status >= 500;
            log.LogWarning("An AI step's call answered {Status} (try {Attempt})", status, attempt);
            if (!retryable)
                return AiReply.Fail(status is 401 or 403 ? "The server's AI key was refused. An admin can check the AI settings." : $"The AI couldn't take this request (HTTP {status}).");
            if (attempt >= StepAttempts)
                return AiReply.Fail(status switch
                {
                    429 => "The AI is busy right now.",
                    408 => $"The AI didn't answer within {StepAttemptTimeout.TotalSeconds:0} seconds.",
                    _ => "The AI is unavailable right now.",
                }, retryable: true);
            await Sleep(RetryWait(attempt, retryAfter), ct);
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
