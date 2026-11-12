using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>
/// What db/007 keeps: a workspace's sealed secrets (the HTTP request step's credentials), Approval steps' requests and
/// their decisions, and the newest payload each webhook trigger received.
/// </summary>
public sealed class StepsRepository(SqlConnectionFactory factory)
{
    // ── Secrets ──

    private const string SecretCols = "Id, WorkspaceId, Name, Cipher, Hint, CreatedBy, CreatedByName, CreatedAt, UpdatedAt, LastUsedAt";

    public async Task<IReadOnlyList<SecretRow>> SecretsAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<SecretRow>($"SELECT {SecretCols} FROM flow.Secrets WHERE WorkspaceId = @WorkspaceId ORDER BY Name;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<SecretRow?> SecretAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<SecretRow>($"SELECT {SecretCols} FROM flow.Secrets WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    /// <summary>False when the workspace already has a secret by that name.</summary>
    public async Task<bool> InsertSecretAsync(SecretRow r)
    {
        using var conn = factory.Flow();
        try
        {
            return await conn.ExecuteAsync(@"
INSERT INTO flow.Secrets (Id, WorkspaceId, Name, Cipher, Hint, CreatedBy, CreatedByName, CreatedAt, UpdatedAt)
SELECT @Id, @WorkspaceId, @Name, @Cipher, @Hint, @CreatedBy, @CreatedByName, @CreatedAt, @UpdatedAt
WHERE NOT EXISTS (SELECT 1 FROM flow.Secrets WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND Name = @Name);",
                new { r.Id, r.WorkspaceId, r.Name, r.Cipher, r.Hint, r.CreatedBy, r.CreatedByName, r.CreatedAt, r.UpdatedAt }) > 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601) { return false; }
    }

    public async Task<bool> UpdateSecretAsync(Guid workspaceId, string id, byte[] cipher, string? hint, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync("UPDATE flow.Secrets SET Cipher = @Cipher, Hint = @Hint, UpdatedAt = @Now WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id, Cipher = cipher, Hint = hint, Now = now }) > 0;
    }

    public async Task<bool> DeleteSecretAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync("DELETE FROM flow.Secrets WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id }) > 0;
    }

    public async Task TouchSecretAsync(Guid workspaceId, string id, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Secrets SET LastUsedAt = @Now WHERE WorkspaceId = @WorkspaceId AND Id = @Id;", new { WorkspaceId = workspaceId, Id = id, Now = now });
    }

    // ── Approvals ──

    private const string ApprovalCols =
        "Id, WorkspaceId, FlowId, FlowName, RunId, StepId, Title, Details, Approvers, Status, DecidedBy, DecidedByName, DecidedAt, DecidedVia, Comment, RequestedAt, ExpiresAt";

    /// <summary>A run's request for a step, made once: the one already there when the step comes round again.</summary>
    public async Task<ApprovalRow> InsertApprovalAsync(ApprovalRow a)
    {
        using var conn = factory.Flow();
        try
        {
            await conn.ExecuteAsync(@"
INSERT INTO flow.Approvals (Id, WorkspaceId, FlowId, FlowName, RunId, StepId, Title, Details, Approvers, Status, RequestedAt, ExpiresAt)
VALUES (@Id, @WorkspaceId, @FlowId, @FlowName, @RunId, @StepId, @Title, @Details, @Approvers, N'pending', @RequestedAt, @ExpiresAt);",
                new { a.Id, a.WorkspaceId, a.FlowId, a.FlowName, a.RunId, a.StepId, a.Title, a.Details, a.Approvers, a.RequestedAt, a.ExpiresAt });
            return a;
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601)
        {
            return await conn.QuerySingleAsync<ApprovalRow>($"SELECT {ApprovalCols} FROM flow.Approvals WHERE RunId = @RunId AND StepId = @StepId;",
                new { a.RunId, a.StepId });
        }
    }

    public async Task<ApprovalRow?> ApprovalAsync(string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<ApprovalRow>($"SELECT {ApprovalCols} FROM flow.Approvals WHERE Id = @Id;", new { Id = id });
    }

    /// <summary>A workspace's requests, newest first: those still waiting, or the decided ones too.</summary>
    public async Task<IReadOnlyList<ApprovalRow>> ApprovalsAsync(Guid workspaceId, bool pendingOnly, int take)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ApprovalRow>($@"
SELECT TOP (@Take) {ApprovalCols} FROM flow.Approvals
WHERE WorkspaceId = @WorkspaceId AND (@PendingOnly = 0 OR Status = N'pending')
ORDER BY RequestedAt DESC;", new { WorkspaceId = workspaceId, PendingOnly = pendingOnly, Take = take })).ToList();
    }

    /// <summary>A run's requests (its Approval steps').</summary>
    public async Task<IReadOnlyList<ApprovalRow>> RunApprovalsAsync(Guid workspaceId, string runId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<ApprovalRow>($"SELECT {ApprovalCols} FROM flow.Approvals WHERE WorkspaceId = @WorkspaceId AND RunId = @RunId ORDER BY RequestedAt;",
            new { WorkspaceId = workspaceId, RunId = runId })).ToList();
    }

    /// <summary>
    /// The decision, once: only a request still pending and not yet expired changes, and its run (waiting on it) is
    /// woken to carry on now. False when someone decided first, it expired, or the run was cancelled.
    /// </summary>
    public async Task<bool> DecideAsync(string id, string status, string by, string? byName, string via, string? comment, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.OpenAsync();
        using var tx = conn.BeginTransaction();
        var runId = await conn.QuerySingleOrDefaultAsync<string?>(@"
UPDATE flow.Approvals
SET Status = @Status, DecidedBy = @By, DecidedByName = @ByName, DecidedAt = @Now, DecidedVia = @Via, Comment = @Comment
OUTPUT INSERTED.RunId
WHERE Id = @Id AND Status = N'pending' AND ExpiresAt > @Now;",
            new { Id = id, Status = status, By = by, ByName = byName, Now = now, Via = via, Comment = comment }, tx);
        if (runId is null) { tx.Rollback(); return false; }
        await conn.ExecuteAsync("UPDATE flow.Runs SET NotBefore = @Now WHERE Id = @RunId AND Status = N'waiting';", new { RunId = runId, Now = now }, tx);
        tx.Commit();
        return true;
    }

    /// <summary>A pending request past its time: marked expired (the run takes its "timed out" path).</summary>
    public async Task ExpireAsync(string id, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Approvals SET Status = N'expired', DecidedAt = @Now WHERE Id = @Id AND Status = N'pending';", new { Id = id, Now = now });
    }

    /// <summary>A cancelled run's requests stop waiting.</summary>
    public async Task CancelApprovalsAsync(string runId, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Approvals SET Status = N'cancelled', DecidedAt = @Now WHERE RunId = @RunId AND Status = N'pending';", new { RunId = runId, Now = now });
    }

    // ── Webhook samples ──

    public async Task SaveHookSampleAsync(Guid workspaceId, string flowId, string payload, DateTime at)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.HookSamples SET Payload = @Payload, ReceivedAt = @At WHERE FlowId = @FlowId;
IF @@ROWCOUNT = 0
    INSERT INTO flow.HookSamples (FlowId, WorkspaceId, Payload, ReceivedAt) VALUES (@FlowId, @WorkspaceId, @Payload, @At);",
            new { WorkspaceId = workspaceId, FlowId = flowId, Payload = payload, At = at });
    }

    public async Task<HookSampleRow?> HookSampleAsync(Guid workspaceId, string flowId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<HookSampleRow>(
            "SELECT Payload, ReceivedAt FROM flow.HookSamples WHERE WorkspaceId = @WorkspaceId AND FlowId = @FlowId;",
            new { WorkspaceId = workspaceId, FlowId = flowId });
    }

    /// <summary>Housekeeping: approvals of runs long gone, and samples of deleted flows.</summary>
    public async Task PruneAsync(DateTime now, int keepRunsDays)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
DELETE TOP (5000) FROM flow.Approvals WHERE RequestedAt < @Old AND Status <> N'pending';
DELETE s FROM flow.HookSamples s WHERE NOT EXISTS (SELECT 1 FROM flow.Flows f WHERE f.Id = s.FlowId);",
            new { Old = now.AddDays(-keepRunsDays) }, commandTimeout: 120);
    }
}
