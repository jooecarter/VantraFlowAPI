using System.Net;
using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Billing;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Services.Developer;

/// <summary>The API and outgoing webhooks are premium (Pro and Business): the plan's say, and the tier that has them.</summary>
public static class DevPlan
{
    public const string Message = "The API and outgoing webhooks are on Pro and up.";

    /// <summary>Allowed under this plan? Plans not in force yet (db/008) allow it, as they allow every premium step.</summary>
    public static bool Allows(EffectivePlan plan) => !plan.Enforced || plan.Limits.Premium;

    public static string UpgradeTo(PlanCatalog catalog) => catalog.Cheapest(l => l.Premium);

    /// <summary>Null when the workspace's plan includes the API, else the tier to upgrade to.</summary>
    public static async Task<string?> RefusedAsync(Plans plans, WorkspaceRow ws, DateTime now)
    {
        var (plan, _) = await plans.ForAsync(ws, now);
        return Allows(plan) ? null : UpgradeTo(plans.Catalog);
    }
}

/// <summary>What happens when something worth a webhook happens (a run finishes, a flow is switched on or off, an approval is asked).</summary>
public interface IDevEvents
{
    Task RunFinishedAsync(RunRow run);
    Task FlowSwitchedAsync(Guid workspaceId, string flowId, bool on);
    Task ApprovalRequestedAsync(ApprovalRow approval);
}

/// <summary>
/// The outbox: an event becomes one queued delivery per endpoint switched on and subscribed to it (db/012), sent by the
/// worker (<see cref="WebhookDispatcher"/>). Before db/012, or with no endpoint listening, nothing is written. Never
/// throws: what happened has happened whatever becomes of its webhook.
/// </summary>
public sealed class DevEvents(DeveloperRepository repo, FlowSchema schema, ILogger<DevEvents> log) : IDevEvents
{
    public Task RunFinishedAsync(RunRow run)
    {
        if (run.Test || run.Status is not (Engine.RunStatuses.Succeeded or Engine.RunStatuses.Failed)) return Task.CompletedTask;
        return EmitAsync(run.WorkspaceId, run.Status == Engine.RunStatuses.Succeeded ? DevEventTypes.RunSucceeded : DevEventTypes.RunFailed,
            () => Task.FromResult<JsonObject?>(new JsonObject { ["run"] = DevJson.Run(run) }));
    }

    public Task FlowSwitchedAsync(Guid workspaceId, string flowId, bool on) =>
        EmitAsync(workspaceId, on ? DevEventTypes.FlowTurnedOn : DevEventTypes.FlowTurnedOff, async () =>
            await repo.FlowAsync(workspaceId, flowId) is { } f ? new JsonObject { ["flow"] = DevJson.Flow(f, Engine.FlowDefinition.Parse(f.Definition)) } : null);

    public Task ApprovalRequestedAsync(ApprovalRow approval) =>
        EmitAsync(approval.WorkspaceId, DevEventTypes.ApprovalRequested,
            () => Task.FromResult<JsonObject?>(new JsonObject { ["approval"] = DevJson.Approval(approval) }));

    private async Task EmitAsync(Guid workspaceId, string type, Func<Task<JsonObject?>> data)
    {
        try
        {
            if (!await schema.DeveloperReadyAsync()) return;
            var to = (await repo.ListeningAsync(workspaceId)).Where(e => DevEventTypes.Parse(e.Events).Contains(type)).Select(e => e.Id).ToList();
            if (to.Count == 0 || await data() is not { } d) return;
            var now = DateTime.UtcNow;
            var id = DevJson.NewEventId();
            await repo.InsertDeliveriesAsync(workspaceId, to, id, type, DevJson.Event(id, type, now, workspaceId, d).ToJsonString(), false, now);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.LogWarning(e, "Couldn't queue the {Type} webhook for workspace {Workspace}", type, workspaceId);
        }
    }
}

/// <summary>
/// Sends the queued deliveries (on Flow's worker, <see cref="Engine.FlowWorker"/>): each attempt signed, retried with
/// backoff (<see cref="WebhookRules.NextDelay"/>), its answer kept for the delivery log. An endpoint whose deliveries
/// keep failing every attempt (or that answers 410) is switched off, and the workspace's owners are emailed. A workspace
/// no longer on a plan with webhooks sends nothing.
/// </summary>
public sealed class WebhookDispatcher(
    DeveloperRepository repo, WebhookSender sender, FlowSecrets secrets, Plans plans, WorkspacesRepository workspaces, EmailService email,
    IConfiguration config, ILogger<WebhookDispatcher> log)
{
    public const int Batch = 20;
    public const int Parallel = 6;
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    public string AppBase => (config["App:BaseUrl"] is { Length: > 0 } u ? u : "https://vantraflow-web.azurewebsites.net").TrimEnd('/');

    /// <summary>One go: the due deliveries, sent a few at a time. Returns how many were tried.</summary>
    public async Task<int> DeliverDueAsync(string me, CancellationToken ct)
    {
        var due = await repo.ClaimDeliveriesAsync(me, DateTime.UtcNow, Batch, Lease);
        if (due.Count == 0) return 0;
        var endpoints = new Dictionary<string, EndpointRow?>();
        var allowed = new Dictionary<Guid, bool>();
        foreach (var d in due)
        {
            if (!endpoints.ContainsKey(d.EndpointId)) endpoints[d.EndpointId] = await repo.EndpointByIdAsync(d.EndpointId);
            if (!allowed.ContainsKey(d.WorkspaceId))
                allowed[d.WorkspaceId] = await workspaces.GetAsync(d.WorkspaceId) is { } ws && await DevPlan.RefusedAsync(plans, ws, DateTime.UtcNow) is null;
        }
        using var gate = new SemaphoreSlim(Parallel);
        await Task.WhenAll(due.Select(async d =>
        {
            await gate.WaitAsync(ct);
            try { await AttemptAsync(d, endpoints[d.EndpointId], allowed[d.WorkspaceId], ct); }
            catch (Exception e) when (e is not OperationCanceledException) { log.LogWarning(e, "Webhook delivery {Delivery} couldn't be saved; it will be tried again", d.Id); }
            finally { gate.Release(); }
        }));
        return due.Count;
    }

    /// <summary>"Send test": a ping (or an example event) to one endpoint, sent now, once. The delivery as logged.</summary>
    public async Task<DeliveryRow> SendTestAsync(EndpointRow e, string type, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var body = DevJson.Sample(type, e.WorkspaceId, e.Id, e.Url, now);
        var d = await repo.InsertSendingAsync(e.WorkspaceId, e.Id, (string)body["id"]!, type, body.ToJsonString(), now, "test");
        var r = await sender.SendAsync(e.Url, secrets.Unprotect(e.SecretCipher, e.WorkspaceId, e.Id), type, d.Id, d.EventId, d.Payload, ct);
        var status = r.Verdict == SendVerdict.Succeeded ? DeliveryStatuses.Succeeded : DeliveryStatuses.Failed;
        var at = DateTime.UtcNow;
        await repo.SaveAttemptAsync(d.Seq, status, 1, null, r.StatusCode, r.Snippet, r.Error, r.DurationMs, at);
        return d with { Status = status, Attempts = 1, NextAttemptAt = null, ResponseStatus = r.StatusCode, ResponseBody = r.Snippet, Error = r.Error,
            DurationMs = r.DurationMs, LastAttemptAt = at, DeliveredAt = status == DeliveryStatuses.Succeeded ? at : null };
    }

    private async Task AttemptAsync(DeliveryRow d, EndpointRow? e, bool allowed, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (e is null || !e.Enabled)
        {
            await repo.SaveAttemptAsync(d.Seq, DeliveryStatuses.Failed, d.Attempts, null, null, null, e is null ? "The endpoint was deleted." : "The endpoint is switched off.", null, now);
            return;
        }
        if (!allowed)
        {
            await repo.SaveAttemptAsync(d.Seq, DeliveryStatuses.Failed, d.Attempts, null, null, null, DevPlan.Message, null, now);
            return;
        }
        var r = await sender.SendAsync(e.Url, secrets.Unprotect(e.SecretCipher, e.WorkspaceId, e.Id), d.EventType, d.Id, d.EventId, d.Payload, ct);
        var attempts = d.Attempts + 1;
        now = DateTime.UtcNow;
        if (r.Verdict == SendVerdict.Succeeded)
        {
            await repo.SaveAttemptAsync(d.Seq, DeliveryStatuses.Succeeded, attempts, null, r.StatusCode, r.Snippet, null, r.DurationMs, now);
            if (!d.Test && e.FailuresInARow > 0) await repo.EndpointSucceededAsync(e.Id);
            return;
        }
        var wait = r.Verdict == SendVerdict.Retry ? WebhookRules.NextDelay(attempts, r.RetryAfter, Random.Shared.NextDouble()) : null;
        if (wait is { } w)
        {
            await repo.SaveAttemptAsync(d.Seq, DeliveryStatuses.Pending, attempts, now + w, r.StatusCode, r.Snippet, r.Error, r.DurationMs, now);
            return;
        }
        await repo.SaveAttemptAsync(d.Seq, DeliveryStatuses.Failed, attempts, null, r.StatusCode, r.Snippet, r.Error, r.DurationMs, now);
        if (d.Test) return;
        var gone = r.Verdict == SendVerdict.Gone;
        var inARow = await repo.EndpointFailedAsync(e.Id);
        if (WebhookRules.ShouldDisable(inARow, gone) && await repo.DisableEndpointAsync(e.Id, WebhookRules.DisabledReason(gone), now))
        {
            log.LogInformation("Webhook endpoint {Endpoint} in workspace {Workspace} was switched off after failing", e.Id, e.WorkspaceId);
            await TellOwnersAsync(e, WebhookRules.DisabledReason(gone));
        }
    }

    private async Task TellOwnersAsync(EndpointRow e, string reason)
    {
        try
        {
            if (!email.CanSend()) return;
            var ws = await workspaces.GetAsync(e.WorkspaceId);
            var (subject, html) = DevMail.EndpointOff(ws?.Name ?? "your workspace", Host(e.Url), reason, $"{AppBase}/settings?tab=developers");
            foreach (var o in await repo.OwnersAsync(e.WorkspaceId))
                await email.SendAsync("", "Vantra Flow", o.Email, o.Name ?? "", subject, html);
        }
        catch (Exception x) when (x is not OperationCanceledException) { log.LogWarning(x, "Couldn't email the owners of workspace {Workspace} about a webhook", e.WorkspaceId); }
    }

    public static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "your endpoint";
}

/// <summary>The email owners get when an endpoint is switched off. Every value is HTML-encoded; the URL's path never appears.</summary>
public static class DevMail
{
    public static (string Subject, string Html) EndpointOff(string workspaceName, string host, string reason, string settingsUrl)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var subject = $"A webhook to {host} was switched off";
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
          <p style=""margin:0 0 6px;font-size:13px;color:#8b95a3;"">{E(workspaceName)} · Developers</p>
          <h1 style=""margin:0 0 14px;font-size:20px;line-height:1.35;color:#18222f;"">Webhooks to {E(host)} are paused</h1>
          <p style=""margin:0 0 18px;font-size:14px;line-height:1.55;color:#43505c;"">{E(reason)} Nothing more is sent to it until it's switched back on. Its delivery log shows each answer.</p>
        </td></tr>
        <tr><td style=""padding:0 28px 28px;"">
          <a href=""{E(settingsUrl)}"" style=""display:inline-block;background:#12855d;color:#ffffff;text-decoration:none;font-size:15px;font-weight:600;padding:12px 24px;border-radius:8px;"">Open Developers</a>
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
        return (subject, html);
    }
}

/// <summary>A refusal from the webhook management shared by the studio and the API: its status, code and message.</summary>
public sealed record DevRefusal(int Status, string Code, string Message);

/// <summary>An endpoint as the studio and the API show it. The secret itself only in the answer that made it.</summary>
public sealed record EndpointDto(
    string Id, string Url, string? Description, IReadOnlyList<string> Events, bool Enabled, string? DisabledReason, string? DisabledAt, string? SecretHint,
    string CreatedAt, string? CreatedByName, EndpointStatsDto? Stats, string? Secret = null);

public sealed record EndpointStatsDto(int Total, int Succeeded, int Failed, int Pending, string? LastAt);

public sealed record DeliveryDto(
    string Id, string EventId, string EventType, string Status, bool Test, int Attempts, string? NextAttemptAt, int? ResponseStatus, string? ResponseBody,
    string? Error, int? DurationMs, string CreatedAt, string? LastAttemptAt, string? DeliveredAt, JsonNode? Payload);

public sealed record EndpointBody(string? Url, string? Description, IReadOnlyList<string?>? Events, bool? Enabled);

/// <summary>Outgoing webhooks' management, the same from Settings → Developers and from the API (<c>webhooks:manage</c>).</summary>
public sealed class DevEndpoints(DeveloperRepository repo, FlowSecrets secrets, WebhookDispatcher dispatcher)
{
    public async Task<IReadOnlyList<EndpointDto>> ListAsync(Guid workspaceId)
    {
        var stats = (await repo.EndpointStatsAsync(workspaceId, DateTime.UtcNow.AddDays(-7))).ToDictionary(s => s.EndpointId);
        return (await repo.EndpointsAsync(workspaceId)).Select(e => Dto(e, stats.GetValueOrDefault(e.Id))).ToList();
    }

    public async Task<EndpointDto?> GetAsync(Guid workspaceId, string id) =>
        await repo.EndpointAsync(workspaceId, id) is { } e
            ? Dto(e, (await repo.EndpointStatsAsync(workspaceId, DateTime.UtcNow.AddDays(-7))).FirstOrDefault(s => s.EndpointId == id))
            : null;

    public async Task<(EndpointDto? Endpoint, DevRefusal? Refusal)> CreateAsync(Guid workspaceId, EndpointBody? body, string by, string? byName)
    {
        if (WebhookRules.CheckUrl(body?.Url, out var uri) is { } bad) return (null, new DevRefusal(400, DevCodes.BadRequest, bad));
        var (events, error) = DevEventTypes.Validate(body?.Events);
        if (error is not null) return (null, new DevRefusal(400, DevCodes.BadRequest, error));
        var now = DateTime.UtcNow;
        var id = FlowSecrets.NewRowId("whe_");
        var secret = WebhookSigning.NewSecret();
        var row = new EndpointRow(id, workspaceId, uri!.AbsoluteUri, Desc(body!.Description), DevEventTypes.Format(events!), secrets.Protect(secret, workspaceId, id),
            WebhookSigning.Hint(secret), true, null, null, 0, by, byName, now, now);
        if (!await repo.InsertEndpointAsync(row, WebhookRules.MaxEndpoints))
            return (null, new DevRefusal(409, "endpoint_limit", $"A workspace can have {WebhookRules.MaxEndpoints} webhooks."));
        return (Dto(row, null) with { Secret = secret }, null);
    }

    public async Task<(EndpointDto? Endpoint, DevRefusal? Refusal)> UpdateAsync(Guid workspaceId, string id, EndpointBody? body)
    {
        if (await repo.EndpointAsync(workspaceId, id) is not { } e) return (null, NoEndpoint);
        var url = e.Url;
        if (body?.Url is not null)
        {
            if (WebhookRules.CheckUrl(body.Url, out var uri) is { } bad) return (null, new DevRefusal(400, DevCodes.BadRequest, bad));
            url = uri!.AbsoluteUri;
        }
        var events = e.Events;
        if (body?.Events is not null)
        {
            var (list, error) = DevEventTypes.Validate(body.Events);
            if (error is not null) return (null, new DevRefusal(400, DevCodes.BadRequest, error));
            events = DevEventTypes.Format(list!);
        }
        await repo.UpdateEndpointAsync(workspaceId, id, url, body?.Description is null ? e.Description : Desc(body.Description), events, body?.Enabled ?? e.Enabled, DateTime.UtcNow);
        return (await GetAsync(workspaceId, id), null);
    }

    /// <summary>A new signing secret, at once (the old one stops working).</summary>
    public async Task<(EndpointDto? Endpoint, DevRefusal? Refusal)> RollSecretAsync(Guid workspaceId, string id)
    {
        if (await repo.EndpointAsync(workspaceId, id) is null) return (null, NoEndpoint);
        var secret = WebhookSigning.NewSecret();
        await repo.SetEndpointSecretAsync(workspaceId, id, secrets.Protect(secret, workspaceId, id), WebhookSigning.Hint(secret), DateTime.UtcNow);
        return ((await GetAsync(workspaceId, id))! with { Secret = secret }, null);
    }

    public Task<bool> DeleteAsync(Guid workspaceId, string id) => repo.DeleteEndpointAsync(workspaceId, id);

    public async Task<(DeliveryDto? Delivery, DevRefusal? Refusal)> TestAsync(Guid workspaceId, string id, string? type, CancellationToken ct)
    {
        if (await repo.EndpointAsync(workspaceId, id) is not { } e) return (null, NoEndpoint);
        var t = string.IsNullOrWhiteSpace(type) ? DevEventTypes.Ping : type.Trim();
        if (t != DevEventTypes.Ping && !DevEventTypes.IsKnown(t)) return (null, new DevRefusal(400, DevCodes.BadRequest, $"type must be ping or one of {string.Join(", ", DevEventTypes.All)}."));
        return (Dto(await dispatcher.SendTestAsync(e, t, ct), payload: true), null);
    }

    /// <summary>An endpoint's log: newest first, 50 a page (<paramref name="before"/>: the last delivery's id of the page before).</summary>
    public async Task<(IReadOnlyList<DeliveryDto>? Page, DevRefusal? Refusal)> DeliveriesAsync(Guid workspaceId, string id, string? status, string? before, int limit = 50)
    {
        if (await repo.EndpointAsync(workspaceId, id) is null) return (null, NoEndpoint);
        if (status is not (null or "" or DeliveryStatuses.Pending or DeliveryStatuses.Succeeded or DeliveryStatuses.Failed))
            return (null, new DevRefusal(400, DevCodes.BadRequest, "status must be pending, succeeded or failed."));
        long? seq = null;
        if (!string.IsNullOrEmpty(before))
        {
            if (await repo.DeliveryAsync(workspaceId, before) is not { } b) return (null, new DevRefusal(400, DevCodes.BadRequest, "before must be a delivery id from this log."));
            seq = b.Seq;
        }
        var rows = await repo.DeliveriesAsync(workspaceId, id, string.IsNullOrEmpty(status) ? null : status, seq, Math.Clamp(limit, 1, 100));
        return (rows.Select(d => Dto(d, payload: true)).ToList(), null);
    }

    public async Task<(DeliveryDto? Delivery, DevRefusal? Refusal)> ResendAsync(Guid workspaceId, string deliveryId)
    {
        if (await repo.DeliveryAsync(workspaceId, deliveryId) is not { } d) return (null, new DevRefusal(404, DevCodes.NotFound, "That delivery isn't in the log any more."));
        if (!await repo.RequeueAsync(workspaceId, deliveryId, DateTime.UtcNow)) return (null, new DevRefusal(409, "delivery_busy", "That delivery is being sent right now."));
        return (Dto((await repo.DeliveryAsync(workspaceId, deliveryId))!, payload: true), null);
    }

    public static readonly DevRefusal NoEndpoint = new(404, DevCodes.NotFound, "That webhook doesn't exist any more.");

    public static EndpointDto Dto(EndpointRow e, EndpointStatsRow? s) =>
        new(e.Id, e.Url, e.Description, DevEventTypes.All.Where(DevEventTypes.Parse(e.Events).Contains).ToList(), e.Enabled, e.DisabledReason, DevJson.Iso(e.DisabledAt),
            e.SecretHint, DevJson.Iso(e.CreatedAt), e.CreatedByName,
            new EndpointStatsDto(s?.Total ?? 0, s?.Succeeded ?? 0, s?.Failed ?? 0, s?.Pending ?? 0, DevJson.Iso(s?.LastAt)));

    public static DeliveryDto Dto(DeliveryRow d, bool payload)
    {
        JsonNode? body = null;
        if (payload)
            try { body = JsonNode.Parse(d.Payload); } catch (System.Text.Json.JsonException) { body = null; }
        return new DeliveryDto(d.Id, d.EventId, d.EventType, d.Status, d.Test, d.Attempts, d.Status == DeliveryStatuses.Pending ? DevJson.Iso(d.NextAttemptAt) : null,
            d.ResponseStatus, d.ResponseBody, d.Error, d.DurationMs, DevJson.Iso(d.CreatedAt), DevJson.Iso(d.LastAttemptAt), DevJson.Iso(d.DeliveredAt), body);
    }

    private static string? Desc(string? d) => d?.Trim() is { Length: > 0 } s ? (s.Length > 200 ? s[..200] : s) : null;
}
