using System.Globalization;
using System.Net;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// Failure alerts (db/002), once a minute from <see cref="FlowWorker"/>: for each flow with failures since its last
/// alert, and no alert in the last hour, one email — the first failure at once, the rest of the hour's in one digest
/// an hour later — with a link to the newest failed run. Sent through the email service within the workspace's daily
/// cap, to whoever turned the alert on (else the flow's creator), while they're still in the workspace. Never sends
/// before db/002 has run.
/// </summary>
public sealed class FailureAlerts(AlertsRepository alerts, EngineRepository engine, EmailService email, FlowSchema schema, IConfiguration config,
    FlowLimits limits, ILogger<FailureAlerts> log)
{
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow-web.azurewebsites.net").TrimEnd('/');

    public static string RunUrl(string appBase, Guid workspaceId, string runId) =>
        $"{appBase}/runs/{Uri.EscapeDataString(runId)}?ws={workspaceId:D}";

    /// <summary>Sends what's due; how many emails went.</summary>
    public async Task<int> SendDueAsync(DateTime now, CancellationToken ct)
    {
        if (!await schema.AlertsReadyAsync() || !email.CanSend()) return 0;
        var sent = 0;
        foreach (var due in await alerts.DueAsync(now))
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(due.Email)) continue;
            var since = due.AlertedAt is { } last && last > now - AlertsRepository.Lookback ? last : now - AlertsRepository.Lookback;
            if (!await alerts.ClaimAsync(due.WorkspaceId, due.FlowId, now)) continue; // another instance has it, or it went just now
            var (runs, count) = await alerts.FailuresAsync(due.WorkspaceId, due.FlowId, since);
            if (runs.Count == 0) continue;
            if (!await engine.CountEmailAsync(due.WorkspaceId, now, limits.EmailsPerDay))
            {
                log.LogInformation("A failure alert for flow {Flow} wasn't sent: the workspace's emails for today are used", due.FlowId);
                continue;
            }
            var (subject, html) = Compose(due.FlowName, due.WorkspaceName, runs, count, AppBase, due.WorkspaceId);
            if (await email.SendAsync("", "Vantra Flow", due.Email, due.Name ?? "", subject, html)) sent++;
            else log.LogWarning("A failure alert for flow {Flow} couldn't be sent", due.FlowId);
        }
        return sent;
    }

    /// <summary>The email: what failed, how often since the last alert, the newest few with their errors, and a link.</summary>
    public static (string Subject, string Html) Compose(string flowName, string workspaceName, IReadOnlyList<AlertFailure> runs, int count, string appBase, Guid workspaceId)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var subject = count == 1 ? $"{flowName} failed" : $"{flowName} failed {count} times";
        if (subject.Length > 200) subject = subject[..200];
        var rows = string.Concat(runs.Select(r =>
            $"<tr><td style=\"padding:8px 0;border-top:1px solid #e6e9ee;color:#5b6573;white-space:nowrap;vertical-align:top\">{E(r.StartedAt.ToString("d MMM, HH:mm", CultureInfo.InvariantCulture))} UTC</td>" +
            $"<td style=\"padding:8px 0 8px 16px;border-top:1px solid #e6e9ee;color:#18222f\"><a href=\"{E(RunUrl(appBase, workspaceId, r.Id))}\" style=\"color:#18222f;text-decoration:none\">{E(Clip(r.Error ?? "Failed.", 240))}</a></td></tr>"));
        var more = count > runs.Count ? $"<p style=\"margin:8px 0 0;color:#5b6573;font-size:13px\">And {count - runs.Count} more.</p>" : "";
        var lead = count == 1
            ? $"<strong>{E(flowName)}</strong> in {E(workspaceName)} failed."
            : $"<strong>{E(flowName)}</strong> in {E(workspaceName)} failed {count} times in the last hour.";
        var html =
            "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55;color:#18222f;max-width:560px\">" +
            $"<p style=\"margin:0 0 14px\">{lead}</p>" +
            $"<table style=\"border-collapse:collapse;width:100%;font-size:13px\">{rows}</table>{more}" +
            $"<p style=\"margin:20px 0\"><a href=\"{E(RunUrl(appBase, workspaceId, runs[0].Id))}\" style=\"display:inline-block;padding:10px 16px;border-radius:8px;background:#127f5a;color:#fff;text-decoration:none;font-weight:600\">Open the run</a></p>" +
            "<p style=\"margin-top:24px;font-size:12px;color:#8b95a3\">At most one alert per flow an hour. Turn them off in the flow's settings.</p></div>";
        return (subject, html);
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
