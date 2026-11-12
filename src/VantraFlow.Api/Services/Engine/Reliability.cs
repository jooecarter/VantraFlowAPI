using System.Globalization;
using System.Net;

namespace VantraFlow.Api.Services.Engine;

/// <summary>A workspace with the daily failure digest on, and the local day its last one went for.</summary>
public sealed record DigestWorkspace(Guid Id, string Name, string TimeZone, DateTime? DigestSentOn);

/// <summary>One failed run for the digest.</summary>
public sealed record DigestRun(string Id, string FlowId, string FlowName, string? Error, DateTime FinishedAt);

/// <summary>A flow in the digest: how often it failed, its commonest errors, and its newest failed run.</summary>
public sealed record DigestFlow(string FlowId, string FlowName, int Failures, IReadOnlyList<(string Error, int Count)> TopErrors, string LatestRunId);

/// <summary>Someone to email: an owner or an admin of the workspace.</summary>
public sealed record MailContact(string Email, string? Name);

/// <summary>A flow's auto-pause setting, and what the email names.</summary>
public sealed record AutoPauseFlow(string FlowId, string FlowName, string WorkspaceName, bool Enabled, int? PauseAfterFailures, DateTime UpdatedAt);

/// <summary>What auto-pause and the digest read and write (<see cref="Data.ReliabilityRepository"/>; a fake in tests). Only once db/010 has run.</summary>
public interface IReliabilityStore
{
    /// <summary>The workspaces with the digest on whose last one went before <paramref name="latestLocalDay"/> (or never).</summary>
    Task<IReadOnlyList<DigestWorkspace>> DigestCandidatesAsync(DateTime latestLocalDay);
    /// <summary>The claim: true for exactly one caller per workspace and local day.</summary>
    Task<bool> ClaimDigestAsync(Guid workspaceId, DateTime localDay);
    Task<IReadOnlyList<DigestRun>> FailedRunsAsync(Guid workspaceId, DateTime since, int take);
    /// <summary>The workspace's owners (and, with <paramref name="admins"/>, its admins) who have an email address.</summary>
    Task<IReadOnlyList<MailContact>> RecipientsAsync(Guid workspaceId, bool admins);
    Task<bool> CountEmailAsync(Guid workspaceId, DateTime now, int cap);
    Task<AutoPauseFlow?> AutoPauseOfAsync(Guid workspaceId, string flowId);
    /// <summary>The flow's newest finished runs (succeeded or failed, tests left out) started since <paramref name="since"/>, newest first.</summary>
    Task<IReadOnlyList<string>> LastStatusesAsync(Guid workspaceId, string flowId, DateTime since, int take);
    /// <summary>Switches the flow off with the reason; true when it was on (so one caller sends the email).</summary>
    Task<bool> PauseAsync(Guid workspaceId, string flowId, string reason);
}

/// <summary>Sends the reliability emails (the email service; a fake in tests).</summary>
public interface IReliabilityMail
{
    bool CanSend();
    Task<bool> SendAsync(string toEmail, string toName, string subject, string html);
}

public sealed class EmailReliabilityMail(EmailService email) : IReliabilityMail
{
    public bool CanSend() => email.CanSend();
    public Task<bool> SendAsync(string toEmail, string toName, string subject, string html) => email.SendAsync("", "Vantra Flow", toEmail, toName, subject, html);
}

/// <summary>
/// The rules of the daily failure digest and auto-pause, without a database.
/// <para><b>Digest</b>: once a day per workspace, from <see cref="SendHour"/> in its time zone, the flows that failed in
/// the last 24 hours, most failures first, each with its commonest errors and a link to its failed runs. Nothing goes
/// on a day with no failures.</para>
/// <para><b>Auto-pause</b>: a flow set to pause after N failures is switched off when its last N finished runs since
/// it was last saved or switched on all failed.</para>
/// </summary>
public static class ReliabilityRules
{
    /// <summary>The local hour the digest goes from (08:00).</summary>
    public const int SendHour = 8;
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);
    public const int MaxFlows = 10;
    public const int TopErrors = 3;
    public static readonly int[] PauseChoices = [3, 5, 10, 20];
    public const int MinPauseAfter = 2;
    public const int MaxPauseAfter = 50;

    /// <summary>The local day a digest is due for at <paramref name="nowUtc"/>, or null (before 08:00 there, or already sent today).</summary>
    public static DateTime? DueDay(string? timeZone, DateTime nowUtc, DateTime? sentOn)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), ScheduleRules.Zone(timeZone));
        if (local.Hour < SendHour) return null;
        var day = local.Date;
        return sentOn is { } last && last.Date >= day ? null : DateTime.SpecifyKind(day, DateTimeKind.Unspecified);
    }

    /// <summary>The latest local day anywhere at <paramref name="nowUtc"/> (UTC+14), to narrow the candidates in SQL.</summary>
    public static DateTime LatestLocalDay(DateTime nowUtc) => nowUtc.AddHours(14).Date;

    /// <summary>The failed runs, by flow: most failures first, each flow's commonest errors (the step's part only).</summary>
    public static IReadOnlyList<DigestFlow> Summarise(IEnumerable<DigestRun> runs) =>
        runs.GroupBy(r => r.FlowId)
            .Select(g =>
            {
                var newest = g.OrderByDescending(r => r.FinishedAt).First();
                var errors = g.GroupBy(r => Clip(string.IsNullOrWhiteSpace(r.Error) ? "Failed." : r.Error!.Trim(), 200))
                    .Select(e => (Error: e.Key, Count: e.Count()))
                    .OrderByDescending(e => e.Count).ThenBy(e => e.Error, StringComparer.Ordinal)
                    .Take(TopErrors).ToList();
                return new DigestFlow(g.Key, newest.FlowName, g.Count(), errors, newest.Id);
            })
            .OrderByDescending(f => f.Failures).ThenBy(f => f.FlowName, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static string RunsUrl(string appBase, Guid workspaceId, string flowId) =>
        $"{appBase}/runs?flow={Uri.EscapeDataString(flowId)}&status=failed&ws={workspaceId:D}";

    public static string FlowUrl(string appBase, Guid workspaceId, string flowId) =>
        $"{appBase}/flows/{Uri.EscapeDataString(flowId)}?ws={workspaceId:D}";

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    private static string Wrap(string lead, string body, string button, string url, string foot) =>
        "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55;color:#18222f;max-width:560px\">" +
        $"<p style=\"margin:0 0 14px\">{lead}</p>{body}" +
        $"<p style=\"margin:20px 0\"><a href=\"{E(url)}\" style=\"display:inline-block;padding:10px 16px;border-radius:8px;background:#127f5a;color:#fff;text-decoration:none;font-weight:600\">{E(button)}</a></p>" +
        $"<p style=\"margin-top:24px;font-size:12px;color:#8b95a3\">{foot}</p></div>";

    /// <summary>The digest email: each failing flow, how often, its top errors, and a link to its failed runs.</summary>
    public static (string Subject, string Html) Digest(string workspaceName, IReadOnlyList<DigestFlow> flows, string appBase, Guid workspaceId, DateTime localDay)
    {
        var total = flows.Sum(f => f.Failures);
        var subject = Clip(flows.Count == 1
            ? $"{flows[0].FlowName} failed {(total == 1 ? "once" : $"{total} times")} in the last day"
            : $"{flows.Count} flows in {workspaceName} failed in the last day", 200);
        var lead = $"In the 24 hours to {E(localDay.ToString("d MMMM", CultureInfo.InvariantCulture))}, " +
                   $"{(flows.Count == 1 ? "one flow" : $"{flows.Count} flows")} in <strong>{E(workspaceName)}</strong> failed {total} {(total == 1 ? "time" : "times")}.";
        var rows = string.Concat(flows.Take(MaxFlows).Select(f =>
            "<tr><td style=\"padding:10px 0;border-top:1px solid #e6e9ee;vertical-align:top\">" +
            $"<a href=\"{E(RunsUrl(appBase, workspaceId, f.FlowId))}\" style=\"color:#18222f;font-weight:600;text-decoration:none\">{E(f.FlowName)}</a>" +
            string.Concat(f.TopErrors.Select(e =>
                $"<div style=\"color:#5b6573;font-size:13px;margin-top:3px\">{E(Clip(e.Error, 160))}{(e.Count > 1 ? $" <span style=\"color:#8b95a3\">× {e.Count}</span>" : "")}</div>")) +
            "</td>" +
            $"<td style=\"padding:10px 0 10px 16px;border-top:1px solid #e6e9ee;vertical-align:top;text-align:right;white-space:nowrap;color:#b42318;font-weight:600\">{f.Failures} failed</td></tr>"));
        var more = flows.Count > MaxFlows ? $"<p style=\"margin:8px 0 0;color:#5b6573;font-size:13px\">And {flows.Count - MaxFlows} more.</p>" : "";
        var body = $"<table style=\"border-collapse:collapse;width:100%;font-size:14px\">{rows}</table>{more}";
        return (subject, Wrap(lead, body, "See failed runs", $"{appBase}/runs?status=failed&ws={workspaceId:D}",
            "Sent once a day to the workspace's owners and admins when flows failed. Turn it off in Settings, Workspace."));
    }

    // ── Auto-pause ──

    public static string PauseReason(int n) => $"Paused after {n} failures in a row.";

    /// <summary>A flow's auto-pause setting as stored: 2–50, else off.</summary>
    public static int? PauseAfter(int? n) => n is >= MinPauseAfter and <= MaxPauseAfter ? n : null;

    /// <summary>Whether <paramref name="newestFirst"/> (the flow's latest finished runs) is <paramref name="n"/> failures in a row.</summary>
    public static bool ShouldPause(int n, IReadOnlyList<string> newestFirst) =>
        n >= MinPauseAfter && newestFirst.Count >= n && newestFirst.Take(n).All(s => s == RunStatuses.Failed);

    public static (string Subject, string Html) Paused(string flowName, string workspaceName, int n, string? lastError, string appBase, Guid workspaceId, string flowId)
    {
        var subject = Clip($"{flowName} was paused after {n} failures", 200);
        var lead = $"<strong>{E(flowName)}</strong> in {E(workspaceName)} failed {n} times in a row, so it was switched off. Nothing was deleted.";
        var body = lastError is { Length: > 0 }
            ? $"<p style=\"margin:0 0 12px;color:#3b4552\">The last error: {E(Clip(lastError, 300))}</p>" : "";
        body += "<p style=\"margin:0 0 12px;color:#3b4552\">Fix what's failing, then switch it back on.</p>";
        return (subject, Wrap(lead, body, "Open the flow", FlowUrl(appBase, workspaceId, flowId), "Sent to the workspace's owners. Auto-pause is set in the flow's settings."));
    }
}

/// <summary>
/// The daily failure digest and auto-pause (db/010), run by <see cref="FlowWorker"/>: the digest every few minutes,
/// auto-pause after each failed run. Neither does anything before db/010 has run or without email set up (a flow is
/// still paused then; only the email is skipped).
/// </summary>
public sealed class Reliability(IReliabilityStore store, IReliabilityMail mail, FlowLimits limits, string appBase, ILogger log)
{
    /// <summary>Sends the digests due at <paramref name="now"/>; how many workspaces got one.</summary>
    public async Task<int> SendDigestsAsync(DateTime now, CancellationToken ct)
    {
        if (!mail.CanSend()) return 0;
        var sent = 0;
        foreach (var ws in await store.DigestCandidatesAsync(ReliabilityRules.LatestLocalDay(now)))
        {
            if (ct.IsCancellationRequested) break;
            if (ReliabilityRules.DueDay(ws.TimeZone, now, ws.DigestSentOn) is not { } day) continue;
            if (!await store.ClaimDigestAsync(ws.Id, day)) continue; // another instance has it, or it went already
            var flows = ReliabilityRules.Summarise(await store.FailedRunsAsync(ws.Id, now - ReliabilityRules.Window, 500));
            if (flows.Count == 0) continue;
            var (subject, html) = ReliabilityRules.Digest(ws.Name, flows, appBase, ws.Id, day);
            var any = false;
            foreach (var to in await store.RecipientsAsync(ws.Id, admins: true))
            {
                if (!await store.CountEmailAsync(ws.Id, now, limits.EmailsPerDay))
                {
                    log.LogInformation("The failure digest for workspace {Workspace} wasn't all sent: its emails for today are used", ws.Id);
                    break;
                }
                if (await mail.SendAsync(to.Email, to.Name ?? "", subject, html)) any = true;
            }
            if (any) sent++;
        }
        return sent;
    }

    /// <summary>
    /// After a run of <paramref name="flowId"/> failed: switch the flow off when it's set to after N failures in a row
    /// and its last N have all failed, and tell the owners. True when it was paused now.
    /// </summary>
    public async Task<bool> AfterFailureAsync(Guid workspaceId, string flowId, string? error, DateTime now)
    {
        if (await store.AutoPauseOfAsync(workspaceId, flowId) is not { Enabled: true } flow || ReliabilityRules.PauseAfter(flow.PauseAfterFailures) is not { } n)
            return false;
        // Since it was last saved or switched on: switching it back on starts the count again.
        var last = await store.LastStatusesAsync(workspaceId, flowId, flow.UpdatedAt, n);
        if (!ReliabilityRules.ShouldPause(n, last)) return false;
        if (!await store.PauseAsync(workspaceId, flowId, ReliabilityRules.PauseReason(n))) return false;
        log.LogInformation("Flow {Flow} was paused after {Failures} failures in a row", flowId, n);
        if (!mail.CanSend()) return true;
        var (subject, html) = ReliabilityRules.Paused(flow.FlowName, flow.WorkspaceName, n, error, appBase, workspaceId, flowId);
        foreach (var to in await store.RecipientsAsync(workspaceId, admins: false))
        {
            if (!await store.CountEmailAsync(workspaceId, now, limits.EmailsPerDay)) break;
            await mail.SendAsync(to.Email, to.Name ?? "", subject, html);
        }
        return true;
    }
}
