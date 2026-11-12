using System.Net;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Services.Team;

/// <summary>Keeps a new flow's first version (db/003), as a save does (<see cref="FirstVersion"/>; a fake in tests).</summary>
public interface IFirstVersion
{
    Task KeepAsync(FlowRow saved, int stepCount, string by, string? byName, string? note);
}

public sealed class FirstVersion(FlowSchema schema, VersionsRepository versions) : IFirstVersion
{
    public async Task KeepAsync(FlowRow saved, int stepCount, string by, string? byName, string? note)
    {
        if (!await schema.VersionsReadyAsync()) return;
        await versions.InsertAsync(new FlowVersionRow(saved.WorkspaceId, saved.Id, saved.Version, saved.Name, saved.Definition, stepCount, note, by, byName, DateTime.UtcNow));
    }
}

/// <summary>The email to someone mentioned in a comment (<see cref="MentionMail"/>; a fake in tests).</summary>
public interface IMentionMail
{
    bool CanSend { get; }
    /// <summary>The flow's comments in the studio.</summary>
    string LinkFor(Guid workspaceId, string flowId);
    Task<bool> SendAsync(string toEmail, string? toName, string authorName, string? authorEmail, string workspaceName, string flowName, string comment, string link);
}

/// <summary>
/// Sends mention emails through Flow's <see cref="EmailService"/> as the comment's author (their name on the shared
/// mailbox, Reply-To them), in the workspace emails' frame. Best effort: false when it didn't send, never throws.
/// The link opens the flow's comments, on <c>App:BaseUrl</c>.
/// </summary>
public sealed class MentionMail(EmailService email, IConfiguration config, ILogger<MentionMail> log) : IMentionMail
{
    public bool CanSend => email.CanSend();

    /// <summary>The flow's comments in the studio.</summary>
    public static string Link(string? baseUrl, Guid workspaceId, string flowId)
    {
        var app = (!string.IsNullOrWhiteSpace(baseUrl) ? baseUrl : "https://flow.vantrasoftware.com").TrimEnd('/');
        return $"{app}/flows/{Uri.EscapeDataString(flowId)}?ws={workspaceId}&panel=comments";
    }

    public string LinkFor(Guid workspaceId, string flowId) => Link(config["App:BaseUrl"], workspaceId, flowId);

    /// <summary>Subject (one line) and HTML. Every name, the comment and the link are HTML-encoded.</summary>
    public static (string Subject, string Html) Compose(string authorName, string workspaceName, string flowName, string comment, string link)
    {
        var who = string.IsNullOrWhiteSpace(authorName) ? "A teammate" : authorName.Trim();
        var subject = OneLine($"{who} mentioned you on {flowName}");
        var text = comment.Length > 600 ? comment[..600] + "…" : comment;
        var quote = WebUtility.HtmlEncode(text).Replace("\n", "<br>");
        var body = $"{E(who)} mentioned you in a comment on <strong>{E(flowName)}</strong> in {E(workspaceName)}:</p>"
                   + $@"<p style=""margin:0 0 18px;padding:12px 14px;border-left:3px solid #12855d;background:#f4f6f5;border-radius:6px;font-size:14.5px;line-height:1.55;color:#18222f;"">{quote}";
        return (subject, WorkspaceMail.Frame("You were mentioned", body, "Open the comments", link,
            "You get at most one email an hour for each flow you're mentioned on.", showLink: false));
    }

    public async Task<bool> SendAsync(string toEmail, string? toName, string authorName, string? authorEmail, string workspaceName, string flowName, string comment, string link)
    {
        if (!email.CanSend()) return false;
        var (subject, html) = Compose(authorName, workspaceName, flowName, comment, link);
        try { return await email.SendAsync(authorEmail ?? "", string.IsNullOrWhiteSpace(authorName) ? "Vantra Flow" : authorName, toEmail, toName ?? toEmail, subject, html); }
        catch (Exception e)
        {
            log.LogWarning(e, "A mention email didn't send");
            return false;
        }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string OneLine(string s) => new(s.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
}
