namespace VantraFlow.Api.Services.Workspaces;

/// <summary>
/// A workspace logo is a small image sent inline as a data URL — Flow has no
/// blob storage, and the SPA resizes to ~160px first (≈5–20 KB). Accepted:
/// <c>data:image/(png|jpeg|webp);base64,…</c>, at most 100,000 characters in
/// all, strict base64 (no whitespace), and the decoded bytes must really be
/// that format (PNG / JPEG / WebP magic bytes). SVG is refused — it can carry
/// script. The CHECK in db/005 backs the prefix and length up.
/// </summary>
public static class LogoValidator
{
    public const int MaxLength = 100_000;

    private static readonly (string Mime, string Prefix)[] Kinds =
    [
        ("image/png", "data:image/png;base64,"),
        ("image/jpeg", "data:image/jpeg;base64,"),
        ("image/webp", "data:image/webp;base64,"),
    ];

    /// <summary>True when <paramref name="dataUrl"/> is an acceptable logo.</summary>
    public static bool IsValid(string? dataUrl) => Problem(dataUrl) is null;

    /// <summary>Why <paramref name="dataUrl"/> isn't acceptable, in words for the error; null when it is.</summary>
    public static string? Problem(string? dataUrl) =>
        ImageProblem(dataUrl, MaxLength, "logo", "use an image under about 70 KB");

    /// <summary>
    /// The shared data-URL image check (logo and cover): PNG / JPEG / WebP,
    /// at most <paramref name="maxLength"/> characters, strict base64, magic
    /// bytes matching the type. <paramref name="noun"/> names it in the message.
    /// </summary>
    internal static string? ImageProblem(string? dataUrl, int maxLength, string noun, string sizeHint)
    {
        if (string.IsNullOrEmpty(dataUrl)) return $"The {noun} is empty.";
        if (dataUrl.Length > maxLength) return $"The {noun} is too large — {sizeHint}.";

        var kind = Kinds.FirstOrDefault(k => dataUrl.StartsWith(k.Prefix, StringComparison.Ordinal));
        if (kind.Prefix is null) return $"The {noun} must be a PNG, JPEG or WebP image (a data:image/…;base64 URL).";

        var invalid = $"The {noun}'s image data isn't valid base64.";
        var b64 = dataUrl.AsSpan(kind.Prefix.Length);
        if (b64.Length < 16 || b64.Length % 4 != 0) return invalid;
        var pad = 0;
        for (var i = 0; i < b64.Length; i++)
        {
            var ch = b64[i];
            if (ch == '=')
            {
                // Padding only at the very end, at most two.
                if (i < b64.Length - 2) return invalid;
                pad++;
                continue;
            }
            if (pad > 0 || !(char.IsAsciiLetterOrDigit(ch) || ch == '+' || ch == '/'))
                return invalid;
        }

        var bytes = new byte[b64.Length / 4 * 3];
        if (!Convert.TryFromBase64Chars(b64, bytes, out var written))
            return invalid;

        return Matches(kind.Mime, bytes.AsSpan(0, written))
            ? null
            : $"The {noun}'s image data doesn't match its type.";
    }

    private static bool Matches(string mime, ReadOnlySpan<byte> b) => mime switch
    {
        "image/png" => b.Length >= 8 && b[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
        "image/jpeg" => b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF,
        "image/webp" => b.Length >= 12
                        && b[..4].SequenceEqual("RIFF"u8)
                        && b.Slice(8, 4).SequenceEqual("WEBP"u8),
        _ => false,
    };
}
