using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Team;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Duplicating, exporting and importing flows (<see cref="FlowPorter"/>). Members of a workspace may do each; a copy
/// into another workspace needs the caller to be a member there too. New flows always start switched off.
/// <code>
/// POST api/w/{ws}/flows/{id}/duplicate   { workspaceId?, name? } → 201 { flow: { id, name, workspaceId }, unmapped }
/// GET  api/w/{ws}/flows/{id}/export      → the file (no secrets, sealed values or tokens; names only)
/// POST api/w/{ws}/import/preview         { document } → what it is, what it needs and how that maps here, or its errors
/// POST api/w/{ws}/import                 { document, name?, folderId?, secrets: { name: secretId | null } } → 201, or 422 { errors }
/// </code>
/// </summary>
public sealed class PortController(IWorkspaceStore workspaces, IFlowStore flows, ITeamStore team, IFirstVersion versions, IConfiguration config)
    : FlowControllerBase(workspaces)
{
    private const long BodyLimit = FlowPorter.MaxFileBytes * 2L + 16_384;
    private FlowLimits Limits => FlowLimits.From(config);

    [HttpPost("api/w/{workspaceId:guid}/flows/{flowId}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid workspaceId, string flowId, [FromBody] DuplicateBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanEdit(role)) return NotAllowed();
        if (await flows.GetAsync(workspaceId, flowId) is not { } source) return NoFlow();
        var target = body?.WorkspaceId is { } t && t != Guid.Empty ? t : workspaceId;
        var elsewhere = target != workspaceId;
        if (elsewhere)
        {
            var (targetRole, targetRefusal) = await MemberAsync(target);
            if (targetRefusal is not null) return targetRefusal;
            if (!TeamRules.CanEdit(targetRole)) return NotAllowed();
        }
        var def = FlowDefinition.Parse(source.Definition) ?? new FlowDefinition();
        IReadOnlyList<string> unmapped = [];
        if (elsewhere)
        {
            var targetMembers = (await Workspaces.MembersAsync(target)).Select(m => m.VantraId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            (def, unmapped) = FlowPorter.Retarget(def, await SecretNamesAsync(workspaceId), await SecretIdsAsync(target), await FlowNamesAsync(workspaceId),
                await FlowIdsAsync(target), targetMembers);
        }
        var name = Clip(body?.Name, 160) ?? Clip($"{source.Name} (copy)", 160)!;
        var note = $"Duplicated from “{source.Name}”";
        var created = await CreateAsync(target, name, source.Description, def, source.TemplateId, note);
        if (!elsewhere && await team.ReadyAsync())
        {
            var placed = await team.PlacementAsync(workspaceId, source.Id);
            if (placed?.FolderId is { } folder) await team.SetFolderAsync(workspaceId, created.Id, folder);
            var tags = (await team.FlowTagsAsync(workspaceId)).Where(x => x.FlowId == source.Id).Select(x => x.TagId).ToList();
            if (tags.Count > 0) await team.SetTagsAsync(workspaceId, created.Id, tags);
        }
        var from = elsewhere ? $"{note} in {(await Workspaces.GetAsync(workspaceId))?.Name ?? "another workspace"}" : note;
        await LogAsync(target, ActivityKinds.FlowCreated, created.Id, created.Name, created.Version, from);
        return StatusCode(201, new { flow = new { id = created.Id, name = created.Name, workspaceId = target }, unmapped });
    }

    [HttpGet("api/w/{workspaceId:guid}/flows/{flowId}/export")]
    public async Task<IActionResult> Export(Guid workspaceId, string flowId)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanEdit(role)) return NotAllowed();
        if (await flows.GetAsync(workspaceId, flowId) is not { } f) return NoFlow();
        var def = FlowDefinition.Parse(f.Definition) ?? new FlowDefinition();
        var connections = (await team.ConnectedAppsAsync(workspaceId)).GroupBy(c => c.App).ToDictionary(g => g.Key, g => g.First().Name);
        return Ok(FlowPorter.Export(f.Name, f.Description, def, await SecretNamesAsync(workspaceId), await FlowNamesAsync(workspaceId), connections, DateTime.UtcNow));
    }

    [HttpPost("api/w/{workspaceId:guid}/import/preview")]
    [RequestSizeLimit(BodyLimit)]
    public async Task<IActionResult> Preview(Guid workspaceId, [FromBody] ImportBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanEdit(role)) return NotAllowed();
        var read = FlowPorter.Read(body?.Document, Size(body?.Document), Limits);
        if (read.Definition is null) return UnprocessableEntity(new { error = "That file can't be imported.", errors = read.Errors });
        var from = FlowPorter.ConnectionNames(body!.Document);
        var here = (await team.ConnectedAppsAsync(workspaceId)).GroupBy(c => c.App).ToDictionary(g => g.Key, g => g.First().Name);
        var secrets = await team.SecretNamesAsync(workspaceId);
        var flowIds = await FlowIdsAsync(workspaceId);
        return Ok(new
        {
            name = read.Name,
            description = read.Description,
            trigger = new { app = read.Definition.Trigger.App, @event = read.Definition.Trigger.Event },
            stepCount = read.Definition.AllSteps().Count(),
            apps = read.Needs.Apps.Select(a => new { app = a.App, from = from.GetValueOrDefault(a.App), connected = here.ContainsKey(a.App), here = here.GetValueOrDefault(a.App) }),
            secrets = read.Needs.Secrets.Select(n => new { name = n, match = secrets.FirstOrDefault(s => s.Name == n)?.Id }),
            flows = read.Needs.Flows.Select(n => new { name = n, match = flowIds.GetValueOrDefault(n) }),
            secretOptions = secrets.Select(s => new { id = s.Id, name = s.Name }),
            warnings = DefinitionRules.Validate(read.Definition, Limits).Select(e => e.Message).Distinct().Take(10),
        });
    }

    [HttpPost("api/w/{workspaceId:guid}/import")]
    [RequestSizeLimit(BodyLimit)]
    public async Task<IActionResult> Import(Guid workspaceId, [FromBody] ImportBody? body)
    {
        var (role, refusal) = await MemberAsync(workspaceId);
        if (refusal is not null) return refusal;
        if (!TeamRules.CanEdit(role)) return NotAllowed();
        var read = FlowPorter.Read(body?.Document, Size(body?.Document), Limits);
        if (read.Definition is null) return UnprocessableEntity(new { error = "That file can't be imported.", errors = read.Errors });
        // Secret names to this workspace's secrets: as chosen, else the one of the same name; a choice must be one of its own.
        var secrets = await team.SecretNamesAsync(workspaceId);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var n in read.Needs.Secrets)
        {
            if (body!.Secrets is { } chosen && chosen.TryGetValue(n, out var id))
            {
                if (id is null) continue;
                if (secrets.FirstOrDefault(s => s.Id == id) is null) return BadRequest(new { error = "Choose one of this workspace's secrets.", code = "no_secret" });
                map[n] = id;
            }
            else if (secrets.FirstOrDefault(s => s.Name == n) is { } same) map[n] = same.Id;
        }
        var (def, unmapped) = FlowPorter.Bind(read.Definition, map, await FlowIdsAsync(workspaceId));
        var name = Clip(body!.Name, 160) ?? read.Name!;
        var created = await CreateAsync(workspaceId, name, read.Description, def, null, "Imported from a file");
        if (body.FolderId is { Length: > 0 } folderId && await team.ReadyAsync() && (await team.FoldersAsync(workspaceId)).Any(f => f.Id == folderId))
            await team.SetFolderAsync(workspaceId, created.Id, folderId);
        await LogAsync(workspaceId, ActivityKinds.FlowCreated, created.Id, created.Name, created.Version, "Imported from a file");
        return StatusCode(201, new { flow = new { id = created.Id, name = created.Name, workspaceId }, unmapped });
    }

    // ── Helpers ──

    /// <summary>A new flow, switched off, its first version kept.</summary>
    private async Task<FlowRow> CreateAsync(Guid workspaceId, string name, string? description, FlowDefinition def, string? templateId, string note)
    {
        var now = DateTime.UtcNow;
        var row = FlowsController.Shape(new FlowRow(FlowSecrets.NewRowId("flw_"), workspaceId, name, description, false, def.ToJson(), 1, "", "", null, null, null,
            templateId, null, Sub, now, Sub, now, null, null), def);
        await flows.InsertAsync(row);
        var saved = await flows.GetAsync(workspaceId, row.Id) ?? row;
        await versions.KeepAsync(saved, def.AllSteps().Count(), Sub, DisplayName, note);
        return saved;
    }

    private async Task<IReadOnlyDictionary<string, string>> SecretNamesAsync(Guid ws) =>
        (await team.SecretNamesAsync(ws)).GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Name);
    private async Task<IReadOnlyDictionary<string, string>> SecretIdsAsync(Guid ws) =>
        (await team.SecretNamesAsync(ws)).GroupBy(s => s.Name).ToDictionary(g => g.Key, g => g.First().Id);
    private async Task<IReadOnlyDictionary<string, string>> FlowNamesAsync(Guid ws) =>
        (await flows.ListAsync(ws)).ToDictionary(f => f.Id, f => f.Name);
    private async Task<IReadOnlyDictionary<string, string>> FlowIdsAsync(Guid ws) =>
        (await flows.ListAsync(ws)).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Id);

    private static int Size(JsonNode? doc) => doc is null ? 0 : Encoding.UTF8.GetByteCount(doc.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
    private static string? Clip(string? s, int max) => s?.Trim() is { Length: > 0 } t ? (t.Length > max ? t[..max] : t) : null;
    private ObjectResult NotAllowed() => StatusCode(403, new { error = "You can't add flows to this workspace.", code = "not_allowed" });
    private NotFoundObjectResult NoFlow() => NotFound(new { error = "That flow doesn't exist any more.", code = "no_flow" });
}
