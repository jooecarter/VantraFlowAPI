using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Notifications;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Your notifications in a workspace (the bell and the Inbox page), and what reaches you (Settings → Notifications). db/016.
/// <code>
/// GET  api/w/{ws}/notifications?before={id}&amp;take=30&amp;unread=true → { available, items, unread, next }
/// GET  api/w/{ws}/notifications/unread                         → { available, unread }
/// POST api/w/{ws}/notifications/{id}/read   { read? = true }   → 204 (read or unread again)
/// POST api/w/{ws}/notifications/read-all                       → { marked }
/// GET  api/me/notification-prefs                               → { available, prefs: { kind: { app, email } } }
/// PUT  api/me/notification-prefs   { kind: { app?, email? } }  → the same, as saved
/// </code>
/// Only yours: every query is by the caller's VantraId and a workspace they're a member of. Before db/016 the reads answer
/// <c>{ available: false }</c> (the studio keeps the bell it had) and the writes 503 <c>not_ready</c>.
/// </summary>
public sealed class NotificationsController(IWorkspaceStore workspaces, INotificationStore store) : FlowControllerBase(workspaces)
{
    [HttpGet("api/w/{workspaceId:guid}/notifications")]
    public async Task<IActionResult> List(Guid workspaceId, [FromQuery] string? before, [FromQuery] int? take, [FromQuery] bool? unread)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return Ok(new { available = false });
        long? cursor = long.TryParse(before, NumberStyles.None, CultureInfo.InvariantCulture, out var b) ? b : null;
        var n = Math.Clamp(take ?? NotificationRules.PageSize, 1, NotificationRules.MaxPage);
        var hidden = await HiddenAsync();
        var rows = await store.ListAsync(workspaceId, Sub, cursor, n + 1, unread == true, hidden);
        var page = rows.Take(n).Select(NotificationRules.Dto).ToList();
        return Ok(new
        {
            available = true, items = page, unread = await store.UnreadAsync(workspaceId, Sub, hidden),
            next = rows.Count > n ? page[^1].Id : null,
        });
    }

    [HttpGet("api/w/{workspaceId:guid}/notifications/unread")]
    public async Task<IActionResult> Unread(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return Ok(new { available = false });
        return Ok(new { available = true, unread = await store.UnreadAsync(workspaceId, Sub, await HiddenAsync()) });
    }

    [HttpPost("api/w/{workspaceId:guid}/notifications/{id}/read")]
    public async Task<IActionResult> Mark(Guid workspaceId, string id, [FromBody] MarkBody? body)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        if (!long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return NotFound(new { error = "That notification has gone." });
        return await store.MarkAsync(workspaceId, Sub, n, body?.Read ?? true, DateTime.UtcNow) ? NoContent() : NotFound(new { error = "That notification has gone." });
    }

    [HttpPost("api/w/{workspaceId:guid}/notifications/read-all")]
    public async Task<IActionResult> MarkAll(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!await store.ReadyAsync()) return NotReady();
        return Ok(new { marked = await store.MarkAllReadAsync(workspaceId, Sub, DateTime.UtcNow) });
    }

    [HttpGet("api/me/notification-prefs")]
    public async Task<IActionResult> Prefs()
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        if (!await store.ReadyAsync()) return Ok(new { available = false, prefs = Wire(NotificationPrefs.Defaults()) });
        return Ok(new { available = true, prefs = Wire(await PrefsOfAsync()) });
    }

    [HttpPut("api/me/notification-prefs")]
    public async Task<IActionResult> SavePrefs([FromBody] JsonObject? body)
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        if (!await store.ReadyAsync()) return NotReady();
        var next = NotificationPrefs.Merge(await PrefsOfAsync(), body);
        await store.SavePrefsAsync(Sub, NotificationPrefs.Serialize(next));
        return Ok(new { available = true, prefs = Wire(next) });
    }

    private async Task<Dictionary<string, NotificationPref>> PrefsOfAsync()
    {
        var raw = await store.PrefsAsync([Sub]);
        return NotificationPrefs.Parse(raw.TryGetValue(Sub, out var j) ? j : null);
    }

    /// <summary>The kinds the caller has switched off in the app: kept (for grouping and email), not shown.</summary>
    private async Task<IReadOnlyCollection<string>> HiddenAsync() =>
        (await PrefsOfAsync()).Where(p => !p.Value.App).Select(p => p.Key).ToList();

    private static Dictionary<string, object> Wire(Dictionary<string, NotificationPref> prefs) =>
        NotificationKinds.All.ToDictionary(k => k, k => (object)new { app = prefs[k].App, email = prefs[k].Email });

    private ObjectResult NotReady() => StatusCode(503, new { error = "Notifications arrive with the next server update.", code = "not_ready" });

    public sealed record MarkBody(bool? Read);
}
