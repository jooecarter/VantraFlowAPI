using System.Text.RegularExpressions;
using VantraFlow.Api.Models;

namespace VantraFlow.Api.Services.Team;

/// <summary>
/// Who may do what with a workspace's flows, decided on the server for every request. Flow's roles are owner, admin and
/// member; every member edits flows (the editor's rights), so:
/// <list type="bullet">
/// <item>any member comments, and edits, files, tags, duplicates, exports and imports flows;</item>
/// <item>owners and admins manage folders and tags, change a flow's owner and read the workspace's activity log;</item>
/// <item>a comment is edited only by its author, and deleted by its author or an owner or admin.</item>
/// </list>
/// </summary>
public static class TeamRules
{
    public static bool IsMember(string? role) => FlowRoles.IsValid(role);
    public static bool CanComment(string? role) => IsMember(role);
    public static bool CanEdit(string? role) => IsMember(role);
    public static bool CanManageFolders(string? role) => FlowRoles.CanManage(role);
    public static bool CanManageTags(string? role) => FlowRoles.CanManage(role);
    public static bool CanChangeOwner(string? role) => FlowRoles.CanManage(role);
    public static bool CanAudit(string? role) => FlowRoles.CanManage(role);
    public static bool CanEditComment(string authorId, string caller) => string.Equals(authorId, caller, StringComparison.OrdinalIgnoreCase);
    public static bool CanDeleteComment(string? role, string authorId, string caller) => CanEditComment(authorId, caller) || FlowRoles.CanManage(role);

    public static TeamCanDto Can(string? role) =>
        new(CanEdit(role), CanManageFolders(role), CanManageTags(role), CanChangeOwner(role), CanAudit(role));

    public const int FolderNameMax = 80;
    public const int TagNameMax = 40;
    public const int MaxFolders = 100;
    public const int MaxTags = 200;
    public const int MaxTagsPerFlow = 10;
    public const int CommentMax = 2000;
    public const int MaxMentions = 10;

    /// <summary>The tag colours the studio offers (any other is refused).</summary>
    public static readonly IReadOnlyList<string> Colors = ["green", "blue", "violet", "pink", "red", "orange", "amber", "teal", "slate"];

    /// <summary>A folder's or tag's name, trimmed, inner whitespace collapsed; null when empty or too long.</summary>
    public static string? CleanName(string? name, int max)
    {
        if (name is null) return null;
        var n = Regex.Replace(name, @"\s+", " ").Trim();
        if (n.Length == 0 || n.Length > max || n.Any(char.IsControl)) return null;
        return n;
    }

    public static string? CleanColor(string? color) => color?.Trim().ToLowerInvariant() is { } c && Colors.Contains(c) ? c : null;

    /// <summary>A comment's text: trimmed, at most <see cref="CommentMax"/> characters, line breaks kept, other control characters dropped.</summary>
    public static string? CleanComment(string? body)
    {
        if (body is null) return null;
        var t = new string(body.Replace("\r\n", "\n").Where(ch => ch == '\n' || !char.IsControl(ch)).ToArray()).Trim();
        return t.Length is 0 or > CommentMax ? null : t;
    }

    // ── Mentions ──

    /// <summary>A mention in a comment's text: <c>@[Priya Shah](vantra-id)</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "SYSLIB1045", Justification = "Simple, rarely run.")]
    private static readonly Regex Mention = new(@"@\[([^\]\n]{1,80})\]\(([^)\s]{1,64})\)", RegexOptions.Compiled);

    /// <summary>The Vantra ids a comment mentions, once each, in order, at most <see cref="MaxMentions"/>.</summary>
    public static IReadOnlyList<string> Mentions(string body) =>
        Mention.Matches(body).Select(m => m.Groups[2].Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxMentions).ToList();

    /// <summary>The comment as plain text: each mention as <c>@Name</c>.</summary>
    public static string Plain(string body) => Mention.Replace(body, m => "@" + m.Groups[1].Value);

    /// <summary>Who to email about a comment: mentioned members, not the author, not mentioned before (an edit), with an address.</summary>
    public static IReadOnlyList<MemberRow> ToNotify(IReadOnlyList<string> mentioned, IEnumerable<string> before, string author, IReadOnlyList<MemberRow> members)
    {
        var old = new HashSet<string>(before, StringComparer.OrdinalIgnoreCase);
        return mentioned.Where(id => !old.Contains(id) && !string.Equals(id, author, StringComparison.OrdinalIgnoreCase))
            .Select(id => members.FirstOrDefault(m => string.Equals(m.VantraId, id, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m is { Email.Length: > 0 }).Select(m => m!).ToList();
    }

    /// <summary>At most one mention email per person, per flow, per hour.</summary>
    public static readonly TimeSpan MentionEvery = TimeSpan.FromHours(1);

    public static bool MentionDue(DateTime? lastSent, DateTime now) => lastSent is not { } at || now - at >= MentionEvery;
}
