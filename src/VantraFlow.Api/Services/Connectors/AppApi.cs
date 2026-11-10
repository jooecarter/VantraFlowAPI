using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// The app couldn't be reached, timed out, or answered 5xx / 429: worth trying again. <see cref="Status"/> is the
/// HTTP status when there was an answer; <see cref="NotSetUp"/> when this server has no address or key for the app.
/// </summary>
public sealed class AppUnavailableException(string message, Exception? inner = null, int? status = null, bool notSetUp = false) : Exception(message, inner)
{
    public int? Status { get; } = status;
    public bool NotSetUp { get; } = notSetUp;
}

/// <summary>The connection's key was revoked in the app (401): the connection is marked so, and nothing retries.</summary>
public sealed class AppRevokedException(string app) : Exception($"The {app} connection was disconnected. Connect it again from Connections.");

/// <summary>The app refused the request (another 4xx), in its own words.</summary>
public sealed class AppRefusedException(int status, string message, string? code) : Exception(message)
{
    public int Status { get; } = status;
    public string? Code { get; } = code;
}

/// <summary>A workspace in the app that the person could connect.</summary>
public sealed record AppWorkspace(Guid Id, string Name, string Role, bool CanConnect);

/// <summary>What connecting hands back: the key Flow acts with (sealed at once) and its id (the origin marker).</summary>
public sealed record AppConnectResult(string KeyId, string Key, Guid WorkspaceId, string WorkspaceName);

/// <summary>
/// The app's answer about a two-step code: <c>Ok</c> (Kind <c>totp</c> or <c>backup</c>), or why not
/// (<c>wrong_code</c>, <c>too_many_attempts</c>, <c>challenge_expired</c>, <c>not_enabled</c>) in the app's words.
/// </summary>
public sealed record AppTwoFactorVerdict(bool Ok, string? Code = null, string? Error = null, string? Kind = null, int? BackupCodesLeft = null);

internal sealed record AppTwoFactorStatus(bool Enabled);

/// <summary>A webhook Flow registered in the app: its id there and its signing secret (sealed at once).</summary>
public sealed record AppHook(string Id, string Secret);

/// <summary>
/// The shared HTTP side of a connector (as Orbit's ProjectsApi): a two-minute signed assertion of who is asking
/// before a connection exists, then the connection's own key. Every call times out after <see cref="Timeout"/>;
/// a network failure, a timeout, a 5xx or a 429 is <see cref="AppUnavailableException"/>, a 401 on a key
/// <see cref="AppRevokedException"/>, any other 4xx <see cref="AppRefusedException"/>.
/// </summary>
public abstract class AppApi(IHttpClientFactory http, IConfiguration config)
{
    public const string ClientName = "VantraApps";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(12);
    protected static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary><c>orbit</c> or <c>projects</c>.</summary>
    public abstract string App { get; }
    public abstract string AppName { get; }
    protected abstract string DefaultApiUrl { get; }
    protected abstract string DefaultAppUrl { get; }

    /// <summary>The assertion's audience: <c>vantra-flow-to-{app}</c>, which no session token carries.</summary>
    public string Audience => $"vantra-flow-to-{App}";

    public Uri? ApiUrl =>
        Uri.TryCreate((config[$"Connectors:{Cap(App)}:ApiUrl"] is { Length: > 0 } u ? u : DefaultApiUrl).TrimEnd('/'), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.IsLoopback) ? uri : null;

    public string AppUrl => (config[$"Connectors:{Cap(App)}:AppUrl"] is { Length: > 0 } u ? u : DefaultAppUrl).TrimEnd('/');

    private byte[]? Key =>
        config["Connectors:SharedKey"] is { Length: >= 32 } k ? Encoding.UTF8.GetBytes(k)
        : config["Jwt:Key"] is { Length: >= 32 } j ? Encoding.UTF8.GetBytes(j) : null;

    public bool Configured => ApiUrl is not null && Key is not null;

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>The <c>amr</c> on a session (and an assertion) that passed two-step sign-in.</summary>
    public const string SecondStepAmr = "otp";

    /// <summary>
    /// A two-minute JWT: who is asking (their VantraId) and for which Flow workspace (empty at sign-in), signed with
    /// the shared key; with <c>amr: otp</c> when their Flow session passed two-step sign-in.
    /// </summary>
    public string Assertion(string vantraId, string? name, Guid flowWorkspaceId, bool passedSecondStep = false)
    {
        var key = Key ?? throw new AppUnavailableException($"{AppName} isn't set up on this server.", notSetUp: true);
        var claims = new List<Claim> { new("sub", vantraId), new("flow_ws", flowWorkspaceId.ToString()), new("jti", Guid.NewGuid().ToString()) };
        if (!string.IsNullOrWhiteSpace(name)) claims.Add(new Claim("name", name));
        if (passedSecondStep) claims.Add(new Claim("amr", SecondStepAmr));
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(config["Jwt:Issuer"] ?? "vantra", Audience, claims, now, now.AddMinutes(2),
            new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public Task<IReadOnlyList<AppWorkspace>> WorkspacesAsync(string assertion, CancellationToken ct) =>
        SendAsync<IReadOnlyList<AppWorkspace>>(HttpMethod.Post, "/api/integrations/flow/v1/workspaces", null, null, assertion, ct);

    public Task<AppConnectResult> ConnectAsync(string assertion, Guid workspaceId, string flowWorkspaceName, CancellationToken ct) =>
        SendAsync<AppConnectResult>(HttpMethod.Post, "/api/integrations/flow/v1/connect", new { workspaceId, flowWorkspaceName }, null, assertion, ct);

    /// <summary>Whether the person has the app's two-step sign-in on.</summary>
    public async Task<bool> TwoFactorEnabledAsync(string assertion, CancellationToken ct) =>
        (await SendAsync<AppTwoFactorStatus>(HttpMethod.Post, "/api/integrations/flow/v1/2fa/status", null, null, assertion, ct)).Enabled;

    /// <summary>Checks a code against the app's two-step sign-in; Flow's challenge id caps the tries there too.</summary>
    public Task<AppTwoFactorVerdict> TwoFactorVerifyAsync(string assertion, string code, Guid challengeId, CancellationToken ct) =>
        SendAsync<AppTwoFactorVerdict>(HttpMethod.Post, "/api/integrations/flow/v1/2fa/verify", new { code, challengeId }, null, assertion, ct);

    /// <summary>Revokes the connection's key in the app (best effort: Flow forgets it either way).</summary>
    public Task DisconnectAsync(string key, CancellationToken ct) =>
        SendAsync<bool>(HttpMethod.Delete, "/api/integrations/flow/v1/connection", null, key, null, ct);

    /// <summary>The app's tables / projects, for the studio's pickers.</summary>
    public abstract Task<JsonObject> SchemaAsync(string key, CancellationToken ct);
    /// <summary>Registers Flow's receiver for this scope (an Orbit table; Projects: the workspace, <c>*</c>).</summary>
    public abstract Task<AppHook> SubscribeAsync(string key, string scope, string url, CancellationToken ct);
    public abstract Task UnsubscribeAsync(string key, string remoteId, CancellationToken ct);
    /// <summary>Real data for a test: the newest record / project, shaped as the trigger hands it on; null when there's none.</summary>
    public abstract Task<JsonObject?> SampleAsync(string key, Engine.TriggerDef trigger, CancellationToken ct);
    /// <summary>Carries out an action; throws the exceptions above.</summary>
    public abstract Task<JsonNode?> ActAsync(string key, string action, JsonObject input, string idempotencyKey, CancellationToken ct);

    /// <summary>How the key is sent: Orbit's API v1 takes a bearer key, Projects an X-Api-Key header.</summary>
    protected abstract void Authenticate(HttpRequestMessage req, string key);

    protected async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? key, string? assertion, CancellationToken ct, string? idempotencyKey = null)
    {
        var baseUrl = ApiUrl ?? throw new AppUnavailableException($"{AppName} isn't set up on this server.", notSetUp: true);
        using var req = new HttpRequestMessage(method, new Uri(baseUrl, path));
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        if (key is not null) Authenticate(req, key);
        if (assertion is not null) req.Headers.TryAddWithoutValidation("X-Flow-Assertion", assertion);
        if (idempotencyKey is not null) req.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKey);
        req.Headers.TryAddWithoutValidation("X-Vantra-Origin", "vantra-flow");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        HttpResponseMessage res;
        try { res = await http.CreateClient(ClientName).SendAsync(req, timeout.Token); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new AppUnavailableException($"{AppName} didn't answer in time."); }
        catch (HttpRequestException e) { throw new AppUnavailableException($"{AppName} couldn't be reached.", e); }
        using (res)
        {
            if (res.IsSuccessStatusCode)
            {
                if (res.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(bool)) return default!;
                try { return (await res.Content.ReadFromJsonAsync<T>(Json, timeout.Token))!; }
                catch (JsonException e) { throw new AppUnavailableException($"{AppName} answered something unexpected.", e); }
            }
            var (message, code) = await ErrorAsync(res, timeout.Token);
            if ((int)res.StatusCode >= 500 || res.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout)
                throw new AppUnavailableException(code == "not_ready" ? $"{AppName} isn't ready for Vantra Flow yet." : $"{AppName} is unavailable.", status: (int)res.StatusCode);
            if (res.StatusCode == HttpStatusCode.Unauthorized && key is not null) throw new AppRevokedException(AppName);
            throw new AppRefusedException((int)res.StatusCode, message ?? $"{AppName} refused that.", code);
        }
    }

    /// <summary>The app's error body: <c>{ error, code }</c> or Orbit API v1's <c>{ error: { code, message } }</c>.</summary>
    private static async Task<(string? Message, string? Code)> ErrorAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null);
            if (root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
                return (Str(e, "message"), Str(e, "code"));
            return (Str(root, "error"), Str(root, "code"));
        }
        catch { return (null, null); }

        static string? Str(JsonElement o, string k) => o.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    protected static string Required(JsonObject input, string key, string what) =>
        Engine.Tokens.Text(input[key]) is { Length: > 0 } s ? s.Trim() : throw new AppRefusedException(400, $"{what} is empty.", "empty");
}
