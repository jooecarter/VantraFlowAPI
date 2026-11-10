using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Credentials at rest and on the wire, copied from Vantra Orbit's WebhookSecrets (db/012):
/// <para>
/// <b>Sealed</b> (<see cref="Protect"/> / <see cref="Unprotect"/>): AES-256-GCM, stored as
/// <c>version (1 byte) ‖ nonce (12) ‖ tag (16) ‖ ciphertext</c>, with the row bound in as associated data
/// (<c>"{workspaceId}:{rowId}"</c>), so a ciphertext copied to another row doesn't open. Connection tokens, the
/// apps' webhook secrets and webhook triggers' secrets are kept this way. The key: <c>Connectors:SecretKey</c>
/// (base64 of 32 bytes; version 2) when set, else HKDF-SHA256 of <c>Jwt:Key</c> (version 1). Both are kept, so
/// setting the dedicated key later still opens what the derived one sealed.
/// </para>
/// <para><b>Signatures</b>: HMAC-SHA256 over <c>"{timestamp}.{rawBody}"</c> as <c>sha256=&lt;hex&gt;</c>, Orbit's
/// <c>X-Vantra-Signature</c>. Never logs a secret.</para>
/// </summary>
public sealed class FlowSecrets
{
    public const byte DerivedKeyVersion = 1;
    public const byte DedicatedKeyVersion = 2;
    public const string DerivationInfo = "vantra-flow/secrets/v1";
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int HeaderSize = 1 + NonceSize + TagSize;
    /// <summary>The columns are VARBINARY(400).</summary>
    public const int MaxCipherBytes = 400;
    public const string SignaturePrefix = "sha256=";

    private readonly byte[]? dedicatedKey;
    private readonly byte[] derivedKey;

    public FlowSecrets(IConfiguration config, IHostEnvironment? env = null, ILogger<FlowSecrets>? log = null)
    {
        var root = config["Jwt:Key"] is { Length: > 0 } jwt ? Encoding.UTF8.GetBytes(jwt) : RandomNumberGenerator.GetBytes(32);
        derivedKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, root, 32, salt: [], info: Encoding.UTF8.GetBytes(DerivationInfo));
        if (config["Connectors:SecretKey"] is { Length: > 0 } configured)
        {
            byte[] key;
            try { key = Convert.FromBase64String(configured.Trim()); }
            catch (FormatException) { key = []; }
            if (key.Length != 32) throw new InvalidOperationException("Connectors:SecretKey must be the base64 of exactly 32 random bytes.");
            dedicatedKey = key;
        }
        else if (env?.IsProduction() == true)
        {
            log?.LogWarning("Connectors:SecretKey is not set: connection tokens are sealed with a key derived from Jwt:Key. Rotating Jwt:Key would make them unreadable; set a dedicated key.");
        }
    }

    public byte[] Protect(string secret, Guid workspaceId, string rowId)
    {
        var plain = Encoding.UTF8.GetBytes(secret);
        if (HeaderSize + plain.Length > MaxCipherBytes) throw new ArgumentException("The secret is too long to store.", nameof(secret));
        var (version, key) = dedicatedKey is { } k ? (DedicatedKeyVersion, k) : (DerivedKeyVersion, derivedKey);
        var output = new byte[HeaderSize + plain.Length];
        output[0] = version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(HeaderSize), output.AsSpan(1 + NonceSize, TagSize), Aad(workspaceId, rowId));
        return output;
    }

    /// <summary>The secret sealed for this row, or null when it can't be opened. Never throws.</summary>
    public string? Unprotect(byte[]? sealedSecret, Guid workspaceId, string rowId)
    {
        if (sealedSecret is null || sealedSecret.Length < HeaderSize) return null;
        var key = sealedSecret[0] switch { DedicatedKeyVersion => dedicatedKey, DerivedKeyVersion => derivedKey, _ => null };
        if (key is null) return null;
        try
        {
            var plain = new byte[sealedSecret.Length - HeaderSize];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(sealedSecret.AsSpan(1, NonceSize), sealedSecret.AsSpan(HeaderSize), sealedSecret.AsSpan(1 + NonceSize, TagSize), plain, Aad(workspaceId, rowId));
            return Encoding.UTF8.GetString(plain);
        }
        catch (CryptographicException) { return null; }
    }

    private static byte[] Aad(Guid workspaceId, string rowId) => Encoding.UTF8.GetBytes(workspaceId.ToString("D") + ":" + rowId);

    /// <summary>An unguessable id: <paramref name="prefix"/> + 24 random bytes, base64url (32 characters).</summary>
    public static string NewId(string prefix) => prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

    /// <summary>A short row id: <paramref name="prefix"/> + 16 random bytes as hex.</summary>
    public static string NewRowId(string prefix) => prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>A webhook trigger's HMAC secret: <c>whsec_</c> + 24 random bytes as hex, as Orbit's and Projects'.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));

    public static string Sign(string secret, long timestamp, ReadOnlySpan<byte> body) =>
        SignaturePrefix + Convert.ToHexStringLower(Hmac(secret, timestamp, body));

    public static byte[] Hmac(string secret, long timestamp, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes(timestamp.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message);
    }

    /// <summary>Constant-time check of a <c>sha256=&lt;hex&gt;</c> header against the expected MAC.</summary>
    public static bool SignatureMatches(string? header, byte[] expected)
    {
        if (string.IsNullOrWhiteSpace(header)) return false;
        var h = header.Trim();
        if (h.StartsWith(SignaturePrefix, StringComparison.OrdinalIgnoreCase)) h = h[SignaturePrefix.Length..];
        byte[] given;
        try { given = Convert.FromHexString(h); }
        catch (FormatException) { return false; }
        return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
    }

    /// <summary>
    /// A signed request in the suite's shape: <c>X-…-Signature: sha256=hex(HMAC(secret, "{ts}.{body}"))</c> and a
    /// unix timestamp within five minutes of now (a replay of an old delivery is refused).
    /// </summary>
    public static bool VerifyTimestamped(string secret, string? signature, string? timestamp, ReadOnlySpan<byte> body, DateTime nowUtc)
    {
        if (!long.TryParse(timestamp, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ts)) return false;
        var age = nowUtc - DateTimeOffset.FromUnixTimeSeconds(ts).UtcDateTime;
        if (age > TimeSpan.FromMinutes(5) || age < TimeSpan.FromMinutes(-1)) return false;
        return SignatureMatches(signature, Hmac(secret, ts, body));
    }
}
