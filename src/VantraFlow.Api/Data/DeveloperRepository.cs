using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>An API key (db/012). Only its hash and lookup prefix are kept.</summary>
public sealed record ApiKeyRow(
    string Id, Guid WorkspaceId, string Name, string Prefix, byte[] KeyHash, string Scopes, DateTime? ExpiresAt, string CreatedBy, string? CreatedByName,
    DateTime CreatedAt, DateTime? LastUsedAt, DateTime? RevokedAt, string? RevokedBy);

/// <summary>An outgoing webhook (db/012). Its secret is sealed (AES-256-GCM, bound to the workspace and the row).</summary>
public sealed record EndpointRow(
    string Id, Guid WorkspaceId, string Url, string? Description, string Events, byte[] SecretCipher, string? SecretHint, bool Enabled,
    string? DisabledReason, DateTime? DisabledAt, int FailuresInARow, string CreatedBy, string? CreatedByName, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>One event sent to one endpoint (db/012): its attempts so far and the last answer.</summary>
public sealed record DeliveryRow(
    long Seq, string Id, Guid WorkspaceId, string EndpointId, string EventId, string EventType, string Payload, string Status, bool Test, int Attempts,
    DateTime? NextAttemptAt, int? ResponseStatus, string? ResponseBody, string? Error, int? DurationMs, DateTime CreatedAt, DateTime? LastAttemptAt,
    DateTime? DeliveredAt);

/// <summary>An endpoint's last 7 days.</summary>
public sealed record EndpointStatsRow(string EndpointId, int Total, int Succeeded, int Failed, int Pending, DateTime? LastAt);

public sealed record OwnerContact(string Email, string? Name);

public static class DeliveryStatuses
{
    public const string Pending = "pending";
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
}

/// <summary>
/// What db/012 keeps: API keys, idempotency answers, outgoing webhooks and their deliveries; and the API's own reads
/// of flows and runs (paged by cursor).
/// </summary>
public sealed class DeveloperRepository(SqlConnectionFactory factory)
{
    // ── Keys ──

    private const string KeyCols = "Id, WorkspaceId, Name, Prefix, KeyHash, Scopes, ExpiresAt, CreatedBy, CreatedByName, CreatedAt, LastUsedAt, RevokedAt, RevokedBy";

    public async Task<IReadOnlyList<ApiKeyRow>> KeysAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ApiKeyRow>($"SELECT TOP (200) {KeyCols} FROM flow.ApiKeys WHERE WorkspaceId = @WorkspaceId ORDER BY CreatedAt DESC;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<ApiKeyRow?> KeyByPrefixAsync(string prefix)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<ApiKeyRow>($"SELECT {KeyCols} FROM flow.ApiKeys WHERE Prefix = @Prefix;", new { Prefix = prefix });
    }

    /// <summary>False when the workspace already holds <paramref name="maxActive"/> active keys (or, rarely, the prefix is taken).</summary>
    public async Task<bool> InsertKeyAsync(ApiKeyRow k, int maxActive)
    {
        using var conn = factory.Flow();
        try
        {
            return await conn.ExecuteAsync(@"
INSERT INTO flow.ApiKeys (Id, WorkspaceId, Name, Prefix, KeyHash, Scopes, ExpiresAt, CreatedBy, CreatedByName, CreatedAt)
SELECT @Id, @WorkspaceId, @Name, @Prefix, @KeyHash, @Scopes, @ExpiresAt, @CreatedBy, @CreatedByName, @CreatedAt
WHERE (SELECT COUNT(*) FROM flow.ApiKeys WITH (UPDLOCK, HOLDLOCK)
       WHERE WorkspaceId = @WorkspaceId AND RevokedAt IS NULL AND (ExpiresAt IS NULL OR ExpiresAt > @CreatedAt)) < @Max;",
                new { k.Id, k.WorkspaceId, k.Name, k.Prefix, k.KeyHash, k.Scopes, k.ExpiresAt, k.CreatedBy, k.CreatedByName, k.CreatedAt, Max = maxActive }) > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task<bool> RevokeKeyAsync(Guid workspaceId, string id, string by, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync("UPDATE flow.ApiKeys SET RevokedAt = @Now, RevokedBy = @By WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND RevokedAt IS NULL;",
            new { WorkspaceId = workspaceId, Id = id, By = by, Now = now }) > 0;
    }

    public async Task TouchKeyAsync(string id, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.ApiKeys SET LastUsedAt = @Now WHERE Id = @Id;", new { Id = id, Now = now });
    }

    // ── Idempotency ──

    /// <summary>Claims the key for this request: null when it's ours now, else the row already kept (a live one).</summary>
    public async Task<Services.Developer.IdempotencyRow?> ClaimIdempotencyAsync(string keyId, string idemKey, Guid workspaceId, byte[] hash, DateTime now)
    {
        using var conn = factory.Flow();
        try
        {
            var claimed = await conn.ExecuteAsync(@"
DELETE FROM flow.ApiIdempotency WHERE KeyId = @KeyId AND IdemKey = @IdemKey AND CreatedAt < @Expired;
INSERT INTO flow.ApiIdempotency (KeyId, IdemKey, WorkspaceId, RequestHash, Status, CreatedAt)
SELECT @KeyId, @IdemKey, @WorkspaceId, @Hash, 0, @Now
WHERE NOT EXISTS (SELECT 1 FROM flow.ApiIdempotency WITH (UPDLOCK, HOLDLOCK) WHERE KeyId = @KeyId AND IdemKey = @IdemKey);",
                new { KeyId = keyId, IdemKey = idemKey, WorkspaceId = workspaceId, Hash = hash, Now = now, Expired = now - Services.Developer.DevConstants.IdempotencyWindow });
            if (claimed > 0) return null;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { /* someone else's claim: read it */ }
        return await conn.QuerySingleOrDefaultAsync<Services.Developer.IdempotencyRow>(
            "SELECT KeyId, IdemKey, RequestHash, Status, Response, CreatedAt FROM flow.ApiIdempotency WHERE KeyId = @KeyId AND IdemKey = @IdemKey;",
            new { KeyId = keyId, IdemKey = idemKey });
    }

    public async Task CompleteIdempotencyAsync(string keyId, string idemKey, int status, string response)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.ApiIdempotency SET Status = @Status, Response = @Response WHERE KeyId = @KeyId AND IdemKey = @IdemKey;",
            new { KeyId = keyId, IdemKey = idemKey, Status = status, Response = response });
    }

    /// <summary>The request failed on our side: the key is free again, so a retry runs it.</summary>
    public async Task ReleaseIdempotencyAsync(string keyId, string idemKey)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("DELETE FROM flow.ApiIdempotency WHERE KeyId = @KeyId AND IdemKey = @IdemKey AND Status = 0;", new { KeyId = keyId, IdemKey = idemKey });
    }

    // ── Endpoints ──

    private const string EndpointCols =
        "Id, WorkspaceId, Url, Description, Events, SecretCipher, SecretHint, Enabled, DisabledReason, DisabledAt, FailuresInARow, CreatedBy, CreatedByName, CreatedAt, UpdatedAt";

    public async Task<IReadOnlyList<EndpointRow>> EndpointsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<EndpointRow>($"SELECT {EndpointCols} FROM flow.WebhookEndpoints WHERE WorkspaceId = @WorkspaceId ORDER BY CreatedAt;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<EndpointRow?> EndpointAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<EndpointRow>($"SELECT {EndpointCols} FROM flow.WebhookEndpoints WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<EndpointRow?> EndpointByIdAsync(string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<EndpointRow>($"SELECT {EndpointCols} FROM flow.WebhookEndpoints WHERE Id = @Id;", new { Id = id });
    }

    /// <summary>The endpoints switched on in a workspace (each event looks these up first: nothing else happens without one).</summary>
    public async Task<IReadOnlyList<(string Id, string Events)>> ListeningAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<(string, string)>("SELECT Id, Events FROM flow.WebhookEndpoints WHERE WorkspaceId = @WorkspaceId AND Enabled = 1;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>False when the workspace already has <paramref name="max"/> endpoints.</summary>
    public async Task<bool> InsertEndpointAsync(EndpointRow e, int max)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
INSERT INTO flow.WebhookEndpoints (Id, WorkspaceId, Url, Description, Events, SecretCipher, SecretHint, Enabled, CreatedBy, CreatedByName, CreatedAt, UpdatedAt)
SELECT @Id, @WorkspaceId, @Url, @Description, @Events, @SecretCipher, @SecretHint, 1, @CreatedBy, @CreatedByName, @CreatedAt, @UpdatedAt
WHERE (SELECT COUNT(*) FROM flow.WebhookEndpoints WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId) < @Max;",
            new { e.Id, e.WorkspaceId, e.Url, e.Description, e.Events, e.SecretCipher, e.SecretHint, e.CreatedBy, e.CreatedByName, e.CreatedAt, e.UpdatedAt, Max = max }) > 0;
    }

    /// <summary>Its URL, description, events and on/off. Switching it on clears why it was off and its failures.</summary>
    public async Task UpdateEndpointAsync(Guid workspaceId, string id, string url, string? description, string events, bool enabled, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.WebhookEndpoints
SET Url = @Url, Description = @Description, Events = @Events, UpdatedAt = @Now,
    FailuresInARow = CASE WHEN @Enabled = 1 AND Enabled = 0 THEN 0 ELSE FailuresInARow END,
    DisabledReason = CASE WHEN @Enabled = 1 THEN NULL WHEN Enabled = 1 THEN NULL ELSE DisabledReason END,
    DisabledAt = CASE WHEN @Enabled = 1 THEN NULL WHEN Enabled = 1 THEN @Now ELSE DisabledAt END,
    Enabled = @Enabled
WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id, Url = url, Description = description, Events = events, Enabled = enabled, Now = now });
    }

    public async Task SetEndpointSecretAsync(Guid workspaceId, string id, byte[] cipher, string hint, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.WebhookEndpoints SET SecretCipher = @Cipher, SecretHint = @Hint, UpdatedAt = @Now WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id, Cipher = cipher, Hint = hint, Now = now });
    }

    public async Task<bool> DeleteEndpointAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
DELETE FROM flow.WebhookDeliveries WHERE WorkspaceId = @WorkspaceId AND EndpointId = @Id;
DELETE FROM flow.WebhookEndpoints WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id }) > 0;
    }

    /// <summary>A delivery failed every attempt: one more in a row. Returns how many now.</summary>
    public async Task<int> EndpointFailedAsync(string id)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteScalarAsync<int>(@"
UPDATE flow.WebhookEndpoints SET FailuresInARow = FailuresInARow + 1 OUTPUT INSERTED.FailuresInARow WHERE Id = @Id;", new { Id = id });
    }

    public async Task EndpointSucceededAsync(string id)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.WebhookEndpoints SET FailuresInARow = 0 WHERE Id = @Id AND FailuresInARow <> 0;", new { Id = id });
    }

    /// <summary>Switched off by the sender (repeated failures, 410): true when it was on.</summary>
    public async Task<bool> DisableEndpointAsync(string id, string reason, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.WebhookEndpoints SET Enabled = 0, DisabledReason = @Reason, DisabledAt = @Now, UpdatedAt = @Now WHERE Id = @Id AND Enabled = 1;",
            new { Id = id, Reason = reason, Now = now }) > 0;
    }

    public async Task<IReadOnlyList<EndpointStatsRow>> EndpointStatsAsync(Guid workspaceId, DateTime since)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<EndpointStatsRow>(@"
SELECT EndpointId, COUNT(*) AS Total,
       SUM(CASE WHEN Status = N'succeeded' THEN 1 ELSE 0 END) AS Succeeded,
       SUM(CASE WHEN Status = N'failed' THEN 1 ELSE 0 END) AS Failed,
       SUM(CASE WHEN Status = N'pending' THEN 1 ELSE 0 END) AS Pending,
       MAX(CreatedAt) AS LastAt
FROM flow.WebhookDeliveries
WHERE WorkspaceId = @WorkspaceId AND CreatedAt >= @Since AND Test = 0
GROUP BY EndpointId;", new { WorkspaceId = workspaceId, Since = since })).ToList();
    }

    // ── Deliveries ──

    private const string DeliveryCols =
        "Seq, Id, WorkspaceId, EndpointId, EventId, EventType, Payload, Status, Test, Attempts, NextAttemptAt, ResponseStatus, ResponseBody, Error, DurationMs, " +
        "CreatedAt, LastAttemptAt, DeliveredAt";

    /// <summary>Queues one event for each of these endpoints (an endpoint that already has it is skipped).</summary>
    public async Task InsertDeliveriesAsync(Guid workspaceId, IReadOnlyList<string> endpointIds, string eventId, string eventType, string payload, bool test, DateTime now)
    {
        using var conn = factory.Flow();
        foreach (var endpointId in endpointIds)
            await conn.ExecuteAsync(@"
INSERT INTO flow.WebhookDeliveries (Id, WorkspaceId, EndpointId, EventId, EventType, Payload, Status, Test, Attempts, NextAttemptAt, CreatedAt)
SELECT @Id, @WorkspaceId, @EndpointId, @EventId, @EventType, @Payload, N'pending', @Test, 0, @Now, @Now
WHERE NOT EXISTS (SELECT 1 FROM flow.WebhookDeliveries WHERE EndpointId = @EndpointId AND EventId = @EventId);",
                new { Id = Services.Developer.DevJson.NewDeliveryId(), WorkspaceId = workspaceId, EndpointId = endpointId, EventId = eventId, EventType = eventType,
                      Payload = payload, Test = test, Now = now });
    }

    /// <summary>A delivery made to be sent at once (Send test): not queued for the worker.</summary>
    public async Task<DeliveryRow> InsertSendingAsync(Guid workspaceId, string endpointId, string eventId, string eventType, string payload, DateTime now, string me)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleAsync<DeliveryRow>(@"
INSERT INTO flow.WebhookDeliveries (Id, WorkspaceId, EndpointId, EventId, EventType, Payload, Status, Test, Attempts, NextAttemptAt, LeaseUntil, LeaseBy, CreatedAt)
OUTPUT INSERTED.Seq, INSERTED.Id, INSERTED.WorkspaceId, INSERTED.EndpointId, INSERTED.EventId, INSERTED.EventType, INSERTED.Payload, INSERTED.Status,
       INSERTED.Test, INSERTED.Attempts, INSERTED.NextAttemptAt, INSERTED.ResponseStatus, INSERTED.ResponseBody, INSERTED.Error, INSERTED.DurationMs,
       INSERTED.CreatedAt, INSERTED.LastAttemptAt, INSERTED.DeliveredAt
VALUES (@Id, @WorkspaceId, @EndpointId, @EventId, @EventType, @Payload, N'pending', 1, 0, @Now, @Lease, @Me, @Now);",
            new { Id = Services.Developer.DevJson.NewDeliveryId(), WorkspaceId = workspaceId, EndpointId = endpointId, EventId = eventId, EventType = eventType,
                  Payload = payload, Now = now, Lease = now.AddMinutes(2), Me = me });
    }

    /// <summary>Deliveries due an attempt, leased to <paramref name="me"/>.</summary>
    public async Task<IReadOnlyList<DeliveryRow>> ClaimDeliveriesAsync(string me, DateTime now, int take, TimeSpan lease)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<DeliveryRow>(@"
WITH due AS (
    SELECT TOP (@Take) *
    FROM flow.WebhookDeliveries WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE Status = N'pending' AND NextAttemptAt <= @Now AND (LeaseUntil IS NULL OR LeaseUntil < @Now)
    ORDER BY NextAttemptAt, Seq
)
UPDATE due SET LeaseUntil = @LeaseUntil, LeaseBy = @Me
OUTPUT INSERTED.Seq, INSERTED.Id, INSERTED.WorkspaceId, INSERTED.EndpointId, INSERTED.EventId, INSERTED.EventType, INSERTED.Payload, INSERTED.Status,
       INSERTED.Test, INSERTED.Attempts, INSERTED.NextAttemptAt, INSERTED.ResponseStatus, INSERTED.ResponseBody, INSERTED.Error, INSERTED.DurationMs,
       INSERTED.CreatedAt, INSERTED.LastAttemptAt, INSERTED.DeliveredAt;",
            new { Take = take, Now = now, LeaseUntil = now.Add(lease), Me = me })).OrderBy(d => d.Seq).ToList();
    }

    /// <summary>An attempt's outcome: succeeded, failed for good, or pending again until <paramref name="next"/>.</summary>
    public async Task SaveAttemptAsync(long seq, string status, int attempts, DateTime? next, int? responseStatus, string? responseBody, string? error,
        int? durationMs, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.WebhookDeliveries
SET Status = @Status, Attempts = @Attempts, NextAttemptAt = @Next, LeaseUntil = NULL, LeaseBy = NULL, ResponseStatus = @ResponseStatus,
    ResponseBody = @ResponseBody, Error = @Error, DurationMs = @DurationMs, LastAttemptAt = @Now,
    DeliveredAt = CASE WHEN @Status = N'succeeded' THEN @Now ELSE DeliveredAt END
WHERE Seq = @Seq;",
            new
            {
                Seq = seq, Status = status, Attempts = attempts, Next = next, ResponseStatus = responseStatus, ResponseBody = Clip(responseBody, 1000),
                Error = Clip(error, 400), DurationMs = durationMs, Now = now,
            });
    }

    /// <summary>An endpoint's log, newest first, before <paramref name="beforeSeq"/>.</summary>
    public async Task<IReadOnlyList<DeliveryRow>> DeliveriesAsync(Guid workspaceId, string endpointId, string? status, long? beforeSeq, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<DeliveryRow>($@"
SELECT TOP (@Take) {DeliveryCols} FROM flow.WebhookDeliveries
WHERE WorkspaceId = @WorkspaceId AND EndpointId = @EndpointId
  AND (@Status IS NULL OR Status = @Status)
  AND (@Before IS NULL OR Seq < @Before)
ORDER BY Seq DESC;",
            new { WorkspaceId = workspaceId, EndpointId = endpointId, Status = status, Before = beforeSeq, Take = take })).ToList();
    }

    public async Task<DeliveryRow?> DeliveryAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<DeliveryRow>($"SELECT {DeliveryCols} FROM flow.WebhookDeliveries WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    /// <summary>Resend: the delivery is due again now, with its attempts counted afresh (not while one is under way).</summary>
    public async Task<bool> RequeueAsync(Guid workspaceId, string id, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.WebhookDeliveries SET Status = N'pending', Attempts = 0, NextAttemptAt = @Now, Error = NULL
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND (LeaseUntil IS NULL OR LeaseUntil < @Now);",
            new { WorkspaceId = workspaceId, Id = id, Now = now }) > 0;
    }

    public async Task<IReadOnlyList<OwnerContact>> OwnersAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<OwnerContact>(@"
SELECT Email, Name FROM flow.WorkspaceMembers
WHERE WorkspaceId = @WorkspaceId AND Role = N'owner' AND Email IS NOT NULL AND Email <> N'';", new { WorkspaceId = workspaceId })).ToList();
    }

    /// <summary>Deliveries over 30 days old, and idempotency answers past their day.</summary>
    public async Task PruneAsync(DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE TOP (5000) FROM flow.WebhookDeliveries WHERE CreatedAt < @Deliveries;
DELETE TOP (5000) FROM flow.ApiIdempotency WHERE CreatedAt < @Idempotency;",
            new { Deliveries = now.AddDays(-30), Idempotency = now - Services.Developer.DevConstants.IdempotencyWindow }, commandTimeout: 120);
    }

    // ── The API's reads ──

    private const string FlowCols =
        "Id, WorkspaceId, Name, Description, Enabled, Definition, Version, TriggerApp, TriggerEvent, TriggerScope, HookId, HookSecretCipher, " +
        "TemplateId, PausedReason, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt, LastRunAt, LastRunStatus";

    public async Task<FlowRow?> FlowAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<FlowRow>($"SELECT {FlowCols} FROM flow.Flows WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    private const string RunCols =
        "Seq, Id, WorkspaceId, FlowId, FlowName, FlowVersion, Definition, TriggerData, Context, Position, Attempt, Status, Test, ReplayOf, EventId, StepCount, " +
        "Error, StartedAt, FinishedAt, DurationMs, NotBefore";

    /// <summary>
    /// A page of runs, newest first: of one flow, in one status, started in [<paramref name="from"/>, <paramref name="to"/>),
    /// after the cursor (<paramref name="afterAt"/>, <paramref name="afterSeq"/>: the last run of the page before).
    /// </summary>
    public async Task<IReadOnlyList<RunRow>> RunsPageAsync(Guid workspaceId, string? flowId, string? status, DateTime? from, DateTime? to, bool tests,
        DateTime? afterAt, long? afterSeq, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<RunRow>($@"
SELECT TOP (@Take) {RunCols} FROM flow.Runs
WHERE WorkspaceId = @WorkspaceId
  AND (@FlowId IS NULL OR FlowId = @FlowId)
  AND (@Status IS NULL OR Status = @Status)
  AND (@From IS NULL OR StartedAt >= @From)
  AND (@To IS NULL OR StartedAt < @To)
  AND (@Tests = 1 OR Test = 0)
  AND (@AfterAt IS NULL OR StartedAt < @AfterAt OR (StartedAt = @AfterAt AND Seq < @AfterSeq))
ORDER BY StartedAt DESC, Seq DESC;",
            new { WorkspaceId = workspaceId, FlowId = flowId, Status = status, From = from, To = to, Tests = tests, AfterAt = afterAt, AfterSeq = afterSeq ?? long.MaxValue, Take = take }))
            .ToList();
    }

    private static string? Clip(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
