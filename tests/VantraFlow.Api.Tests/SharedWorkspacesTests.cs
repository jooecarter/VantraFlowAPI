using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Data;
using VantraFlow.Api.Models;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>
/// Shared Vantra workspaces, stage 1: the reader answers the shared record for a linked workspace and otherwise
/// nothing — never an error, and nothing on the wire changes until a link exists.
/// </summary>
public sealed class SharedWorkspacesTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SharedA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private sealed class FakeStore : ISharedWorkspaceStore
    {
        public bool Ready;
        public Exception? ProbeThrows;
        public Exception? LoadThrows;
        public List<SharedWorkspaceRow> Rows = [];
        public int Probes;
        public int Loads;
        public string? LastApp;

        public Task<bool> ProbeAsync()
        {
            Probes++;
            return ProbeThrows is { } e ? Task.FromException<bool>(e) : Task.FromResult(Ready);
        }

        public Task<IReadOnlyList<SharedWorkspaceRow>> LoadAsync(string app, IReadOnlyCollection<Guid> localIds)
        {
            Loads++;
            LastApp = app;
            if (LoadThrows is { } e) return Task.FromException<IReadOnlyList<SharedWorkspaceRow>>(e);
            return Task.FromResult<IReadOnlyList<SharedWorkspaceRow>>(Rows.Where(r => localIds.Contains(r.LocalWorkspaceId)).ToList());
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static SharedWorkspaces Reader(FakeStore store, Clock? clock = null) =>
        new(store, NullLogger<SharedWorkspaces>.Instance, clock ?? new Clock());

    private static SharedWorkspaceRow Row(Guid local, Guid shared, string name = "Example Co") =>
        new() { LocalWorkspaceId = local, Id = shared, Name = name, Color = "#223947", Logo = " ", TimeZone = "Europe/London" };

    [Fact]
    public async Task Before_the_shared_tables_exist_it_answers_nothing_and_never_queries_them()
    {
        var store = new FakeStore { Ready = false, Rows = [Row(A, SharedA)] };
        var map = await Reader(store).ForLocalAsync([A, B]);
        Assert.Empty(map);
        Assert.Equal(1, store.Probes);
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task A_missing_probe_is_remembered_for_a_while_then_asked_again()
    {
        var store = new FakeStore { Ready = false };
        var clock = new Clock();
        var reader = Reader(store, clock);
        await reader.ForLocalAsync([A]);
        await reader.ForLocalAsync([A]);
        Assert.Equal(1, store.Probes);

        clock.Now += SharedWorkspaces.NegativeTtl + TimeSpan.FromSeconds(1);
        store.Ready = true;
        store.Rows = [Row(A, SharedA)];
        var map = await reader.ForLocalAsync([A]);
        Assert.Equal(2, store.Probes);
        Assert.Equal(SharedA, map[A].Id);

        // A yes is kept: no probe on later reads.
        await reader.ForLocalAsync([A]);
        Assert.Equal(2, store.Probes);
    }

    [Fact]
    public async Task An_unreadable_identity_database_answers_nothing_rather_than_failing()
    {
        var store = new FakeStore { ProbeThrows = new InvalidOperationException("login failed") };
        Assert.Empty(await Reader(store).ForLocalAsync([A]));
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task A_failed_read_after_a_yes_answers_nothing_and_probes_again_later()
    {
        // e.g. the tables were dropped to roll back, or SELECT was revoked.
        var store = new FakeStore { Ready = true, LoadThrows = new InvalidOperationException("Invalid object name 'vantra.WorkspaceApps'.") };
        var clock = new Clock();
        var reader = Reader(store, clock);
        Assert.Empty(await reader.ForLocalAsync([A]));
        Assert.Equal(1, store.Probes);

        store.LoadThrows = null;
        store.Ready = false;
        Assert.Empty(await reader.ForLocalAsync([A]));
        Assert.Equal(1, store.Probes); // within the wait: not even probed

        clock.Now += SharedWorkspaces.NegativeTtl + TimeSpan.FromSeconds(1);
        Assert.Empty(await reader.ForLocalAsync([A]));
        Assert.Equal(2, store.Probes);
    }

    [Fact]
    public async Task No_workspaces_asks_nothing()
    {
        var store = new FakeStore { Ready = true };
        Assert.Empty(await Reader(store).ForLocalAsync([]));
        Assert.Empty(await Reader(store).ForLocalAsync([Guid.Empty]));
        Assert.Equal(0, store.Probes);
        Assert.Equal(0, store.Loads);
    }

    [Fact]
    public async Task A_linked_workspace_gets_its_shared_record_and_an_unlinked_one_none()
    {
        var store = new FakeStore { Ready = true, Rows = [Row(A, SharedA)] };
        var reader = Reader(store);
        var map = await reader.ForLocalAsync([A, B, A]);
        Assert.Equal("flow", store.LastApp);
        var s = Assert.Single(map).Value;
        Assert.Equal(SharedA, s.Id);
        Assert.Equal("Example Co", s.Name);
        Assert.Equal("#223947", s.Color);
        Assert.Null(s.Logo); // blank reads as unset
        Assert.Equal("Europe/London", s.TimeZone);
        Assert.Null(await reader.GetAsync(B));
        Assert.Equal(SharedA.ToString(), SharedWorkspaces.Ref(map, A)!.Id);
        Assert.Null(SharedWorkspaces.Ref(map, B));
    }

    [Fact]
    public async Task The_workspace_list_carries_shared_only_for_a_linked_workspace_and_is_unchanged_otherwise()
    {
        var db = new FakeWorkspaces();
        var linked = db.Add("Northwind", ("olga", "owner"));
        var plain = db.Add("Example Co", ("olga", "owner"));
        var store = new FakeStore { Ready = true, Rows = [Row(linked, SharedA)] };
        WorkspacesController Controller(SharedWorkspaces? reader) =>
            new(db, new ConfigurationBuilder().Build(), new FakeAlerts(), new FakeFeatures(), new FakeTeardown(db),
                NullLogger<WorkspacesController>.Instance, reader) { ControllerContext = Http.As("olga") };
        static IReadOnlyList<WorkspaceDto> Dtos(ActionResult<IReadOnlyList<WorkspaceDto>> r) =>
            Assert.IsAssignableFrom<IReadOnlyList<WorkspaceDto>>(Assert.IsType<OkObjectResult>(r.Result).Value);
        var opts = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

        var withLinks = Dtos(await Controller(Reader(store)).List());
        Assert.Equal(SharedA.ToString(), withLinks.Single(d => d.Id == linked).Shared!.Id);
        Assert.Null(withLinks.Single(d => d.Id == plain).Shared);
        Assert.Contains($"\"shared\":{{\"id\":\"{SharedA}\"}}", JsonSerializer.Serialize(withLinks.Single(d => d.Id == linked), opts));

        // No reader, or no shared tables yet: exactly today's answer.
        var before = JsonSerializer.Serialize(Dtos(await Controller(null).List()), opts);
        var notReady = JsonSerializer.Serialize(Dtos(await Controller(Reader(new FakeStore { Ready = false })).List()), opts);
        Assert.DoesNotContain("\"shared\"", before);
        Assert.Equal(before, notReady);
    }

    [Fact]
    public void The_reader_sql_parses_and_the_probe_reads_tables_and_indexes_never_a_definition()
    {
        foreach (var sql in new[] { SqlSharedWorkspaceStore.ProbeSql, SqlSharedWorkspaceStore.LoadSql.Replace("IN @Ids", "IN (@Ids1, @Ids2)") })
        {
            foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
            {
                parser.Parse(new StringReader(sql), out var errors);
                Assert.Empty(errors);
            }
        }
        Assert.Contains("UX_WorkspaceApps_Local", SqlSharedWorkspaceStore.ProbeSql);
        Assert.DoesNotContain("check_constraints", SqlSharedWorkspaceStore.ProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("definition", SqlSharedWorkspaceStore.ProbeSql, StringComparison.OrdinalIgnoreCase);
        // Read only, and only links that are switched on.
        Assert.StartsWith("SELECT", SqlSharedWorkspaceStore.LoadSql.Trim());
        Assert.Contains("a.Enabled = 1", SqlSharedWorkspaceStore.LoadSql);
    }
}
