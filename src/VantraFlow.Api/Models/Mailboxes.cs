namespace VantraFlow.Api.Models;

/// <summary>An SMTP mailbox connected to a workspace (db/013). The password is sealed; a removed one keeps its row, wiped.</summary>
public sealed record MailboxRow(
    string Id, Guid WorkspaceId, string Host, int Port, string Security, string Username, byte[]? PasswordCipher, string FromEmail, string? FromName,
    string Status, string? LastError, string CreatedBy, string? CreatedByName, DateTime CreatedAt, DateTime UpdatedAt, DateTime? LastUsedAt, DateTime? RemovedAt);

/// <summary>
/// An SMTP mailbox as the studio sees it. Everyone sees its address and whether it works; owners and admins also see the
/// server it uses. The password is never in it.
/// </summary>
public sealed record MailboxDto(
    string Id, string FromEmail, string Status, string? FromName = null, string? Host = null, int? Port = null, string? Security = null, string? Username = null,
    string? LastError = null, string? CreatedByName = null, string? CreatedAt = null, string? LastUsedAt = null);

/// <summary>What an SMTP mailbox is connected with (the dialog's fields). The password only travels in.</summary>
public sealed record MailboxBody(string? Host, int? Port, string? Security, string? Username, string? Password, string? FromEmail, string? FromName);

/// <summary>One choice in a "Send from" picker: Vantra Flow, a connected Microsoft 365 or Google account, or an SMTP mailbox.</summary>
/// <param name="Value">what a step or the workspace keeps: <c>vantraflow</c>, <c>microsoft</c>, <c>google</c> or <c>smtp:&lt;id&gt;</c></param>
/// <param name="Status"><c>ok</c>, <c>not_set_up</c> (Vantra Flow without its mail settings) or <c>error</c> (needs reconnecting)</param>
/// <param name="Premium">sending from it needs a plan with premium apps</param>
public sealed record SenderOptionDto(string Value, string Kind, string Label, string? Email, string Status, bool Premium, string? Note = null);

/// <summary>
/// The "Send from" choices for this workspace and its default. <c>Ready</c>: db/013 has run (SMTP mailboxes and the default
/// can be kept). <c>PlanAllows</c>: the plan includes premium apps, so own mailboxes can send.
/// </summary>
public sealed record SendersDto(bool Ready, bool CanManage, string Default, bool PlanAllows, IReadOnlyList<SenderOptionDto> Options);

public sealed record DefaultSenderBody(string? From);
