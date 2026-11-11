using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Workspaces;

namespace VantraFlow.Api.Tests;

/// <summary>An in-memory Flow database for the workspace endpoints: workspaces, members, invitations and a log of what happened, in order.</summary>
public sealed class FakeWorkspaces : IWorkspaceStore, IInviteStore
{
    public readonly Dictionary<Guid, WorkspaceRow> Rows = [];
    public readonly Dictionary<Guid, List<MemberRow>> Members = [];
    public readonly Dictionary<Guid, WorkspaceLook> Looks = [];
    public readonly List<(InviteRow Row, byte[] Hash)> Invites = [];
    public readonly List<string> Log = [];
    public int Flows = 3;
    public bool FailDelete;

    public Guid Add(string name, params (string Id, string Role)[] members)
    {
        var id = Guid.NewGuid();
        var owner = members.First(m => m.Role == FlowRoles.Owner).Id;
        Rows[id] = new WorkspaceRow(id, name, "UTC", owner, null, DateTime.UtcNow.AddDays(-30));
        Members[id] = members.Select((m, i) => new MemberRow(id, m.Id, m.Role, $"{m.Id}@example.com", m.Id, DateTime.UtcNow.AddDays(-20 + i))).ToList();
        return id;
    }

    public string? RoleOf(Guid ws, string id) => Members.GetValueOrDefault(ws)?.FirstOrDefault(m => m.VantraId == id)?.Role;

    // ── IWorkspaceStore ──
    public Task<IReadOnlyList<(WorkspaceRow Workspace, string Role)>> ForUserAsync(string vantraId) =>
        Task.FromResult<IReadOnlyList<(WorkspaceRow, string)>>(Members.Where(kv => kv.Value.Any(m => m.VantraId == vantraId))
            .Select(kv => (Rows[kv.Key], kv.Value.First(m => m.VantraId == vantraId).Role)).ToList());
    public Task<WorkspaceRow?> GetAsync(Guid id) => Task.FromResult(Rows.GetValueOrDefault(id));
    public Task<string?> RoleAsync(Guid workspaceId, string vantraId) => Task.FromResult(RoleOf(workspaceId, vantraId));
    public Task<WorkspaceRow> CreateAsync(string name, string timeZone, string ownerVantraId, string? email, string? ownerName, WorkspaceLook? look = null)
    {
        var id = Add(name, (ownerVantraId, FlowRoles.Owner));
        Rows[id] = Rows[id] with { TimeZone = timeZone };
        if (look is not null) Looks[id] = look with { WorkspaceId = id };
        Log.Add($"create:{name}");
        return Task.FromResult(Rows[id]);
    }
    public Task<int> OwnedCountAsync(string vantraId) => Task.FromResult(Rows.Values.Count(r => r.OwnerVantraId == vantraId));
    public Task UpdateAsync(Guid id, string name, string timeZone) { Rows[id] = Rows[id] with { Name = name, TimeZone = timeZone }; return Task.CompletedTask; }
    public Task SetLookAsync(Guid id, bool setColor, string? color, bool setLogo, string? logo)
    {
        var was = Looks.GetValueOrDefault(id) ?? new WorkspaceLook(id, null, null);
        Looks[id] = new WorkspaceLook(id, setColor ? color : was.Color, setLogo ? logo : was.Logo);
        return Task.CompletedTask;
    }
    public Task<IReadOnlyDictionary<Guid, WorkspaceLook>> LooksAsync(IReadOnlyCollection<Guid> ids) =>
        Task.FromResult<IReadOnlyDictionary<Guid, WorkspaceLook>>(Looks.Where(kv => ids.Contains(kv.Key)).ToDictionary());
    public Task<IReadOnlyDictionary<Guid, int>> MemberCountsAsync(IReadOnlyCollection<Guid> ids) =>
        Task.FromResult<IReadOnlyDictionary<Guid, int>>(ids.Where(Members.ContainsKey).ToDictionary(i => i, i => Members[i].Count));
    public Task<IReadOnlyList<MemberRow>> MembersAsync(Guid workspaceId) => Task.FromResult<IReadOnlyList<MemberRow>>(Members.GetValueOrDefault(workspaceId) ?? []);
    public Task<bool> AddMemberAsync(Guid workspaceId, string vantraId, string role, string? email, string? name, string by)
    {
        if (RoleOf(workspaceId, vantraId) is not null) return Task.FromResult(false);
        Members[workspaceId].Add(new MemberRow(workspaceId, vantraId, role, email, name, DateTime.UtcNow));
        Log.Add($"add:{vantraId}:{role}");
        return Task.FromResult(true);
    }
    public Task<MemberPlan?> ApplyAsync(Guid workspaceId, Func<WorkspaceState, MemberPlan> decide)
    {
        if (!Rows.TryGetValue(workspaceId, out var row)) return Task.FromResult<MemberPlan?>(null);
        var list = Members[workspaceId];
        var plan = decide(new WorkspaceState(list.Select(m => new MemberState(m.VantraId, m.Role, m.AddedAt)).ToList(), row.OwnerVantraId));
        if (!plan.Ok) return Task.FromResult<MemberPlan?>(plan);
        foreach (var c in plan.Changes)
        {
            var i = list.FindIndex(m => m.VantraId == c.VantraId);
            list[i] = list[i] with { Role = c.Role };
        }
        list.RemoveAll(m => plan.Removals.Contains(m.VantraId));
        Rows[workspaceId] = row with { OwnerVantraId = plan.PrimaryOwner };
        return Task.FromResult<MemberPlan?>(plan);
    }
    public int AddedToday;
    public Task<int> AddedSinceAsync(Guid workspaceId, DateTime sinceUtc) => Task.FromResult(AddedToday);
    public Task<(int Runs, int Emails)> UsageAsync(Guid workspaceId, DateTime nowUtc) => Task.FromResult((12, 3));
    public Task PauseAllAsync(Guid workspaceId, string reason) { Log.Add("pause"); Flows = 0; return Task.CompletedTask; }
    public Task DeleteAllAsync(Guid workspaceId)
    {
        if (FailDelete) throw new InvalidOperationException("The database went away.");
        Log.Add("delete");
        Rows.Remove(workspaceId);
        Members.Remove(workspaceId);
        Invites.RemoveAll(i => i.Row.WorkspaceId == workspaceId);
        return Task.CompletedTask;
    }

    // ── IInviteStore ──
    public int SentToday;
    public Task<IReadOnlyList<InviteRow>> ListOpenAsync(Guid workspaceId) =>
        Task.FromResult<IReadOnlyList<InviteRow>>(Invites.Where(i => i.Row.WorkspaceId == workspaceId && InviteRules.IsOpen(i.Row.Status)).Select(i => i.Row).ToList());
    public Task<InviteRow?> GetAsync(Guid workspaceId, Guid id) =>
        Task.FromResult(Invites.Select(i => i.Row).FirstOrDefault(r => r.WorkspaceId == workspaceId && r.Id == id));
    public Task<int> CountSentSinceAsync(Guid workspaceId, DateTime sinceUtc) => Task.FromResult(SentToday);
    public Task<InviteRow> IssueAsync(Guid workspaceId, string email, string role, byte[] tokenHash, string by, string? byName)
    {
        Invites.RemoveAll(i => i.Row.WorkspaceId == workspaceId && i.Row.Email == email);
        var row = new InviteRow
        {
            Id = Guid.NewGuid(), WorkspaceId = workspaceId, Email = email, Role = role, Status = InviteRules.Pending, InvitedBy = by, InvitedByName = byName,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(InviteRules.ValidDays),
        };
        Invites.Add((row, tokenHash));
        return Task.FromResult(row);
    }
    public Task<InviteRow?> RenewAsync(Guid workspaceId, Guid id, byte[] tokenHash)
    {
        var i = Invites.FindIndex(x => x.Row.WorkspaceId == workspaceId && x.Row.Id == id && InviteRules.IsOpen(x.Row.Status));
        if (i < 0) return Task.FromResult<InviteRow?>(null);
        Invites[i].Row.ExpiresAt = DateTime.UtcNow.AddDays(InviteRules.ValidDays);
        Invites[i] = (Invites[i].Row, tokenHash);
        return Task.FromResult<InviteRow?>(Invites[i].Row);
    }
    public Task<InviteRow?> MarkSentAsync(Guid id)
    {
        var r = Invites.Select(i => i.Row).FirstOrDefault(x => x.Id == id);
        if (r is not null) { r.Status = InviteRules.Sent; r.SentAt = DateTime.UtcNow; }
        return Task.FromResult(r);
    }
    public Task<bool> RevokeAsync(Guid workspaceId, Guid id)
    {
        var r = Invites.Select(i => i.Row).FirstOrDefault(x => x.WorkspaceId == workspaceId && x.Id == id && InviteRules.IsOpen(x.Status));
        if (r is null) return Task.FromResult(false);
        r.Status = InviteRules.Revoked;
        return Task.FromResult(true);
    }
    public Task<InviteRow?> FindByTokenAsync(byte[] tokenHash)
    {
        var hit = Invites.FirstOrDefault(i => i.Hash.SequenceEqual(tokenHash)).Row;
        if (hit is not null && Rows.TryGetValue(hit.WorkspaceId, out var w)) { hit.WorkspaceName = w.Name; hit.WorkspaceColor = Looks.GetValueOrDefault(w.Id)?.Color; }
        return Task.FromResult(hit);
    }
    public async Task<(InviteOutcome Outcome, InviteRow Invite)?> AcceptAsync(byte[] tokenHash, string vantraId, string? accountEmail, string? name)
    {
        var r = await FindByTokenAsync(tokenHash);
        if (r is null) return null;
        var outcome = InviteRules.Evaluate(r.Status, r.ExpiresAt, DateTime.UtcNow, r.Email, accountEmail, RoleOf(r.WorkspaceId, vantraId) is not null);
        if (outcome == InviteOutcome.Accept)
        {
            Members[r.WorkspaceId].Add(new MemberRow(r.WorkspaceId, vantraId, r.Role, r.Email, name, DateTime.UtcNow));
            r.Status = InviteRules.Accepted;
        }
        return (outcome, r);
    }
}

public sealed class FakeUsers : IUserDirectory
{
    public readonly List<UserDetails> People = [];
    public void Add(string id, string email, string first = "Sam") => People.Add(new UserDetails { VantraID = id, Email = email, FirstName = first, LastName = "Doe" });
    public Task<UserDetails?> FindByEmailAsync(string email) => Task.FromResult(People.FirstOrDefault(p => string.Equals(p.Email, email, StringComparison.OrdinalIgnoreCase)));
    public Task<UserDetails?> FindByVantraIdAsync(string vantraId) => Task.FromResult(People.FirstOrDefault(p => p.VantraID == vantraId));
}

public sealed class FakeMail : IWorkspaceMail
{
    public bool CanSend { get; set; } = true;
    public readonly List<(string Kind, string To, string Link)> Sent = [];
    public string InviteLink(string? origin, string token) => InviteRules.Link(null, "https://flow.example.com", token);
    public string WorkspaceLink(string? origin, Guid workspaceId) => InviteRules.WorkspaceLink(null, "https://flow.example.com", workspaceId);
    public Task<bool> SendInviteAsync(string toEmail, string workspaceName, string inviterName, string? inviterEmail, string role, string link)
    { Sent.Add(("invite", toEmail, link)); return Task.FromResult(CanSend); }
    public Task<bool> SendAddedAsync(string toEmail, string? toName, string workspaceName, string inviterName, string? inviterEmail, string role, string link)
    { Sent.Add(("added", toEmail, link)); return Task.FromResult(CanSend); }
}

public sealed class FakeFeatures(bool invites = true, bool alerts = true) : IWorkspaceFeatures
{
    public bool Invites { get; set; } = invites;
    public Task<bool> AlertsReadyAsync() => Task.FromResult(alerts);
    public Task<bool> InvitesReadyAsync() => Task.FromResult(Invites);
}

public sealed class FakeAlerts : IWorkspaceAlerts
{
    public Task<bool> WorkspaceDefaultAsync(Guid workspaceId) => Task.FromResult(false);
    public Task SetWorkspaceDefaultAsync(Guid workspaceId, bool on) => Task.CompletedTask;
}

public sealed class FakeTeardown(FakeWorkspaces db) : IWorkspaceTeardown
{
    public readonly List<string> Unreachable = [];
    public Task<TeardownReport> DisconnectAllAsync(Guid workspaceId, CancellationToken ct)
    {
        db.Log.Add("disconnect");
        return Task.FromResult(new TeardownReport(2, Unreachable));
    }
}

/// <summary>Every message logged, formatted, to check nothing secret is in them.</summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public readonly ConcurrentQueue<string> Lines = new();
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue(formatter(state, exception) + " " + state);
}

public static class Http
{
    public static ControllerContext As(string? sub, string? email = null)
    {
        var claims = sub is null ? [] : new List<Claim> { new("sub", sub), new("email", email ?? $"{sub}@example.com"), new("given_name", sub) };
        return new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, sub is null ? null : "test")) } };
    }

    public static int Status(IActionResult r) => r switch
    {
        ObjectResult o => o.StatusCode ?? 200,
        StatusCodeResult s => s.StatusCode,
        _ => 200,
    };

    public static int Status<T>(ActionResult<T> r) => r.Result is null ? 200 : Status(r.Result);

    public static string? Code(IActionResult r) =>
        r is ObjectResult { Value: { } v } ? v.GetType().GetProperty("code")?.GetValue(v) as string : null;

    public static string? Code<T>(ActionResult<T> r) => r.Result is null ? null : Code(r.Result);

    public static T Body<T>(IActionResult r) => (T)((ObjectResult)r).Value!;
}
