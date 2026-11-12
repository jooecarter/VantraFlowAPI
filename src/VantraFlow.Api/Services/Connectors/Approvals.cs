using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// The Approval step's side outside the engine (db/007): the request is kept (flow.Approvals, once per run and step),
/// and each approver still in the workspace is emailed Approve and Reject links through Flow's email sending, within the
/// workspace's daily cap. Never logs a link.
/// </summary>
public sealed class ApprovalGate(StepsRepository repo, FlowSchema schema, WorkspacesRepository workspaces, FlowsRepository flows, EmailService email,
    EngineRepository engine, FlowLimits limits, FlowSecrets secrets, IConfiguration config, ILogger<ApprovalGate> log) : IApprovalGate
{
    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow-web.azurewebsites.net").TrimEnd('/');

    public async Task<ApprovalState> RequestAsync(ApprovalAsk ask, CancellationToken ct)
    {
        if (!await schema.StepsReadyAsync()) throw new ApprovalUnavailableException("Approvals arrive with the next server update.");
        var members = await workspaces.MembersAsync(ask.WorkspaceId);
        var approvers = ask.Approvers.Select(id => members.FirstOrDefault(m => m.VantraId == id)).OfType<MemberRow>().ToList();
        if (approvers.Count == 0) throw new ApprovalUnavailableException("None of the people chosen to approve are in the workspace any more.");
        var flow = await flows.GetAsync(ask.WorkspaceId, ask.FlowId);
        var list = new JsonArray(approvers.Select(a => (JsonNode)new JsonObject { ["id"] = a.VantraId, ["name"] = a.Name, ["email"] = a.Email }).ToArray());
        var row = new ApprovalRow(FlowSecrets.NewRowId("apr_"), ask.WorkspaceId, ask.FlowId, Clip(flow?.Name ?? "A flow", 160), ask.RunId, ask.StepId,
            Clip(ask.Title.Length > 0 ? ask.Title : "Approval needed", 200), ask.Details is null ? null : Clip(ask.Details, 4000), list.ToJsonString(), "pending",
            null, null, null, null, null, DateTime.UtcNow, ask.ExpiresAt);
        var saved = await repo.InsertApprovalAsync(row);
        var notified = 0;
        if (saved.Id == row.Id && email.CanSend())
        {
            var ws = await workspaces.GetAsync(ask.WorkspaceId);
            foreach (var a in approvers)
            {
                if (ct.IsCancellationRequested) break;
                if (string.IsNullOrWhiteSpace(a.Email)) continue;
                if (!await engine.CountEmailAsync(ask.WorkspaceId, DateTime.UtcNow, limits.EmailsPerDay))
                {
                    log.LogInformation("An approval email for run {Run} wasn't sent: the workspace's emails for today are used", ask.RunId);
                    break;
                }
                var approve = ApprovalLinks.Url(AppBase, ApprovalLinks.Make(secrets, saved.Id, a.VantraId, "approved", saved.ExpiresAt));
                var reject = ApprovalLinks.Url(AppBase, ApprovalLinks.Make(secrets, saved.Id, a.VantraId, "rejected", saved.ExpiresAt));
                var (subject, html) = ApprovalMail.Compose(saved.FlowName, ws?.Name ?? "your workspace", saved.Title, saved.Details, approve, reject,
                    $"{AppBase}/approvals?ws={ask.WorkspaceId:D}", saved.ExpiresAt);
                try { if (await email.SendAsync("", "Vantra Flow", a.Email!, a.Name ?? "", subject, html)) notified++; }
                catch (Exception e) { log.LogWarning(e, "An approval email for run {Run} didn't send", ask.RunId); }
            }
        }
        return State(saved, notified);
    }

    public async Task<ApprovalState?> StateAsync(Guid workspaceId, string approvalId, CancellationToken ct) =>
        await repo.ApprovalAsync(approvalId) is { } a && a.WorkspaceId == workspaceId ? State(a) : null;

    public Task ExpireAsync(string approvalId, DateTime now) => repo.ExpireAsync(approvalId, now);

    public static ApprovalState State(ApprovalRow a, int notified = 0) =>
        new(a.Id, a.Status, a.DecidedByName, a.DecidedAt, a.Comment, a.DecidedVia, Approvers(a.Approvers).Select(x => x.Name ?? x.Email ?? "Someone").ToList(),
            a.ExpiresAt, notified);

    /// <summary>A request's approvers, as it keeps them.</summary>
    public static IReadOnlyList<ApproverDto> Approvers(string json)
    {
        try
        {
            return (JsonNode.Parse(json) as JsonArray ?? [])
                .Select(n => new ApproverDto(Tokens.Text(n?["id"]), Tokens.Text(n?["name"]) is { Length: > 0 } nm ? nm : null, Tokens.Text(n?["email"]) is { Length: > 0 } e ? e : null))
                .Where(x => x.Id.Length > 0).ToList();
        }
        catch (JsonException) { return []; }
    }

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max];
}

/// <summary>
/// The links in an approval email: the request, the approver and the decision, sealed (AES-256-GCM, so they can't be
/// read or forged) and expiring with the request. Single-use because a request is decided once: a second click finds it
/// decided. The page they open asks before deciding, so a mail scanner fetching the link decides nothing.
/// </summary>
public static class ApprovalLinks
{
    private const string Purpose = "approval-link";

    private sealed record Payload(string A, string U, string D, long Exp);

    public static string Make(FlowSecrets secrets, string approvalId, string approverId, string decision, DateTime expiresAt) =>
        System.Buffers.Text.Base64Url.EncodeToString(secrets.Protect(JsonSerializer.Serialize(new Payload(approvalId, approverId, decision,
            new DateTimeOffset(DateTime.SpecifyKind(expiresAt, DateTimeKind.Utc)).ToUnixTimeSeconds())), Guid.Empty, Purpose, FlowSecrets.MaxLargeCipherBytes));

    /// <summary>What a link says, or null when it isn't one of ours or it has expired.</summary>
    public static (string ApprovalId, string ApproverId, string Decision)? Read(FlowSecrets secrets, string? token, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2000) return null;
        try
        {
            var raw = System.Buffers.Text.Base64Url.DecodeFromChars(token.Trim());
            if (secrets.Unprotect(raw, Guid.Empty, Purpose) is not { } json || JsonSerializer.Deserialize<Payload>(json) is not { } p) return null;
            if (DateTimeOffset.FromUnixTimeSeconds(p.Exp).UtcDateTime < nowUtc) return null;
            return p.D is "approved" or "rejected" ? (p.A, p.U, p.D) : null;
        }
        catch (Exception e) when (e is FormatException or JsonException) { return null; }
    }

    public static string Url(string appBase, string token) => $"{appBase}/approve/{token}";
}

/// <summary>The approval email: what needs deciding, and the two buttons. Every value is HTML-encoded.</summary>
public static class ApprovalMail
{
    public static (string Subject, string Html) Compose(string flowName, string workspaceName, string title, string? details, string approveUrl, string rejectUrl,
        string inboxUrl, DateTime expiresAt)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var subject = OneLine($"Approval needed: {title}");
        if (subject.Length > 200) subject = subject[..200];
        var more = string.IsNullOrWhiteSpace(details) ? ""
            : $"<p style=\"margin:0 0 18px;padding:12px 14px;background:#f4f6f5;border-radius:8px;font-size:14px;line-height:1.55;color:#43505c;\">{E(details!.Trim()).Replace("\n", "<br>")}</p>";
        var html = $@"<!DOCTYPE html>
<html>
<body style=""margin:0;background:#f4f6f5;font-family:-apple-system,Segoe UI,Inter,Roboto,Helvetica,Arial,sans-serif;"">
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f4f6f5;padding:32px 16px;"">
    <tr><td align=""center"">
      <table role=""presentation"" width=""480"" cellpadding=""0"" cellspacing=""0"" style=""max-width:480px;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #e3e8e5;"">
        <tr><td style=""background:#0f3d2e;padding:20px 28px;"">
          <span style=""color:#ffffff;font-size:18px;font-weight:700;"">Vantra <span style=""font-weight:400;"">Flow</span></span>
        </td></tr>
        <tr><td style=""padding:30px 28px 8px;"">
          <p style=""margin:0 0 6px;font-size:13px;color:#8b95a3;"">{E(flowName)} · {E(workspaceName)}</p>
          <h1 style=""margin:0 0 14px;font-size:20px;line-height:1.35;color:#18222f;"">{E(title)}</h1>
          {more}
        </td></tr>
        <tr><td style=""padding:0 28px 26px;"">
          <a href=""{E(approveUrl)}"" style=""display:inline-block;background:#12855d;color:#ffffff;text-decoration:none;font-size:15px;font-weight:600;padding:12px 24px;border-radius:8px;margin-right:8px;"">Approve</a>
          <a href=""{E(rejectUrl)}"" style=""display:inline-block;background:#ffffff;color:#b42318;text-decoration:none;font-size:15px;font-weight:600;padding:11px 23px;border-radius:8px;border:1px solid #f1c4c0;"">Reject</a>
          <p style=""margin:16px 0 0;font-size:12.5px;line-height:1.5;color:#8b95a3;"">Or decide in <a href=""{E(inboxUrl)}"" style=""color:#12855d;"">your approvals inbox</a>.</p>
        </td></tr>
        <tr><td style=""padding:16px 28px;border-top:1px solid #eef1ef;font-size:12px;color:#9aa3ad;"">The first decision counts. The links work once, until {E(expiresAt.ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.InvariantCulture))} UTC.</td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
        return (subject, html);
    }

    private static string OneLine(string s) => new(s.Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());
}
