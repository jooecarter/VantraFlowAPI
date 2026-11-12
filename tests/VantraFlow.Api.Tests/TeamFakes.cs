using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Team;

namespace VantraFlow.Api.Tests;

/// <summary>An in-memory db/011: folders, tags, placements, comments, mention notices and the activity log.</summary>
public sealed class FakeTeam(FakeFlows flows) : ITeamStore
{
    public bool Ready = true;
    public readonly List<FolderRow> Folders = [];
    public readonly List<TagRow> Tags = [];
    public readonly List<(Guid Ws, string FlowId, string TagId)> FlowTags = [];
    public readonly Dictionary<string, (string? Folder, string? Owner)> Places = [];
    public readonly List<CommentRow> Comments = [];
    public readonly Dictionary<(Guid, string, string), DateTime> Notices = [];
    public readonly List<ActivityRow> Activity = [];
    public readonly List<SecretNameRow> Secrets = [];
    public readonly List<ConnectedAppRow> Apps = [];
    public readonly Dictionary<Guid, List<SecretNameRow>> SecretsByWs = [];
    public bool FailActivity;

    public Task<bool> ReadyAsync() => Task.FromResult(Ready);

    public Task<IReadOnlyList<FolderRow>> FoldersAsync(Guid ws) => Task.FromResult<IReadOnlyList<FolderRow>>(Folders.Where(f => f.WorkspaceId == ws).ToList());
    public Task<bool> InsertFolderAsync(FolderRow f)
    {
        if (Folders.Any(x => x.WorkspaceId == f.WorkspaceId && x.Name == f.Name)) return Task.FromResult(false);
        Folders.Add(f);
        return Task.FromResult(true);
    }
    public Task<bool> RenameFolderAsync(Guid ws, string id, string name)
    {
        if (Folders.Any(x => x.WorkspaceId == ws && x.Name == name && x.Id != id)) return Task.FromResult(false);
        var i = Folders.FindIndex(x => x.Id == id);
        Folders[i] = Folders[i] with { Name = name };
        return Task.FromResult(true);
    }
    public Task DeleteFolderAsync(Guid ws, string id)
    {
        foreach (var k in Places.Where(p => p.Value.Folder == id).Select(p => p.Key).ToList()) Places[k] = (null, Places[k].Owner);
        Folders.RemoveAll(f => f.Id == id);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TagRow>> TagsAsync(Guid ws) => Task.FromResult<IReadOnlyList<TagRow>>(Tags.Where(t => t.WorkspaceId == ws).ToList());
    public Task<bool> InsertTagAsync(TagRow t) { Tags.Add(t); return Task.FromResult(true); }
    public Task<bool> UpdateTagAsync(Guid ws, string id, string name, string color)
    {
        var i = Tags.FindIndex(x => x.Id == id);
        Tags[i] = Tags[i] with { Name = name, Color = color };
        return Task.FromResult(true);
    }
    public Task DeleteTagAsync(Guid ws, string id) { Tags.RemoveAll(t => t.Id == id); FlowTags.RemoveAll(t => t.TagId == id); return Task.CompletedTask; }

    private FlowPlacementRow Place(FlowRow f) =>
        new(f.Id, f.Name, Places.GetValueOrDefault(f.Id).Folder, Places.GetValueOrDefault(f.Id).Owner, f.CreatedBy, Comments.Count(c => c.FlowId == f.Id));
    public Task<IReadOnlyList<FlowPlacementRow>> PlacementsAsync(Guid ws) =>
        Task.FromResult<IReadOnlyList<FlowPlacementRow>>(flows.Rows.Where(f => f.WorkspaceId == ws).Select(Place).ToList());
    public Task<FlowPlacementRow?> PlacementAsync(Guid ws, string flowId) =>
        Task.FromResult(flows.Rows.FirstOrDefault(f => f.WorkspaceId == ws && f.Id == flowId) is { } f ? Place(f) : null);
    public Task<IReadOnlyList<FlowTagRow>> FlowTagsAsync(Guid ws) =>
        Task.FromResult<IReadOnlyList<FlowTagRow>>(FlowTags.Where(t => t.Ws == ws).Select(t => new FlowTagRow(t.FlowId, t.TagId)).ToList());
    public Task SetFolderAsync(Guid ws, string flowId, string? folderId) { Places[flowId] = (folderId, Places.GetValueOrDefault(flowId).Owner); return Task.CompletedTask; }
    public Task SetOwnerAsync(Guid ws, string flowId, string ownerId) { Places[flowId] = (Places.GetValueOrDefault(flowId).Folder, ownerId); return Task.CompletedTask; }
    public Task SetTagsAsync(Guid ws, string flowId, IReadOnlyList<string> tagIds)
    {
        FlowTags.RemoveAll(t => t.Ws == ws && t.FlowId == flowId);
        FlowTags.AddRange(tagIds.Select(t => (ws, flowId, t)));
        return Task.CompletedTask;
    }
    public Task ForgetFlowAsync(Guid ws, string flowId) { FlowTags.RemoveAll(t => t.FlowId == flowId); Comments.RemoveAll(c => c.FlowId == flowId); return Task.CompletedTask; }

    public Task<IReadOnlyList<CommentRow>> CommentsAsync(Guid ws, string flowId) =>
        Task.FromResult<IReadOnlyList<CommentRow>>(Comments.Where(c => c.WorkspaceId == ws && c.FlowId == flowId).ToList());
    public Task<CommentRow?> CommentAsync(Guid ws, string id) => Task.FromResult(Comments.FirstOrDefault(c => c.WorkspaceId == ws && c.Id == id));
    public Task InsertCommentAsync(CommentRow c) { Comments.Add(c); return Task.CompletedTask; }
    public Task UpdateCommentAsync(Guid ws, string id, string body, DateTime at)
    {
        var i = Comments.FindIndex(c => c.Id == id);
        Comments[i] = Comments[i] with { Body = body, EditedAt = at };
        return Task.CompletedTask;
    }
    public Task DeleteCommentAsync(Guid ws, string id) { Comments.RemoveAll(c => c.Id == id); return Task.CompletedTask; }
    public Task<bool> ClaimMentionAsync(Guid ws, string flowId, string vantraId, DateTime now)
    {
        var key = (ws, flowId, vantraId);
        if (!TeamRules.MentionDue(Notices.TryGetValue(key, out var at) ? at : null, now)) return Task.FromResult(false);
        Notices[key] = now;
        return Task.FromResult(true);
    }

    public Task InsertActivityAsync(ActivityRow row)
    {
        if (FailActivity) throw new InvalidOperationException("The database went away.");
        Activity.Add(row with { Id = Activity.Count + 1 });
        return Task.CompletedTask;
    }
    public Task<IReadOnlyList<ActivityRow>> ActivityAsync(Guid ws, ActivityQuery q) =>
        Task.FromResult<IReadOnlyList<ActivityRow>>(Activity.Where(a => a.WorkspaceId == ws && (q.ActorId is null || a.ActorId == q.ActorId)
                && (q.FlowId is null || a.FlowId == q.FlowId) && (q.KindPrefix is null || a.Kind.StartsWith(q.KindPrefix, StringComparison.Ordinal))
                && (q.Before is null || a.Id < q.Before))
            .OrderByDescending(a => a.Id).Take(q.Limit).ToList());

    public Task<IReadOnlyList<SecretNameRow>> SecretNamesAsync(Guid ws) => Task.FromResult<IReadOnlyList<SecretNameRow>>(SecretsByWs.GetValueOrDefault(ws) ?? Secrets);
    public Task<IReadOnlyList<ConnectedAppRow>> ConnectedAppsAsync(Guid ws) => Task.FromResult<IReadOnlyList<ConnectedAppRow>>(Apps);
}

public sealed class FakeFlows : IFlowStore
{
    public readonly List<FlowRow> Rows = [];

    public FlowRow Add(Guid ws, string id, string name, string definitionJson, string createdBy = "alex")
    {
        var row = new FlowRow(id, ws, name, null, false, definitionJson, 3, "schedule", "every", null, null, null, null, null, createdBy, DateTime.UtcNow.AddDays(-3),
            createdBy, DateTime.UtcNow.AddDays(-1), null, null);
        Rows.Add(row);
        return row;
    }

    public Task<IReadOnlyList<FlowRow>> ListAsync(Guid ws) => Task.FromResult<IReadOnlyList<FlowRow>>(Rows.Where(f => f.WorkspaceId == ws).ToList());
    public Task<FlowRow?> GetAsync(Guid ws, string id) => Task.FromResult(Rows.FirstOrDefault(f => f.WorkspaceId == ws && f.Id == id));
    public Task InsertAsync(FlowRow f) { Rows.Add(f with { Enabled = false, Version = 1 }); return Task.CompletedTask; }
}

/// <summary>What would have been written to the activity log.</summary>
public sealed class FakeActivity : IActivityLog
{
    public readonly List<ActivityEntry> Lines = [];
    public Task WriteAsync(ActivityEntry entry) { Lines.Add(entry); return Task.CompletedTask; }
}

public sealed class FakeMentionMail : IMentionMail
{
    public bool CanSend { get; set; } = true;
    public readonly List<(string To, string Comment)> Sent = [];
    public string LinkFor(Guid workspaceId, string flowId) => MentionMail.Link("https://flow.example.com", workspaceId, flowId);
    public Task<bool> SendAsync(string toEmail, string? toName, string authorName, string? authorEmail, string workspaceName, string flowName, string comment, string link)
    {
        Sent.Add((toEmail, comment));
        return Task.FromResult(true);
    }
}

public sealed class FakeFirstVersion : IFirstVersion
{
    public readonly List<(string FlowId, string? Note)> Kept = [];
    public Task KeepAsync(FlowRow saved, int stepCount, string by, string? byName, string? note) { Kept.Add((saved.Id, note)); return Task.CompletedTask; }
}

public static class TeamHttp
{
    /// <summary>A caller, with the activity log the controllers write to.</summary>
    public static ControllerContext As(string? sub, IActivityLog log)
    {
        var ctx = Http.As(sub);
        ctx.HttpContext.RequestServices = new ServiceCollection().AddSingleton(log).BuildServiceProvider();
        return ctx;
    }
}
