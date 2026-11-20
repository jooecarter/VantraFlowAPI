using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Notifications;

/// <summary>What a notification is about. Each has its own in-app and email switch per person (Settings → Notifications).</summary>
public static class NotificationKinds
{
    /// <summary>An Approval step is waiting for you (each approver).</summary>
    public const string Approval = "approval";
    /// <summary>A flow you own failed (its owner): one note per flow per hour, "failed 3 times".</summary>
    public const string FlowFailed = "flow_failed";
    /// <summary>A flow was switched off by Flow (its owner, the workspace's owners and admins).</summary>
    public const string FlowPaused = "flow_paused";
    /// <summary>A connection needs signing in again (who connected it, owners and admins): one a day per connection.</summary>
    public const string Connection = "connection";
    /// <summary>A scheduled flow sent a report (its owner).</summary>
    public const string ReportSent = "report_sent";
    /// <summary>Someone joined or left the workspace (owners and admins).</summary>
    public const string Members = "members";

    public static readonly IReadOnlyList<string> All = [Approval, FlowFailed, FlowPaused, Connection, ReportSent, Members];

    public static bool IsKnown(string? k) => k is not null && All.Contains(k);

    /// <summary>The defaults: everything in the app; email for approvals, failures and pauses.</summary>
    public static NotificationPref Default(string kind) => kind is Approval or FlowFailed or FlowPaused ? new(true, true) : new(true, false);
}

/// <summary>One kind's switches: in the app (the bell and Inbox), and by email.</summary>
public sealed record NotificationPref(bool App, bool Email);

/// <summary>A person's switches, stored as JSON (<c>{"approval":{"app":true,"email":false},…}</c>), over the defaults.</summary>
public static class NotificationPrefs
{
    public static Dictionary<string, NotificationPref> Defaults() => NotificationKinds.All.ToDictionary(k => k, NotificationKinds.Default);

    /// <summary>The stored switches over the defaults; anything unreadable or unknown is the default.</summary>
    public static Dictionary<string, NotificationPref> Parse(string? json)
    {
        var prefs = Defaults();
        if (string.IsNullOrWhiteSpace(json)) return prefs;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject o) return prefs;
            foreach (var kind in NotificationKinds.All)
                if (o[kind] is JsonObject k) prefs[kind] = Apply(prefs[kind], k);
        }
        catch (JsonException) { }
        return prefs;
    }

    /// <summary>The body's switches over <paramref name="current"/>: unknown kinds and missing switches are left as they are.</summary>
    public static Dictionary<string, NotificationPref> Merge(Dictionary<string, NotificationPref> current, JsonObject? body)
    {
        var next = new Dictionary<string, NotificationPref>(current);
        if (body is null) return next;
        foreach (var kind in NotificationKinds.All)
            if (body[kind] is JsonObject k) next[kind] = Apply(next[kind], k);
        return next;
    }

    public static string Serialize(Dictionary<string, NotificationPref> prefs)
    {
        var o = new JsonObject();
        foreach (var kind in NotificationKinds.All)
        {
            var p = prefs.TryGetValue(kind, out var v) ? v : NotificationKinds.Default(kind);
            o[kind] = new JsonObject { ["app"] = p.App, ["email"] = p.Email };
        }
        return o.ToJsonString();
    }

    private static NotificationPref Apply(NotificationPref p, JsonObject k) => new(Bool(k["app"]) ?? p.App, Bool(k["email"]) ?? p.Email);

    private static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
}

/// <summary>
/// A notification to raise. It's written as the bell shows it: <c>{Lead} **{Title}** {Body}</c>. With <see cref="Group"/>,
/// the same kind and <see cref="RefKey"/> for the same person within that time is one note, counted up (its text then
/// <see cref="BodyMany"/>, <c>{n}</c> the count), moved to the top and unread again.
/// </summary>
public sealed record NotificationDraft(
    Guid WorkspaceId, string Kind, string? RefKey, string? Lead, string Title, string? Body, string? Link,
    TimeSpan? Group = null, string? BodyMany = null);

/// <summary>A notification as stored.</summary>
public sealed record NotificationRow(
    long Id, Guid WorkspaceId, string Recipient, string Kind, string? RefKey, string? Lead, string Title, string? Body, string? Link,
    int Times, DateTime GroupedAt, DateTime CreatedAt, DateTime? ReadAt);

/// <summary>A notification on the wire.</summary>
public sealed record NotificationDto(
    string Id, string Kind, string? Ref, string? Lead, string Title, string? Body, string? Link, int Times, string CreatedAt, bool Read);

/// <summary>Someone a notification could go to.</summary>
public sealed record NotifyPerson(string VantraId, string Role, string? Email, string? Name);

/// <summary>What notifications read and write (<see cref="NotificationsRepository"/>; a fake in tests). Only once db/016 has run.</summary>
public interface INotificationStore
{
    Task<bool> ReadyAsync();
    /// <summary>Each person's stored switches (raw JSON), for those who have any.</summary>
    Task<IReadOnlyDictionary<string, string?>> PrefsAsync(IReadOnlyCollection<string> vantraIds);
    Task SavePrefsAsync(string vantraId, string json);
    /// <summary>Adds the note for <paramref name="recipient"/>, or counts up its group. True when it's a new note.</summary>
    Task<bool> AddAsync(NotificationDraft d, string recipient, DateTime now);
    /// <summary>The person's notes, newest first, leaving out the kinds they've switched off in the app.</summary>
    Task<IReadOnlyList<NotificationRow>> ListAsync(Guid workspaceId, string recipient, long? before, int take, bool unreadOnly, IReadOnlyCollection<string> hidden);
    Task<int> UnreadAsync(Guid workspaceId, string recipient, IReadOnlyCollection<string> hidden);
    Task<bool> MarkAsync(Guid workspaceId, string recipient, long id, bool read, DateTime now);
    Task<int> MarkAllReadAsync(Guid workspaceId, string recipient, DateTime now);
    /// <summary>Marks every note of this kind and reference read, for everyone (an approval someone decided).</summary>
    Task ResolveAsync(string kind, string refKey, DateTime now);
    /// <summary>The flow's name and owner (its creator unless changed).</summary>
    Task<(string Name, string Owner)?> FlowOwnerAsync(Guid workspaceId, string flowId);
    Task PruneAsync(DateTime before);
}

/// <summary>Sends the notification emails (the email service, within the workspace's daily cap; a fake in tests).</summary>
public interface INotifyMail
{
    bool CanSend();
    Task<bool> SendAsync(Guid workspaceId, string toEmail, string toName, string subject, string html);
}

public sealed class EmailNotifyMail(EmailService email, EngineRepository engine, FlowLimits limits) : INotifyMail
{
    public bool CanSend() => email.CanSend();

    public async Task<bool> SendAsync(Guid workspaceId, string toEmail, string toName, string subject, string html) =>
        await engine.CountEmailAsync(workspaceId, DateTime.UtcNow, limits.EmailsPerDay)
        && await email.SendAsync("", "Vantra Flow", toEmail, toName, subject, html);
}

/// <summary>The words and links, without a database.</summary>
public static class NotificationRules
{
    public static readonly TimeSpan FailureGroup = TimeSpan.FromHours(1);
    public static readonly TimeSpan PauseGroup = TimeSpan.FromHours(1);
    public static readonly TimeSpan ConnectionGroup = TimeSpan.FromHours(24);
    public static readonly TimeSpan Keep = TimeSpan.FromDays(90);
    public const int PageSize = 30;
    public const int MaxPage = 100;

    public static NotificationDraft Approval(Guid ws, string approvalId, string title, string? details) =>
        new(ws, NotificationKinds.Approval, approvalId, null, Clip(title, 200),
            Clip("is waiting for you" + (FirstLine(details) is { Length: > 0 } d ? $" · {Clip(d, 120)}" : ""), 400), "/approvals");

    public static NotificationDraft Failed(Guid ws, string flowId, string flowName, string runId) =>
        new(ws, NotificationKinds.FlowFailed, flowId, null, Clip(flowName, 200), "failed", $"/runs/{Uri.EscapeDataString(runId)}",
            FailureGroup, "failed {n} times");

    public static NotificationDraft Paused(Guid ws, string flowId, string flowName, string why) =>
        new(ws, NotificationKinds.FlowPaused, flowId, null, Clip(flowName, 200), Clip(why, 400), $"/flows/{Uri.EscapeDataString(flowId)}", PauseGroup, Clip(why, 400));

    /// <summary>Auto-pause's words: "failed 3 times and was paused".</summary>
    public static string PausedAfter(int n) => $"failed {n} times and was paused";

    /// <summary>Other pauses: "was paused: it started too many runs in a minute".</summary>
    public static string PausedBecause(string reason)
    {
        var r = reason.Trim().TrimEnd('.');
        return r.Length == 0 ? "was paused" : $"was paused: {char.ToLowerInvariant(r[0])}{r[1..]}";
    }

    public static NotificationDraft Connection(Guid ws, string connectionId, string appName, int paused) =>
        new(ws, NotificationKinds.Connection, connectionId, "The", Clip(appName, 200),
            paused switch { 0 => "connection needs signing in again", 1 => "connection needs signing in again. 1 flow is paused", _ => $"connection needs signing in again. {paused} flows are paused" },
            "/connections", ConnectionGroup);

    public static NotificationDraft ReportSent(Guid ws, string flowId, string flowName, string runId, int people) =>
        new(ws, NotificationKinds.ReportSent, flowId, null, Clip(flowName, 200), people == 1 ? "sent to 1 person" : $"sent to {people} people",
            $"/runs/{Uri.EscapeDataString(runId)}");

    public static NotificationDraft Member(Guid ws, string vantraId, string name, string workspaceName, string verb) =>
        new(ws, NotificationKinds.Members, vantraId, null, Clip(name, 200), Clip($"{verb} {workspaceName}", 400), "/settings?tab=members");

    /// <summary>A link in the studio as an absolute address in the workspace (emails).</summary>
    public static string Url(string appBase, Guid workspaceId, string? link)
    {
        var path = string.IsNullOrEmpty(link) || !link.StartsWith('/') ? "/inbox" : link;
        return $"{appBase}{path}{(path.Contains('?') ? '&' : '?')}ws={workspaceId:D}";
    }

    /// <summary>The note as one line of text (the email's subject).</summary>
    public static string Text(string? lead, string title, string? body) =>
        string.Join(' ', new[] { lead, title, body }.Where(s => !string.IsNullOrWhiteSpace(s)));

    /// <summary>The email: the note, a button to open it, and where to turn these off.</summary>
    public static (string Subject, string Html) Email(NotificationDraft d, string workspaceName, string appBase)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var subject = Clip(OneLine(Text(d.Lead, d.Title, d.Body)), 200);
        var line = string.Join(' ', new[] { d.Lead is { Length: > 0 } l ? E(l) : null, $"<strong>{E(d.Title)}</strong>", d.Body is { Length: > 0 } b ? E(b) : null }
            .Where(s => s is not null));
        var button = d.Kind switch
        {
            NotificationKinds.Connection => "Reconnect",
            NotificationKinds.FlowFailed or NotificationKinds.ReportSent => "Open the run",
            NotificationKinds.FlowPaused => "Open the flow",
            NotificationKinds.Approval => "Review it",
            _ => "Open Vantra Flow",
        };
        var html =
            "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55;color:#18222f;max-width:560px\">" +
            $"<p style=\"margin:0 0 6px;font-size:13px;color:#8b95a3\">{E(workspaceName)}</p>" +
            $"<p style=\"margin:0 0 14px\">{line}.</p>" +
            $"<p style=\"margin:20px 0\"><a href=\"{E(Url(appBase, d.WorkspaceId, d.Link))}\" style=\"display:inline-block;padding:10px 16px;border-radius:8px;background:#223947;color:#fff;text-decoration:none;font-weight:600\">{E(button)}</a></p>" +
            $"<p style=\"margin-top:24px;font-size:12px;color:#8b95a3\">Choose what reaches you by email in <a href=\"{E(appBase)}/settings?tab=notifications\" style=\"color:#8b95a3\">Settings, Notifications</a>.</p></div>";
        return (subject, html);
    }

    public static NotificationDto Dto(NotificationRow r) =>
        new(r.Id.ToString(CultureInfo.InvariantCulture), r.Kind, r.RefKey, r.Lead, r.Title, r.Body, r.Link, r.Times,
            Controllers.FlowControllerBase.Iso(r.CreatedAt), r.ReadAt is not null);

    private static string? FirstLine(string? s) => s?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
    private static string OneLine(string s) => new(s.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
    public static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}

/// <summary>
/// Raises notifications (db/016): to each person who's still a member of the workspace, in the app when their switch is
/// on, and by email when theirs is (for the kinds whose email this sends; approvals and failures keep their own emails,
/// which ask <see cref="WantsEmailAsync"/>). Best effort: it never throws, so the operation it follows never fails over it.
/// Nothing is raised before db/016 has run.
/// </summary>
public sealed class Notifier(INotificationStore store, IWorkspaceStore workspaces, INotifyMail mail, IConfiguration config, ILogger<Notifier> log)
{
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow-web.azurewebsites.net").TrimEnd('/');

    public async Task<bool> ReadyAsync()
    {
        try { return await store.ReadyAsync(); }
        catch (Exception e) { log.LogInformation(e, "The notifications probe failed"); return false; }
    }

    /// <summary>Does this person want this kind by email? Yes before db/016 (so the emails go as they always have).</summary>
    public async Task<bool> WantsEmailAsync(string vantraId, string kind)
    {
        try
        {
            if (!await store.ReadyAsync()) return true;
            var raw = await store.PrefsAsync([vantraId]);
            return NotificationPrefs.Parse(raw.TryGetValue(vantraId, out var j) ? j : null)[kind].Email;
        }
        catch (Exception e) { log.LogInformation(e, "Couldn't read {Person}'s notification settings", vantraId); return true; }
    }

    /// <summary>
    /// Raises <paramref name="d"/> for <paramref name="recipients"/> (members only, each once, never <paramref name="actor"/>).
    /// With <paramref name="email"/>, a new note is emailed too to those with that switch on. How many it reached.
    /// </summary>
    public async Task<int> RaiseAsync(NotificationDraft d, IEnumerable<string?> recipients, string? actor = null, bool email = false)
    {
        try
        {
            if (!await store.ReadyAsync()) return 0;
            var members = (await workspaces.MembersAsync(d.WorkspaceId)).ToDictionary(m => m.VantraId, StringComparer.OrdinalIgnoreCase);
            var ids = recipients.OfType<string>().Where(r => r.Length > 0 && members.ContainsKey(r) && !string.Equals(r, actor, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return 0;
            var raw = await store.PrefsAsync(ids);
            var now = DateTime.UtcNow;
            string? wsName = null;
            var reached = 0;
            foreach (var id in ids)
            {
                var prefs = NotificationPrefs.Parse(raw.TryGetValue(id, out var j) ? j : null)[d.Kind];
                // Kept even with the app switch off (the list hides that kind), so a group still counts up and is emailed once.
                var fresh = await store.AddAsync(d, members[id].VantraId, now);
                if (prefs.App) reached++;
                if (email && prefs.Email && fresh && members[id].Email is { Length: > 0 } to && mail.CanSend())
                {
                    wsName ??= (await workspaces.GetAsync(d.WorkspaceId))?.Name ?? "Your workspace";
                    var (subject, html) = NotificationRules.Email(d, wsName, AppBase);
                    if (await mail.SendAsync(d.WorkspaceId, to, members[id].Name ?? "", subject, html) && !prefs.App) reached++;
                }
            }
            return reached;
        }
        catch (Exception e)
        {
            log.LogWarning(e, "A {Kind} notification in workspace {Workspace} couldn't be raised", d.Kind, d.WorkspaceId);
            return 0;
        }
    }

    /// <summary>The workspace's owners and admins.</summary>
    public async Task<IReadOnlyList<string>> ManagersAsync(Guid workspaceId)
    {
        try { return (await workspaces.MembersAsync(workspaceId)).Where(m => FlowRoles.CanManage(m.Role)).Select(m => m.VantraId).ToList(); }
        catch (Exception e) { log.LogInformation(e, "Couldn't list workspace {Workspace}'s managers", workspaceId); return []; }
    }

    // ── The events ──

    public async Task ApprovalAsync(Guid workspaceId, string approvalId, string title, string? details, IEnumerable<string> approvers) =>
        await RaiseAsync(NotificationRules.Approval(workspaceId, approvalId, title, details), approvers);

    /// <summary>An approval was decided (or expired): its notes are read for everyone.</summary>
    public async Task ApprovalDoneAsync(string approvalId)
    {
        try { if (await store.ReadyAsync()) await store.ResolveAsync(NotificationKinds.Approval, approvalId, DateTime.UtcNow); }
        catch (Exception e) { log.LogInformation(e, "Couldn't clear approval {Approval}'s notifications", approvalId); }
    }

    /// <summary>A run of a flow failed: its owner's note for the hour counts up. The email is the failure alert's (<see cref="Engine.FailureAlerts"/>).</summary>
    public async Task FlowFailedAsync(Guid workspaceId, string flowId, string runId)
    {
        try
        {
            if (!await store.ReadyAsync() || await store.FlowOwnerAsync(workspaceId, flowId) is not { } f) return;
            await RaiseAsync(NotificationRules.Failed(workspaceId, flowId, f.Name, runId), [f.Owner]);
        }
        catch (Exception e) { log.LogInformation(e, "Couldn't raise flow {Flow}'s failure notification", flowId); }
    }

    /// <summary>Flow switched a flow off: its owner, the owners and admins hear (and, with <paramref name="email"/>, by email).</summary>
    public async Task FlowPausedAsync(Guid workspaceId, string flowId, string why, bool email = true)
    {
        try
        {
            if (!await store.ReadyAsync() || await store.FlowOwnerAsync(workspaceId, flowId) is not { } f) return;
            var to = new List<string> { f.Owner };
            to.AddRange(await ManagersAsync(workspaceId));
            await RaiseAsync(NotificationRules.Paused(workspaceId, flowId, f.Name, why), to, email: email);
        }
        catch (Exception e) { log.LogInformation(e, "Couldn't raise flow {Flow}'s pause notification", flowId); }
    }

    /// <summary>A connection needs signing in again: who connected it, the owners and admins hear.</summary>
    public async Task ConnectionAsync(Guid workspaceId, Guid connectionId, string appName, string? connectedBy, int paused)
    {
        var to = new List<string?> { connectedBy };
        to.AddRange(await ManagersAsync(workspaceId));
        await RaiseAsync(NotificationRules.Connection(workspaceId, connectionId.ToString("N"), appName, paused), to, email: true);
    }

    public async Task ReportSentAsync(Guid workspaceId, string flowId, string runId, int people)
    {
        try
        {
            if (!await store.ReadyAsync() || await store.FlowOwnerAsync(workspaceId, flowId) is not { } f) return;
            await RaiseAsync(NotificationRules.ReportSent(workspaceId, flowId, f.Name, runId, people), [f.Owner], email: true);
        }
        catch (Exception e) { log.LogInformation(e, "Couldn't raise flow {Flow}'s report notification", flowId); }
    }

    /// <summary>Someone joined, left or was removed: the owners and admins hear (not whoever did it, nor the person).</summary>
    public async Task MemberAsync(Guid workspaceId, string vantraId, string? name, string verb, string? actor)
    {
        try
        {
            if (!await store.ReadyAsync()) return;
            var ws = await workspaces.GetAsync(workspaceId);
            var to = (await ManagersAsync(workspaceId)).Where(m => !string.Equals(m, vantraId, StringComparison.OrdinalIgnoreCase));
            await RaiseAsync(NotificationRules.Member(workspaceId, vantraId, string.IsNullOrWhiteSpace(name) ? "Someone" : name!, ws?.Name ?? "the workspace", verb), to, actor, email: true);
        }
        catch (Exception e) { log.LogInformation(e, "Couldn't raise a members notification in {Workspace}", workspaceId); }
    }

    /// <summary>After a scheduled run that made a report and emailed it: how many people it went to, or 0.</summary>
    public static int ReportRecipients(Engine.FlowDefinition def, JsonObject steps)
    {
        if (def.Trigger.App != "schedule") return 0;
        var all = def.AllSteps().ToList();
        if (!all.Any(s => s.App == "orbit" && s.Action is "report.get" or "rows.export")) return 0;
        var people = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in all)
        {
            if (steps[s.Id] is not JsonObject output || output["sent"] is not JsonValue sent || !sent.TryGetValue<int>(out var n) || n <= 0) continue;
            if (output["to"] is JsonArray to) foreach (var t in to) { if (Engine.Tokens.Text(t) is { Length: > 0 } a) people.Add(a); }
            else people.Add($"{s.Id}:{n}");
        }
        return people.Count;
    }
}
