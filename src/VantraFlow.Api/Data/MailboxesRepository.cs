using Dapper;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Mail;

namespace VantraFlow.Api.Data;

/// <summary>
/// What db/013 keeps: a workspace's SMTP mailboxes (the password sealed by the API; wiped when one is removed) and the
/// workspace's default sender (<c>flow.Workspaces.DefaultSender</c>). Nothing here is asked until the probe finds db/013.
/// </summary>
public sealed class MailboxesRepository(SqlConnectionFactory factory) : IMailboxStore
{
    /// <summary>db/013: the column it adds last, by name in <c>sys.columns</c> (never a constraint's definition).</summary>
    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'flow.Workspaces') AND name = N'DefaultSender')
             AND OBJECT_ID(N'flow.Mailboxes', N'U') IS NOT NULL
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    private static volatile bool ready;
    private static DateTime checkedAt = DateTime.MinValue;

    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < FlowSchema.NegativeTtl) return false;
        try
        {
            using var conn = factory.Flow();
            ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        }
        catch (Microsoft.Data.SqlClient.SqlException) { ready = false; }
        checkedAt = DateTime.UtcNow;
        return ready;
    }

    private const string Cols =
        "Id, WorkspaceId, Host, Port, Security, Username, PasswordCipher, FromEmail, FromName, Status, LastError, CreatedBy, CreatedByName, CreatedAt, UpdatedAt, LastUsedAt, RemovedAt";

    public async Task<IReadOnlyList<MailboxRow>> ListAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return (await conn.QueryAsync<MailboxRow>($"SELECT {Cols} FROM flow.Mailboxes WHERE WorkspaceId = @WorkspaceId AND RemovedAt IS NULL ORDER BY CreatedAt;",
            new { WorkspaceId = workspaceId })).ToList();
    }

    public async Task<MailboxRow?> GetAsync(Guid workspaceId, string id)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<MailboxRow>($"SELECT {Cols} FROM flow.Mailboxes WHERE WorkspaceId = @WorkspaceId AND Id = @Id;",
            new { WorkspaceId = workspaceId, Id = id });
    }

    public async Task<bool> InsertAsync(MailboxRow r, int max)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
INSERT INTO flow.Mailboxes (Id, WorkspaceId, Host, Port, Security, Username, PasswordCipher, FromEmail, FromName, Status, CreatedBy, CreatedByName, CreatedAt, UpdatedAt)
SELECT @Id, @WorkspaceId, @Host, @Port, @Security, @Username, @PasswordCipher, @FromEmail, @FromName, N'active', @CreatedBy, @CreatedByName, @CreatedAt, @UpdatedAt
WHERE (SELECT COUNT(*) FROM flow.Mailboxes WITH (UPDLOCK, HOLDLOCK) WHERE WorkspaceId = @WorkspaceId AND RemovedAt IS NULL) < @Max;",
            new
            {
                r.Id, r.WorkspaceId, r.Host, r.Port, r.Security, r.Username, r.PasswordCipher, r.FromEmail, r.FromName, r.CreatedBy, r.CreatedByName, r.CreatedAt,
                r.UpdatedAt, Max = max,
            }) > 0;
    }

    public async Task<bool> UpdateAsync(Guid workspaceId, string id, SmtpRules.Settings s, byte[]? passwordCipher, DateTime now)
    {
        using var conn = factory.Flow();
        return await conn.ExecuteAsync(@"
UPDATE flow.Mailboxes
SET Host = @Host, Port = @Port, Security = @Security, Username = @Username, PasswordCipher = ISNULL(@Cipher, PasswordCipher), FromEmail = @FromEmail,
    FromName = @FromName, Status = N'active', LastError = NULL, UpdatedAt = @Now
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND RemovedAt IS NULL;",
            new { WorkspaceId = workspaceId, Id = id, s.Host, s.Port, s.Security, s.Username, Cipher = passwordCipher, s.FromEmail, s.FromName, Now = now }) > 0;
    }

    public async Task RemoveAsync(Guid workspaceId, string id, DateTime now)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Mailboxes SET PasswordCipher = NULL, RemovedAt = @Now, UpdatedAt = @Now
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND RemovedAt IS NULL;", new { WorkspaceId = workspaceId, Id = id, Now = now });
    }

    public async Task TouchAsync(Guid workspaceId, string id, string? error)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync(@"
UPDATE flow.Mailboxes
SET LastUsedAt = SYSUTCDATETIME(), LastError = @Error, Status = CASE WHEN @Error IS NULL THEN N'active' ELSE N'error' END
WHERE WorkspaceId = @WorkspaceId AND Id = @Id AND RemovedAt IS NULL;",
            new { WorkspaceId = workspaceId, Id = id, Error = error is { Length: > 400 } ? error[..400] : error });
    }

    public async Task<string?> DefaultAsync(Guid workspaceId)
    {
        using var conn = factory.Flow();
        return await conn.QuerySingleOrDefaultAsync<string?>("SELECT DefaultSender FROM flow.Workspaces WHERE Id = @Id;", new { Id = workspaceId });
    }

    public async Task SetDefaultAsync(Guid workspaceId, string? value)
    {
        using var conn = factory.Flow();
        await conn.ExecuteAsync("UPDATE flow.Workspaces SET DefaultSender = @Value WHERE Id = @Id;", new { Id = workspaceId, Value = value });
    }
}
