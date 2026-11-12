using System.Text.Json;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services;

/// <summary>How a Vantra Flow mailbox send went: the reason in plain words when it didn't, and whether a retry could help.</summary>
public sealed record EmailSendResult(bool Ok, string? Reason = null, bool Retryable = false)
{
    public static readonly EmailSendResult Sent = new(true);
}

/// <summary>
/// Microsoft's answers to Vantra Flow's own mailbox (app-only Microsoft Graph), as reasons a run can show. Only the
/// error code is read, never echoed: Microsoft's descriptions carry trace and app ids. The sender's address is the
/// one thing named, since the "Send from" picker already shows it.
/// </summary>
public static partial class GraphMailErrors
{
    private const string SignIn = "Vantra Flow's mailbox couldn't sign in to Microsoft 365";

    /// <summary>The token request (<c>/oauth2/v2.0/token</c>) failed with <paramref name="status"/> and <paramref name="body"/>.</summary>
    public static EmailSendResult Token(int status, string? body)
    {
        if (status == 429 || status >= 500) return new(false, $"{SignIn} just now. Trying again shortly.", true);
        var code = AadCode(body);
        var why = code switch
        {
            7000215 => "the client secret isn't right",
            7000222 => "the client secret has expired",
            700016 => "the app registration wasn't found in that tenant",
            90002 or 900023 or 90013 => "the tenant wasn't found",
            7000218 => "the client secret is missing",
            700024 or 7000112 => "the app registration is disabled",
            _ => null,
        };
        return new(false, why is null
            ? $"{SignIn}{(code is { } c ? $" (AADSTS{c})" : "")}. An admin should check its Microsoft settings."
            : $"{SignIn}: {why}. An admin should check its Microsoft settings.");
    }

    /// <summary>Graph's <c>sendMail</c> answered <paramref name="status"/> with <paramref name="body"/>.</summary>
    public static EmailSendResult Send(int status, string? body, string sender)
    {
        var code = GraphCode(body);
        if (status == 429 || status >= 500)
            return new(false, $"Microsoft 365 couldn't send it just now{(code is null ? "" : $" ({code})")}. Trying again shortly.", true);
        return code switch
        {
            "ErrorInvalidUser" or "ResourceNotFound" or "MailboxNotEnabledForRESTAPI" or "MailboxNotFound" or "ErrorNonExistentMailbox" =>
                new(false, $"The mailbox {sender} wasn't found in Microsoft 365. It needs to be a real (or shared) mailbox."),
            "ErrorAccessDenied" or "AccessDenied" or "Authorization_RequestDenied" or "ErrorSendAsDenied" =>
                new(false, $"The app isn't allowed to send from {sender}. It needs the Mail.Send application permission with admin consent, and any application access policy must include this mailbox."),
            "InvalidAuthenticationToken" or "Unauthorized" =>
                new(false, "Microsoft 365 didn't accept the app's sign-in. It needs the Mail.Send application permission with admin consent."),
            "ErrorQuotaExceeded" or "ErrorMessageSizeExceeded" =>
                new(false, $"Microsoft 365 refused it: {sender} is over a sending limit."),
            _ when status == 404 => new(false, $"The mailbox {sender} wasn't found in Microsoft 365. It needs to be a real (or shared) mailbox."),
            _ when status is 401 or 403 => new(false, $"The app isn't allowed to send from {sender} ({code ?? status.ToString()})."),
            _ => new(false, $"Microsoft 365 refused it ({code ?? status.ToString()})."),
        };
    }

    /// <summary>The first AADSTS number in a token error: <c>error_codes</c>, else the start of <c>error_description</c>.</summary>
    public static int? AadCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array
                && codes.GetArrayLength() > 0 && codes[0].TryGetInt32(out var first)) return first;
            if (root.TryGetProperty("error_description", out var d) && d.GetString() is { } text && Aadsts().Match(text) is { Success: true } m)
                return int.Parse(m.Groups[1].Value);
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Graph's <c>error.code</c>, kept only when it looks like a code (letters, digits, dots and underscores).</summary>
    public static string? GraphCode(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object
                && e.TryGetProperty("code", out var c) && c.GetString() is { Length: > 0 and <= 64 } code && CodeShape().IsMatch(code))
                return code;
        }
        catch (JsonException) { }
        return null;
    }

    [GeneratedRegex(@"AADSTS(\d{4,8})")]
    private static partial Regex Aadsts();

    [GeneratedRegex(@"^[A-Za-z0-9_.]+$")]
    private static partial Regex CodeShape();
}
