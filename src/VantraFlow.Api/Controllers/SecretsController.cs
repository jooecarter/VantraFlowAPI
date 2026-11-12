using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// A workspace's secrets for the HTTP request step: a token, a password or an API key, kept sealed (AES-256-GCM, bound to
/// the workspace and the secret) and never shown again; a step names one by id (db/007; 503 <c>not_ready</c> before).
/// <code>
/// GET    api/w/{ws}/secrets        → names, the last four characters, and when each was last used (members)
/// POST   api/w/{ws}/secrets        { name, value } → 201 (owners and admins)
/// PUT    api/w/{ws}/secrets/{id}   { value } → a new value under the same name (owners and admins)
/// DELETE api/w/{ws}/secrets/{id}   → 204: steps using it fail until another is chosen (owners and admins)
/// </code>
/// </summary>
public sealed class SecretsController(WorkspacesRepository workspaces, StepsRepository repo, FlowSchema schema, FlowSecrets secrets, ILogger<SecretsController> log)
    : FlowControllerBase(workspaces)
{
    public const int MaxValue = 8000;
    public const int MaxSecrets = 100;

    public sealed record SecretBody(string? Name, string? Value);

    private async Task<IActionResult?> NotReadyAsync() =>
        await schema.StepsReadyAsync() ? null : StatusCode(503, new { error = "Secrets arrive with the next server update.", code = "not_ready" });

    [HttpGet("api/w/{workspaceId:guid}/secrets")]
    public async Task<IActionResult> List(Guid workspaceId)
    {
        var (_, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        return Ok((await repo.SecretsAsync(workspaceId)).Select(Dto).ToList());
    }

    [HttpPost("api/w/{workspaceId:guid}/secrets")]
    public async Task<IActionResult> Create(Guid workspaceId, [FromBody] SecretBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        var name = body.Name?.Trim() ?? "";
        if (name.Length is 0 or > 80) return BadRequest(new { error = "Name it, in up to 80 characters." });
        if (Check(body.Value) is { } why) return BadRequest(new { error = why });
        if ((await repo.SecretsAsync(workspaceId)).Count >= MaxSecrets) return BadRequest(new { error = $"A workspace can keep {MaxSecrets} secrets." });
        var id = FlowSecrets.NewRowId("sec_");
        var now = DateTime.UtcNow;
        var row = new SecretRow(id, workspaceId, name, secrets.Protect(body.Value!, workspaceId, id, FlowSecrets.MaxLargeCipherBytes), Hint(body.Value!), Sub, DisplayName,
            now, now, null);
        if (!await repo.InsertSecretAsync(row)) return Conflict(new { error = "There's already a secret with that name.", code = "name_taken" });
        log.LogInformation("Workspace {Workspace} saved secret {Secret}", workspaceId, id);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: $"Added the secret {name}");
        return StatusCode(201, Dto(row));
    }

    [HttpPut("api/w/{workspaceId:guid}/secrets/{id}")]
    public async Task<IActionResult> Update(Guid workspaceId, string id, [FromBody] SecretBody body)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        if (Check(body.Value) is { } why) return BadRequest(new { error = why });
        if (await repo.SecretAsync(workspaceId, id) is not { } row) return NotFound(new { error = "That secret isn't there any more." });
        var now = DateTime.UtcNow;
        await repo.UpdateSecretAsync(workspaceId, id, secrets.Protect(body.Value!, workspaceId, id, FlowSecrets.MaxLargeCipherBytes), Hint(body.Value!), now);
        await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: $"Replaced the secret {row.Name}");
        return Ok(Dto(row with { Hint = Hint(body.Value!), UpdatedAt = now }));
    }

    [HttpDelete("api/w/{workspaceId:guid}/secrets/{id}")]
    public async Task<IActionResult> Delete(Guid workspaceId, string id)
    {
        var (_, refusal) = await ManageAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (await NotReadyAsync() is { } nr) return nr;
        var gone = await repo.SecretAsync(workspaceId, id);
        await repo.DeleteSecretAsync(workspaceId, id);
        if (gone is not null) await LogAsync(workspaceId, Services.Team.ActivityKinds.SettingsChanged, detail: $"Deleted the secret {gone.Name}");
        return NoContent();
    }

    private static string? Check(string? value) =>
        string.IsNullOrEmpty(value) ? "Paste the value."
        : value.Length > MaxValue ? $"A secret can be at most {MaxValue:N0} characters."
        : value.Any(c => c is '\r' or '\n') ? "A secret is one line." : null;

    /// <summary>The last four characters, so people can tell secrets apart (nothing for a short one).</summary>
    public static string? Hint(string value) => value.Length >= 12 ? value[^4..] : null;

    private static SecretDto Dto(SecretRow r) => new(r.Id, r.Name, r.Hint, r.CreatedByName, Iso(r.CreatedAt), Iso(r.UpdatedAt), Iso(r.LastUsedAt));
}
