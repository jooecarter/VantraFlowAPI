using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>Your Vantra account as Settings → Profile shows it: the name and email, and the photo and time zone once the identity has them.</summary>
public sealed record AccountProfile(string? FirstName, string? LastName, string Email, string? Avatar, string? Timezone);

/// <summary>
/// The profile on the shared identity, <c>Vantra.dbo.UserDetails</c>: the same row Orbit and Projects read, so a change
/// shows in all three. The name is FirstName and LastName; the photo (a data URL) and time zone are the personal columns
/// VantraProjectsAPI's db/96 adds (Avatar, Timezone, in its picker's shape "Europe/London (GMT)"). Until those columns
/// exist, only the name is read and saved. Flow's own copies of the name (each workspace's member list) follow it.
/// </summary>
public sealed class ProfileStore(SqlConnectionFactory factory) : IProfileStore
{
    private static volatile bool personal;
    private static DateTime checkedAt = DateTime.MinValue;

    public const string PersonalProbeSql = @"
SELECT CASE WHEN COL_LENGTH(N'dbo.UserDetails', N'Avatar') IS NOT NULL AND COL_LENGTH(N'dbo.UserDetails', N'Timezone') IS NOT NULL
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>Has the identity got the photo and time zone columns? Remembered once yes, asked again after 30 seconds when no.</summary>
    public async Task<bool> PersonalReadyAsync()
    {
        if (personal) return true;
        if (DateTime.UtcNow - checkedAt < FlowSchema.NegativeTtl) return false;
        using var conn = factory.Vantra();
        personal = await conn.ExecuteScalarAsync<bool>(PersonalProbeSql);
        checkedAt = DateTime.UtcNow;
        return personal;
    }

    public async Task<AccountProfile?> GetAsync(string vantraId)
    {
        var withPersonal = await PersonalReadyAsync();
        using var conn = factory.Vantra();
        return await conn.QuerySingleOrDefaultAsync<AccountProfile>(withPersonal
            ? "SELECT TOP 1 FirstName, LastName, Email, Avatar, Timezone FROM dbo.UserDetails WHERE VantraID = @VantraID;"
            : "SELECT TOP 1 FirstName, LastName, Email, CAST(NULL AS NVARCHAR(MAX)) AS Avatar, CAST(NULL AS NVARCHAR(80)) AS Timezone FROM dbo.UserDetails WHERE VantraID = @VantraID;",
            new { VantraID = vantraId });
    }

    public async Task SaveNameAsync(string vantraId, string firstName, string lastName)
    {
        using (var conn = factory.Vantra())
            await conn.ExecuteAsync("UPDATE dbo.UserDetails SET FirstName = @First, LastName = @Last WHERE VantraID = @VantraID;",
                new { VantraID = vantraId, First = firstName, Last = lastName });
        var full = $"{firstName} {lastName}".Trim();
        using var flow = factory.Flow();
        await flow.ExecuteAsync("UPDATE flow.WorkspaceMembers SET Name = @Name WHERE VantraId = @VantraID;", new { VantraID = vantraId, Name = full });
    }

    /// <summary>The photo and time zone: each left as it is when null, cleared when empty. Only once <see cref="PersonalReadyAsync"/>.</summary>
    public async Task SavePersonalAsync(string vantraId, string? avatar, string? timezone)
    {
        if (avatar is null && timezone is null) return;
        using var conn = factory.Vantra();
        await conn.ExecuteAsync(@"
UPDATE dbo.UserDetails
SET Avatar = CASE WHEN @SetAvatar = 1 THEN NULLIF(@Avatar, N'') ELSE Avatar END,
    Timezone = CASE WHEN @SetZone = 1 THEN NULLIF(@Zone, N'') ELSE Timezone END
WHERE VantraID = @VantraID;",
            new { VantraID = vantraId, SetAvatar = avatar is not null, Avatar = avatar ?? "", SetZone = timezone is not null, Zone = timezone ?? "" });
    }
}

/// <summary>The shared profile (<see cref="ProfileStore"/>; a fake in tests).</summary>
public interface IProfileStore
{
    Task<bool> PersonalReadyAsync();
    Task<AccountProfile?> GetAsync(string vantraId);
    Task SaveNameAsync(string vantraId, string firstName, string lastName);
    Task SavePersonalAsync(string vantraId, string? avatar, string? timezone);
}
