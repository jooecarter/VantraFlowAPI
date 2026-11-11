using Dapper;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Data;

/// <summary>
/// All access to the shared <c>Vantra.dbo.UserDetails</c> table. Every
/// connection here comes from <see cref="SqlConnectionFactory.Vantra"/> —
/// this repository never touches the product database. Identical to the
/// other Vantra APIs so one account authenticates across the whole suite.
/// </summary>
public sealed class UsersRepository(SqlConnectionFactory factory) : IUserDirectory
{
    public async Task<UserDetails?> FindByEmailAsync(string email)
    {
        const string sql = @"
SELECT TOP 1 UserID, FirstName, LastName, Email, Password, PasswordHash, VantraID
FROM dbo.UserDetails
WHERE Email = @Email;";

        using var conn = factory.Vantra();
        return await conn.QuerySingleOrDefaultAsync<UserDetails>(sql, new { Email = email });
    }

    public async Task<bool> EmailExistsAsync(string email)
    {
        using var conn = factory.Vantra();
        var n = await conn.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM dbo.UserDetails WHERE Email = @Email;",
            new { Email = email });
        return n > 0;
    }

    /// <summary>
    /// Resolve display identity (name + email) for a batch of cross-product
    /// VantraIDs — used to render workspace members, who are stored by VantraID
    /// in the product DB. Ids with no matching row are simply omitted.
    /// </summary>
    public async Task<IReadOnlyList<UserDetails>> FindByVantraIdsAsync(IEnumerable<string> vantraIds)
    {
        var ids = vantraIds.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().ToList();
        if (ids.Count == 0) return Array.Empty<UserDetails>();
        using var conn = factory.Vantra();
        return (await conn.QueryAsync<UserDetails>(
            "SELECT UserID, FirstName, LastName, Email, VantraID FROM dbo.UserDetails WHERE VantraID IN @Ids;",
            new { Ids = ids })).ToList();
    }

    /// <summary>
    /// One identity by VantraID — name and email only (no password columns).
    /// Flow matches invitations on this email, not the JWT claim, which can go
    /// stale after an email change.
    /// </summary>
    public async Task<UserDetails?> FindByVantraIdAsync(string vantraId)
    {
        if (string.IsNullOrWhiteSpace(vantraId)) return null;
        using var conn = factory.Vantra();
        return await conn.QueryFirstOrDefaultAsync<UserDetails>(
            "SELECT TOP 1 UserID, FirstName, LastName, Email, VantraID FROM dbo.UserDetails WHERE VantraID = @VantraID;",
            new { VantraID = vantraId });
    }

    /// <summary>The VantraIDs of the accounts with this email (normally one; none for an address with no account).</summary>
    public async Task<IReadOnlyList<string>> FindVantraIdsByEmailAsync(string email)
    {
        using var conn = factory.Vantra();
        return (await conn.QueryAsync<string>(
            "SELECT VantraID FROM dbo.UserDetails WHERE Email = @Email AND VantraID IS NOT NULL;",
            new { Email = email })).ToList();
    }

    /// <summary>
    /// Inserts a brand-new identity. Writes the BCrypt hash (the column this
    /// API owns) AND the legacy plaintext Password — the latter is NOT NULL
    /// and the original Vantra app still authenticates against it, so keeping
    /// it in sync is what lets one account work across every product during
    /// the migration. Returns the freshly-assigned identity UserID.
    /// </summary>
    public async Task<int> CreateAsync(
        string firstName,
        string lastName,
        string email,
        string plaintextPassword,
        string passwordHash,
        string vantraId)
    {
        const string sql = @"
INSERT INTO dbo.UserDetails (FirstName, LastName, Email, Password, PasswordHash, VantraID)
OUTPUT INSERTED.UserID
VALUES (@FirstName, @LastName, @Email, @Password, @PasswordHash, @VantraID);";

        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            Password = plaintextPassword,
            PasswordHash = passwordHash,
            VantraID = vantraId,
        });
    }

    /// <summary>
    /// Inserts a user provisioned via a social provider (Google/Microsoft).
    /// There's no password, but the legacy Password column is NOT NULL, so we
    /// store an unusable random sentinel and leave PasswordHash null — neither
    /// password login path can match it, so the account is social-only until
    /// the user sets a password. Returns the new identity UserID.
    /// </summary>
    public async Task<int> CreateSocialAsync(
        string firstName,
        string lastName,
        string email,
        string vantraId)
    {
        const string sql = @"
INSERT INTO dbo.UserDetails (FirstName, LastName, Email, Password, VantraID)
OUTPUT INSERTED.UserID
VALUES (@FirstName, @LastName, @Email, @Password, @VantraID);";

        using var conn = factory.Vantra();
        return await conn.ExecuteScalarAsync<int>(sql, new
        {
            FirstName = firstName,
            LastName = lastName,
            Email = email,
            // Unguessable sentinel — never a real password.
            Password = "(social-login):" + Guid.NewGuid().ToString("n"),
            VantraID = vantraId,
        });
    }

    /// <summary>
    /// Transparent upgrade for a legacy row that had no hash: stamps the
    /// BCrypt hash computed from the password the user just proved they know.
    /// </summary>
    public async Task SetPasswordHashAsync(int userId, string passwordHash)
    {
        using var conn = factory.Vantra();
        await conn.ExecuteAsync(
            "UPDATE dbo.UserDetails SET PasswordHash = @PasswordHash WHERE UserID = @UserID;",
            new { UserID = userId, PasswordHash = passwordHash });
    }

    /// <summary>
    /// Gives a legacy row a durable cross-product id on first sign-in so the
    /// JWT always carries a stable <c>sub</c>.
    /// </summary>
    public async Task SetVantraIdAsync(int userId, string vantraId)
    {
        using var conn = factory.Vantra();
        await conn.ExecuteAsync(
            "UPDATE dbo.UserDetails SET VantraID = @VantraID WHERE UserID = @UserID;",
            new { UserID = userId, VantraID = vantraId });
    }
}

/// <summary>Looking people up in the shared identity database (<see cref="UsersRepository"/>; a fake in tests).</summary>
public interface IUserDirectory
{
    Task<UserDetails?> FindByEmailAsync(string email);
    Task<UserDetails?> FindByVantraIdAsync(string vantraId);
}
