using System.Globalization;
using System.Net;

namespace VantraFlow.Api.Services.Billing;

/// <summary>The plan's emails to a workspace's owners, in the failure alerts' look. Pure: <see cref="Plans"/> sends them.</summary>
public static class PlanMail
{
    private static string E(string s) => WebUtility.HtmlEncode(s);

    public static string PlansUrl(string appBase, Guid workspaceId) => $"{appBase}/settings?tab=billing&ws={workspaceId:D}";

    private static string Wrap(string lead, string body, string button, string url, string foot) =>
        "<div style=\"font-family:-apple-system,Segoe UI,Inter,Arial,sans-serif;font-size:14px;line-height:1.55;color:#18222f;max-width:560px\">" +
        $"<p style=\"margin:0 0 14px\">{lead}</p>{body}" +
        $"<p style=\"margin:20px 0\"><a href=\"{E(url)}\" style=\"display:inline-block;padding:10px 16px;border-radius:8px;background:#127f5a;color:#fff;text-decoration:none;font-weight:600\">{E(button)}</a></p>" +
        $"<p style=\"margin-top:24px;font-size:12px;color:#8b95a3\">{foot}</p></div>";

    private static string P(string text) => $"<p style=\"margin:0 0 12px;color:#3b4552\">{text}</p>";

    private static string Day(DateTime d) => d.ToString("d MMMM", CultureInfo.InvariantCulture);

    /// <summary>80% or all of the month's runs used.</summary>
    public static (string Subject, string Html) Usage(string kind, string workspaceName, int used, int quota, string tierName, DateTime resetsAt, string appBase, Guid workspaceId)
    {
        var full = kind == PlanNotices.Usage100;
        var subject = full ? $"{workspaceName} has used its runs for the month" : $"{workspaceName} has used 80% of its runs";
        var lead = full
            ? $"<strong>{E(workspaceName)}</strong> has used all {quota:N0} runs on {E(tierName)} this month."
            : $"<strong>{E(workspaceName)}</strong> has used {used:N0} of its {quota:N0} runs on {E(tierName)} this month.";
        var body = full
            ? P($"New runs are skipped until {E(Day(resetsAt))}, when the count starts again. Upgrade to keep your flows running now.")
            : P($"When they're used, new runs are skipped until {E(Day(resetsAt))}.");
        return (Clip(subject), Wrap(lead, body, full ? "Upgrade" : "See plans", PlansUrl(appBase, workspaceId),
            "Sent to the workspace's owners, at most once a month for each."));
    }

    /// <summary>The Pro trial ends soon; what would switch off on Free.</summary>
    public static (string Subject, string Html) TrialEnding(string workspaceName, DateTime endsAt, int wouldPause, string appBase, Guid workspaceId)
    {
        var subject = $"Your Vantra Flow trial ends on {Day(endsAt)}";
        var lead = $"The Pro trial for <strong>{E(workspaceName)}</strong> ends on {E(Day(endsAt))}.";
        var body = wouldPause > 0
            ? P($"Then it moves to Free, and {wouldPause} {(wouldPause == 1 ? "flow" : "flows")} that Free doesn't cover will switch off. Nothing is deleted: choose a plan and switch them back on.")
            : P("Then it moves to Free. Nothing is deleted, and you can choose a plan at any time.");
        return (Clip(subject), Wrap(lead, body, "Choose a plan", PlansUrl(appBase, workspaceId), "Sent to the workspace's owners."));
    }

    /// <summary>Flows were switched off because the plan no longer covers them.</summary>
    public static (string Subject, string Html) Paused(string workspaceName, IReadOnlyList<(string Name, string Reason)> flows, string tierName, string appBase, Guid workspaceId)
    {
        var subject = flows.Count == 1 ? $"{flows[0].Name} was switched off" : $"{flows.Count} flows in {workspaceName} were switched off";
        var lead = $"<strong>{E(workspaceName)}</strong> is on {E(tierName)}, which doesn't cover {(flows.Count == 1 ? "this flow" : "these flows")}. Nothing was deleted.";
        var rows = string.Concat(flows.Take(10).Select(f =>
            $"<tr><td style=\"padding:8px 0;border-top:1px solid #e6e9ee;color:#18222f;font-weight:600\">{E(f.Name)}</td>" +
            $"<td style=\"padding:8px 0 8px 16px;border-top:1px solid #e6e9ee;color:#5b6573\">{E(f.Reason)}</td></tr>"));
        var more = flows.Count > 10 ? $"<p style=\"margin:8px 0 0;color:#5b6573;font-size:13px\">And {flows.Count - 10} more.</p>" : "";
        var body = $"<table style=\"border-collapse:collapse;width:100%;font-size:13px\">{rows}</table>{more}";
        return (Clip(subject), Wrap(lead, body, "Upgrade to switch them on", PlansUrl(appBase, workspaceId), "Sent to the workspace's owners."));
    }

    /// <summary>A subscription payment failed.</summary>
    public static (string Subject, string Html) PaymentFailed(string workspaceName, string tierName, DateTime? nextAttempt, string appBase, Guid workspaceId)
    {
        var subject = $"Payment failed for {workspaceName}";
        var lead = $"We couldn't take the payment for <strong>{E(workspaceName)}</strong> on {E(tierName)}.";
        var body = P(nextAttempt is { } at
            ? $"Stripe tries again on {E(Day(at))}. Update the card in billing to keep the plan."
            : "Update the card in billing to keep the plan.");
        return (Clip(subject), Wrap(lead, body, "Update billing", PlansUrl(appBase, workspaceId), "Sent to the workspace's owners."));
    }

    private static string Clip(string s) => s.Length <= 200 ? s : s[..200];
}
