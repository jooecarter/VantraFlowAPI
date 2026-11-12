using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Developer;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

internal sealed class DevClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ApiKeyTests
{
    [Fact]
    public void A_new_key_has_its_prefix_and_shape_and_only_its_hash_matches()
    {
        var key = DevKeys.NewKey();
        Assert.StartsWith("vfk_", key);
        Assert.Equal(4 + 43, key.Length);
        Assert.True(DevKeys.IsKey(key));
        Assert.NotEqual(key, DevKeys.NewKey());
        // What's kept: the lookup prefix and a SHA-256, never the key.
        var prefix = DevKeys.Prefix(key);
        Assert.Equal(12, prefix.Length);
        Assert.StartsWith(prefix, key);
        var hash = DevKeys.Hash(key);
        Assert.Equal(32, hash.Length);
        Assert.True(DevKeys.Matches(key, hash));
        // One character changed, the same prefix: refused.
        var other = key[..^1] + (key[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(prefix, DevKeys.Prefix(other));
        Assert.False(DevKeys.Matches(other, hash));
        Assert.False(DevKeys.Matches(key, null));
        Assert.False(DevKeys.Matches(key, new byte[16]));
    }

    [Fact]
    public void Only_a_bearer_key_of_the_right_shape_is_read_from_the_header()
    {
        var key = DevKeys.NewKey();
        Assert.Equal(key, DevKeys.FromAuthorization($"Bearer {key}"));
        Assert.Equal(key, DevKeys.FromAuthorization($"  bearer   {key} "));
        Assert.Null(DevKeys.FromAuthorization(key));
        Assert.Null(DevKeys.FromAuthorization($"Basic {key}"));
        Assert.Null(DevKeys.FromAuthorization("Bearer eyJhbGciOiJIUzI1NiJ9.e30.x"));
        Assert.Null(DevKeys.FromAuthorization("Bearer vfk_short"));
        Assert.Null(DevKeys.FromAuthorization(null));
    }

    [Fact]
    public void A_keys_status_names_and_scopes()
    {
        var now = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        Assert.Equal("active", DevKeyStatuses.Of(null, null, now));
        Assert.Equal("active", DevKeyStatuses.Of(null, now.AddDays(1), now));
        Assert.Equal("expired", DevKeyStatuses.Of(null, now, now));
        Assert.Equal("revoked", DevKeyStatuses.Of(now.AddDays(-1), now.AddDays(1), now));
        Assert.Equal("Zapier sync", DevKeys.Name("  Zapier   sync "));
        Assert.Null(DevKeys.Name(" "));
        Assert.Null(DevKeys.Name(new string('a', 81)));
    }
}

public sealed class ScopeTests
{
    [Fact]
    public void Scopes_are_validated_ordered_and_none_implies_another()
    {
        var (ok, error) = DevScopes.Validate(["runs:write", "flows:read", "runs:write"]);
        Assert.Null(error);
        Assert.Equal(["flows:read", "runs:write"], ok);
        Assert.Equal("flows:read runs:write", DevScopes.Format(ok!));
        Assert.NotNull(DevScopes.Validate(["flows:admin"]).Error);
        Assert.NotNull(DevScopes.Validate([]).Error);
        Assert.NotNull(DevScopes.Validate(null).Error);
        var parsed = DevScopes.Parse("flows:write bogus runs:read");
        Assert.True(parsed.Contains("flows:write"));
        Assert.False(parsed.Contains("flows:read"));
        Assert.False(parsed.Contains("bogus"));
        Assert.Empty(DevScopes.Parse(null));
    }
}

public sealed class RateLimitTests
{
    [Fact]
    public void A_key_gets_120_a_minute_then_429_with_the_wait_and_a_new_window_starts_afresh()
    {
        var clock = new DevClock(new DateTimeOffset(2026, 11, 12, 9, 0, 15, TimeSpan.Zero));
        var limiter = new DevRateLimiter(clock);
        for (var i = 0; i < 120; i++) Assert.True(limiter.Take("key_a").Allowed);
        var refused = limiter.Take("key_a");
        Assert.False(refused.Allowed);
        Assert.Equal(0, refused.Remaining);
        Assert.Equal(45, refused.RetryAfterSeconds);
        // Another key has its own minute.
        Assert.True(limiter.Take("key_b").Allowed);
        clock.Now = clock.Now.AddSeconds(45);
        var next = limiter.Take("key_a");
        Assert.True(next.Allowed);
        Assert.Equal(119, next.Remaining);
    }
}

public sealed class IdempotencyTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void The_same_key_and_request_replays_another_request_conflicts_and_one_still_running_waits()
    {
        var body = Encoding.UTF8.GetBytes("{\"data\":{\"email\":\"sam@example.com\"}}");
        var hash = Idempotency.RequestHash("run:flw_1", body);
        Assert.Equal(IdempotencyVerdict.Proceed, Idempotency.Decide(null, hash, Now));
        var done = new IdempotencyRow("key_1", "order-10482", hash, 202, "{\"id\":\"frn_1\"}", Now.AddMinutes(-5));
        Assert.Equal(IdempotencyVerdict.Replay, Idempotency.Decide(done, hash, Now));
        // The same key for another flow, or another body.
        Assert.Equal(IdempotencyVerdict.Mismatch, Idempotency.Decide(done, Idempotency.RequestHash("run:flw_2", body), Now));
        Assert.Equal(IdempotencyVerdict.Mismatch, Idempotency.Decide(done, Idempotency.RequestHash("run:flw_1", "{}"u8), Now));
        Assert.Equal(IdempotencyVerdict.InProgress, Idempotency.Decide(done with { Status = 0, Response = null }, hash, Now));
        // A day on, the key is free again.
        Assert.Equal(IdempotencyVerdict.Proceed, Idempotency.Decide(done with { CreatedAt = Now.AddHours(-25) }, hash, Now));
    }

    [Fact]
    public void The_header_is_read_with_its_limits()
    {
        Assert.Equal((null, null), Idempotency.Read(null));
        Assert.Equal((null, null), Idempotency.Read("  "));
        Assert.Equal("order-10482", Idempotency.Read(" order-10482 ").Key);
        Assert.NotNull(Idempotency.Read(new string('k', 256)).Error);
        Assert.NotNull(Idempotency.Read("a\u0001b").Error);
    }

    [Fact]
    public void Cursors_are_bound_to_their_query()
    {
        var c = DevCursor.Encode("runs|flw_1", "638000000000000000", "42");
        Assert.Equal(("638000000000000000", "42"), DevCursor.Decode(c, "runs|flw_1"));
        Assert.Null(DevCursor.Decode(c, "runs|flw_2"));
        Assert.Null(DevCursor.Decode("not a cursor", "runs|flw_1"));
    }
}

public sealed class WebhookSigningTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_delivery_is_signed_as_stripe_does_and_verifies()
    {
        var secret = WebhookSigning.NewSecret();
        Assert.Matches("^whsec_[0-9a-f]{48}$", secret);
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\",\"type\":\"run.failed\"}");
        var t = Now.ToUnixTimeSeconds();
        var header = WebhookSigning.Sign(secret, t, body);
        Assert.Matches("^t=\\d+,v1=[0-9a-f]{64}$", header);
        Assert.True(WebhookSigning.Verify(secret, header, body, Now));
        // The documented recipe: HMAC-SHA256 of "{t}.{body}".
        var mac = Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret),
            Encoding.UTF8.GetBytes($"{t}.{Encoding.UTF8.GetString(body)}")));
        Assert.Equal($"t={t},v1={mac}", header);
    }

    [Fact]
    public void A_changed_body_another_secret_or_an_old_timestamp_is_refused()
    {
        var secret = WebhookSigning.NewSecret();
        var body = Encoding.UTF8.GetBytes("{\"id\":\"evt_1\"}");
        var header = WebhookSigning.Sign(secret, Now.ToUnixTimeSeconds(), body);
        Assert.False(WebhookSigning.Verify(secret, header, Encoding.UTF8.GetBytes("{\"id\":\"evt_2\"}"), Now));
        Assert.False(WebhookSigning.Verify(WebhookSigning.NewSecret(), header, body, Now));
        Assert.False(WebhookSigning.Verify(secret, header, body, Now.AddMinutes(6)));
        Assert.True(WebhookSigning.Verify(secret, header, body, Now.AddMinutes(4)));
        Assert.False(WebhookSigning.Verify(secret, null, body, Now));
        Assert.False(WebhookSigning.Verify(secret, "v1=abc", body, Now));
        // While a secret is rolled a header may carry two signatures; either may match.
        var both = header + ",v1=" + new string('0', 64);
        Assert.True(WebhookSigning.Verify(secret, both, body, Now));
    }

    [Fact]
    public void Events_are_validated_and_samples_are_marked_test()
    {
        Assert.Equal(["run.succeeded", "run.failed"], DevEventTypes.Validate(["run.failed", "run.succeeded"]).Events);
        Assert.NotNull(DevEventTypes.Validate(["run.started"]).Error);
        Assert.NotNull(DevEventTypes.Validate([]).Error);
        var ws = Guid.NewGuid();
        foreach (var type in DevEventTypes.All.Append(DevEventTypes.Ping))
        {
            var e = DevJson.Sample(type, ws, "whe_1", "https://hooks.example.com/flow", DateTime.UtcNow);
            Assert.Equal(type, (string?)e["type"]);
            Assert.True((bool)e["test"]!);
            Assert.Equal(ws.ToString("D"), (string?)e["workspaceId"]);
            Assert.StartsWith("evt_", (string?)e["id"]);
            Assert.IsType<JsonObject>(e["data"]);
        }
    }
}

public sealed class WebhookRetryTests
{
    [Fact]
    public void Answers_are_classified_for_retry()
    {
        Assert.Equal(SendVerdict.Succeeded, WebhookRules.Classify(200));
        Assert.Equal(SendVerdict.Succeeded, WebhookRules.Classify(204));
        Assert.Equal(SendVerdict.Retry, WebhookRules.Classify(500));
        Assert.Equal(SendVerdict.Retry, WebhookRules.Classify(503));
        Assert.Equal(SendVerdict.Retry, WebhookRules.Classify(429));
        Assert.Equal(SendVerdict.Retry, WebhookRules.Classify(408));
        Assert.Equal(SendVerdict.Fail, WebhookRules.Classify(400));
        Assert.Equal(SendVerdict.Fail, WebhookRules.Classify(301));
        Assert.Equal(SendVerdict.Gone, WebhookRules.Classify(410));
    }

    [Fact]
    public void Retries_back_off_with_jitter_honour_retry_after_and_stop_after_six_attempts()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), WebhookRules.NextDelay(1, null, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(5), WebhookRules.NextDelay(2, null, 0.5));
        Assert.Equal(TimeSpan.FromMinutes(30), WebhookRules.NextDelay(3, null, 0.5));
        Assert.Equal(TimeSpan.FromHours(2), WebhookRules.NextDelay(4, null, 0.5));
        Assert.Equal(TimeSpan.FromHours(8), WebhookRules.NextDelay(5, null, 0.5));
        Assert.Null(WebhookRules.NextDelay(6, null, 0.5));
        Assert.Equal(TimeSpan.FromSeconds(48), WebhookRules.NextDelay(1, null, 0));
        Assert.Equal(TimeSpan.FromSeconds(72), WebhookRules.NextDelay(1, null, 1));
        Assert.Equal(TimeSpan.FromSeconds(90), WebhookRules.NextDelay(1, TimeSpan.FromSeconds(90), 0.5));
        Assert.Equal(TimeSpan.FromHours(1), WebhookRules.NextDelay(2, TimeSpan.FromDays(1), 0.5));
        Assert.Equal(TimeSpan.FromSeconds(1), WebhookRules.NextDelay(2, TimeSpan.Zero, 0.5));
        Assert.Null(WebhookRules.NextDelay(6, TimeSpan.FromSeconds(5), 0.5));
    }

    [Fact]
    public void An_endpoint_is_switched_off_after_five_deliveries_fail_every_attempt_or_a_410()
    {
        for (var n = 0; n < WebhookRules.DisableAfter; n++) Assert.False(WebhookRules.ShouldDisable(n, gone: false));
        Assert.True(WebhookRules.ShouldDisable(5, gone: false));
        Assert.True(WebhookRules.ShouldDisable(0, gone: true));
        Assert.Contains("410", WebhookRules.DisabledReason(true));
        Assert.Contains("5 deliveries", WebhookRules.DisabledReason(false));
    }

    [Fact]
    public void A_snippet_is_short_and_free_of_control_characters()
    {
        Assert.Null(WebhookRules.Snippet(null));
        Assert.Null(WebhookRules.Snippet(" \n "));
        Assert.Equal("ok  done", WebhookRules.Snippet("ok\r\ndone"));
        Assert.Equal(WebhookRules.SnippetMax, WebhookRules.Snippet(new string('x', 5000))!.Length);
    }

    [Fact]
    public void The_disabled_email_names_the_host_never_the_path()
    {
        var (subject, html) = DevMail.EndpointOff("Northwind <Ops>", "hooks.example.com", WebhookRules.DisabledReason(false), "https://vantraflow-web.azurewebsites.net/settings?tab=developers");
        Assert.Contains("hooks.example.com", subject);
        Assert.Contains("Northwind &lt;Ops&gt;", html);
        Assert.Equal("hooks.example.com", WebhookDispatcher.Host("https://hooks.example.com/secret/path?token=1"));
        Assert.DoesNotContain("secret/path", WebhookDispatcher.Host("https://hooks.example.com/secret/path?token=1"));
    }
}

public sealed class WebhookUrlTests
{
    [Theory]
    [InlineData("http://hooks.example.com/flow", "https://")]
    [InlineData("https://localhost/hook", "public internet")]
    [InlineData("https://127.0.0.1/hook", "public internet")]
    [InlineData("https://10.0.0.5/hook", "private")]
    [InlineData("https://192.168.1.20/hook", "private")]
    [InlineData("https://169.254.169.254/latest/meta-data", "private")]
    [InlineData("https://168.63.129.16/", "private")]
    [InlineData("https://[::1]/hook", "public internet")]
    [InlineData("https://[fd00::1]/hook", "private")]
    [InlineData("https://intranet/hook", "public internet")]
    [InlineData("https://build.internal/hook", "public internet")]
    [InlineData("https://printer.local/hook", "public internet")]
    [InlineData("https://user:pass@hooks.example.com/", "credentials")]
    [InlineData("ftp://hooks.example.com/", "https://")]
    [InlineData("", "Enter the URL")]
    public void Private_and_unsafe_addresses_are_refused(string url, string why)
    {
        var error = WebhookRules.CheckUrl(url, out var uri);
        Assert.NotNull(error);
        Assert.Contains(why, error);
        Assert.Null(uri);
    }

    [Fact]
    public void A_public_https_url_is_accepted_and_the_guard_refuses_private_addresses_at_connect_time()
    {
        Assert.Null(WebhookRules.CheckUrl("https://hooks.example.com/vantra-flow?team=ops", out var uri));
        Assert.Equal("hooks.example.com", uri!.Host);
        Assert.Null(WebhookRules.CheckUrl("https://93.184.215.14/hook", out _));
        // The sender's client dials through NetworkGuard, which refuses these whatever a name resolved to.
        Assert.False(VantraFlow.Api.Services.NetworkGuard.IsPublic(System.Net.IPAddress.Parse("127.0.0.1")));
        Assert.False(VantraFlow.Api.Services.NetworkGuard.IsPublic(System.Net.IPAddress.Parse("10.1.2.3")));
        Assert.False(VantraFlow.Api.Services.NetworkGuard.IsPublic(System.Net.IPAddress.Parse("169.254.169.254")));
    }

    [Fact]
    public async Task The_sender_refuses_a_private_address_without_calling_it()
    {
        var sender = new WebhookSender(new NoHttp(), NullLogger<WebhookSender>.Instance);
        var r = await sender.SendAsync("https://10.0.0.8/hook", "whsec_x", "ping", "whd_1", "evt_1", "{}", CancellationToken.None);
        Assert.Equal(SendVerdict.Fail, r.Verdict);
        Assert.Contains("private", r.Error);
        var none = await sender.SendAsync("https://hooks.example.com/", null, "ping", "whd_1", "evt_1", "{}", CancellationToken.None);
        Assert.Equal(SendVerdict.Fail, none.Verdict);
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No request may be made.");
    }
}

public sealed class RunNowTests
{
    [Fact]
    public void A_webhook_flow_gets_the_data_as_its_body_and_others_get_it_as_it_is()
    {
        var now = new DateTime(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
        var data = new JsonObject { ["email"] = "sam@example.com" };
        var hook = new FlowDefinition { Trigger = new TriggerDef { App = "webhook", Event = "catch" } };
        var t = ApiV1Controller.Trigger(hook, data, now, "Europe/London");
        Assert.Equal("sam@example.com", (string?)t["body"]!["email"]);
        Assert.NotNull(t["headers"]);
        var orbit = new FlowDefinition { Trigger = new TriggerDef { App = "orbit", Event = "record.created" } };
        Assert.Equal("sam@example.com", (string?)ApiV1Controller.Trigger(orbit, data, now, null)["email"]);
        var schedule = new FlowDefinition { Trigger = new TriggerDef { App = "schedule", Event = "daily" } };
        Assert.Equal("Europe/London", (string?)ApiV1Controller.Trigger(schedule, null, now, "Europe/London")["timeZone"]);
        Assert.Empty(ApiV1Controller.Trigger(orbit, null, now, null));
    }
}

/// <summary>The API's door, end to end with a fake store: every refusal in its order, and the caller set when it passes.</summary>
public sealed class DevKeyFilterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 9, 0, 10, TimeSpan.Zero);
    private static readonly Guid Ws = Guid.Parse("8f0f2a64-6a0e-4c0d-9a36-0f7e3c2b1a11");

    private sealed class Store : IDevKeyStore
    {
        public bool Ready = true;
        public readonly Dictionary<string, ApiKeyRow> Keys = new();
        public bool Member = true;
        public string? Refusal;
        public int Touches;
        public Task<bool> ReadyAsync() => Task.FromResult(Ready);
        public Task<ApiKeyRow?> KeyByPrefixAsync(string prefix) => Task.FromResult(Keys.GetValueOrDefault(prefix));
        public Task<DevKeyOwner?> OwnerAsync(Guid workspaceId, string vantraId) => Task.FromResult(Member ? new DevKeyOwner("admin", "Northwind") : null);
        public Task<string?> PlanRefusalAsync(Guid workspaceId) => Task.FromResult(Refusal);
        public Task TouchAsync(string keyId, DateTime now) { Touches++; return Task.CompletedTask; }
    }

    private static (Store Store, string Key, ApiKeyRow Row) WithKey(string scopes, DateTime? expires = null, DateTime? revoked = null)
    {
        var store = new Store();
        var key = DevKeys.NewKey();
        var row = new ApiKeyRow(DevKeys.NewId(), Ws, "Zapier", DevKeys.Prefix(key), DevKeys.Hash(key), scopes, expires, "vantra-1", "Alex Morgan",
            Now.UtcDateTime.AddDays(-1), null, revoked, null);
        store.Keys[row.Prefix] = row;
        return (store, key, row);
    }

    private static ActionExecutingContext Context(string? authorization, string? scope)
    {
        var http = new DefaultHttpContext();
        if (authorization is not null) http.Request.Headers.Authorization = authorization;
        var action = new ActionDescriptor { EndpointMetadata = scope is null ? [] : [new DevScopeAttribute(scope)] };
        return new ActionExecutingContext(new ActionContext(http, new RouteData(), action), [], new Dictionary<string, object?>(), new object());
    }

    private static DevKeyFilter Filter(Store store, DevRateLimiter? limiter = null) =>
        new(store, limiter ?? new DevRateLimiter(new DevClock(Now)), NullLogger<DevKeyFilter>.Instance, new DevClock(Now));

    private static string? Code(ObjectResult? r) => (r?.Value as JsonObject)?["error"]?["code"]?.GetValue<string>();

    [Fact]
    public async Task A_valid_key_with_the_scope_passes_and_its_caller_is_set()
    {
        var (store, key, row) = WithKey("flows:read runs:read");
        var ctx = Context($"Bearer {key}", DevScopes.FlowsRead);
        Assert.Null(await Filter(store).CheckAsync(ctx));
        var caller = Assert.IsType<DevCaller>(ctx.HttpContext.Items[DevKeyFilter.CallerKey]);
        Assert.Equal(row.Id, caller.Key.Id);
        Assert.Equal(Ws, caller.WorkspaceId);
        Assert.Equal("119", ctx.HttpContext.Response.Headers["X-RateLimit-Remaining"].ToString());
        Assert.Equal(1, store.Touches);
    }

    [Fact]
    public async Task Missing_wrong_revoked_and_expired_keys_are_401()
    {
        var (store, key, row) = WithKey("flows:read");
        var r = await Filter(store).CheckAsync(Context(null, null));
        Assert.Equal(401, r!.StatusCode);
        Assert.Equal("unauthorized", Code(r));
        var wrong = key[..^2] + "zz";
        Assert.Equal("unauthorized", Code(await Filter(store).CheckAsync(Context($"Bearer {wrong}", null))));
        Assert.Equal("unauthorized", Code(await Filter(store).CheckAsync(Context($"Bearer {DevKeys.NewKey()}", null))));
        store.Keys[row.Prefix] = row with { RevokedAt = Now.UtcDateTime.AddMinutes(-1) };
        Assert.Equal("key_revoked", Code(await Filter(store).CheckAsync(Context($"Bearer {key}", null))));
        store.Keys[row.Prefix] = row with { ExpiresAt = Now.UtcDateTime.AddSeconds(-1) };
        Assert.Equal("key_expired", Code(await Filter(store).CheckAsync(Context($"Bearer {key}", null))));
        store.Keys[row.Prefix] = row;
        store.Member = false;
        Assert.Equal("key_inactive", Code(await Filter(store).CheckAsync(Context($"Bearer {key}", null))));
    }

    [Fact]
    public async Task A_key_without_the_scope_is_403_and_a_lower_plan_is_402()
    {
        var (store, key, _) = WithKey("flows:read runs:read");
        var r = await Filter(store).CheckAsync(Context($"Bearer {key}", DevScopes.RunsWrite));
        Assert.Equal(403, r!.StatusCode);
        Assert.Equal("missing_scope", Code(r));
        Assert.Equal("runs:write", (r.Value as JsonObject)!["error"]!["scope"]!.GetValue<string>());
        Assert.Equal("missing_scope", Code(await Filter(store).CheckAsync(Context($"Bearer {key}", DevScopes.FlowsWrite))));
        Assert.Equal("missing_scope", Code(await Filter(store).CheckAsync(Context($"Bearer {key}", DevScopes.WebhooksManage))));
        store.Refusal = "pro";
        var plan = await Filter(store).CheckAsync(Context($"Bearer {key}", DevScopes.FlowsRead));
        Assert.Equal(402, plan!.StatusCode);
        Assert.Equal("plan_api", Code(plan));
        Assert.Equal("pro", (plan.Value as JsonObject)!["error"]!["upgradeTo"]!.GetValue<string>());
    }

    [Fact]
    public async Task Over_the_rate_limit_is_429_with_retry_after_and_before_db012_is_503()
    {
        var (store, key, _) = WithKey("flows:read");
        var limiter = new DevRateLimiter(new DevClock(Now));
        var filter = Filter(store, limiter);
        for (var i = 0; i < DevConstants.RateLimitPerMinute; i++) Assert.Null(await filter.CheckAsync(Context($"Bearer {key}", DevScopes.FlowsRead)));
        var ctx = Context($"Bearer {key}", DevScopes.FlowsRead);
        var r = await filter.CheckAsync(ctx);
        Assert.Equal(429, r!.StatusCode);
        Assert.Equal("rate_limited", Code(r));
        Assert.Equal("50", ctx.HttpContext.Response.Headers.RetryAfter.ToString());
        Assert.Equal("0", ctx.HttpContext.Response.Headers["X-RateLimit-Remaining"].ToString());
        store.Ready = false;
        var nr = await Filter(store).CheckAsync(Context($"Bearer {key}", null));
        Assert.Equal(503, nr!.StatusCode);
        Assert.Equal("not_ready", Code(nr));
    }
}

public sealed class DeveloperMigrationTests
{
    private static string Sql => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "012_developer_api.sql"));

    [Fact]
    public void The_migration_is_one_azure_ready_batch_that_stops_without_db001_and_makes_its_probe_index_last()
    {
        var sql = Sql;
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(CREATE\s+(UNIQUE\s+)?INDEX|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase), outside);
        // It alters nothing from earlier migrations, and stops first, changing nothing, when db/001 hasn't run.
        Assert.DoesNotMatch(new Regex(@"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase), sql);
        Assert.True(sql.IndexOf("THROW 51012", StringComparison.Ordinal) < sql.IndexOf("CREATE TABLE", StringComparison.Ordinal));
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_WebhookDeliveries_Endpoint", last);
        Assert.Contains("IX_WebhookDeliveries_Endpoint", FlowSchema.DeveloperProbeSql);
        Assert.DoesNotContain("definition", FlowSchema.DeveloperProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
    }
}
