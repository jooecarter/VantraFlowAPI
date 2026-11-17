using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Shared;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Appearance saved to the Vantra account (db/shared/003): validation, the probe, and the endpoints.</summary>
public sealed class SharedAppearanceTests
{
    private sealed class FakeStore(bool ready, bool throws = false) : ISharedAppearanceStore
    {
        public readonly Dictionary<string, SharedAppearance> Rows = new();
        public int Probes, Reads, Writes;
        public string? LastApp;

        public Task<bool> ProbeAsync()
        {
            Probes++;
            if (throws) throw new InvalidOperationException("no database");
            return Task.FromResult(ready);
        }

        public Task<SharedAppearance?> GetAsync(string vantraId)
        {
            Reads++;
            return Task.FromResult(Rows.TryGetValue(vantraId, out var a) ? a : null);
        }

        public Task<SharedAppearance> SaveAsync(string vantraId, SharedAppearanceBody c, string app)
        {
            Writes++;
            LastApp = app;
            Rows.TryGetValue(vantraId, out var was);
            var now = new SharedAppearance(c.Theme ?? was?.Theme, c.Nav ?? was?.Nav, c.Density ?? was?.Density,
                c.ReduceMotion ?? was?.ReduceMotion, c.Accent ?? was?.Accent, c.AiRibbon ?? was?.AiRibbon, DateTime.UtcNow);
            Rows[vantraId] = now;
            return Task.FromResult(now);
        }
    }

    private static SharedAppearanceService Service(FakeStore store) => new(store, NullLogger<SharedAppearanceService>.Instance);

    private static AppearanceController Controller(FakeStore store, string sub = "user_1")
    {
        var c = new AppearanceController(new FakeWorkspaces(), Service(store));
        var http = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", sub)], "test")) };
        c.ControllerContext = new ControllerContext { HttpContext = http };
        return c;
    }

    [Theory]
    [InlineData("Dark", "slate", "Compact", "purple")]
    [InlineData("light", "match", "comfortable", "teal")]
    [InlineData(" System ", "WHITE", null, null)]
    public void Known_values_are_accepted_in_lower_case(string theme, string nav, string? density, string? accent)
    {
        var (clean, problem) = SharedAppearanceRules.Check(new SharedAppearanceBody(theme, nav, density, true, accent, false));
        Assert.Null(problem);
        Assert.Equal(theme.Trim().ToLowerInvariant(), clean!.Theme);
        Assert.Equal(nav.ToLowerInvariant(), clean.Nav);
        Assert.Equal(density?.ToLowerInvariant(), clean.Density);
        Assert.Equal(accent, clean.Accent);
        Assert.True(clean.ReduceMotion);
        Assert.False(clean.AiRibbon);
    }

    [Theory]
    [InlineData("sepia", null, null, null)]
    [InlineData(null, "green", null, null)]
    [InlineData(null, null, "cosy", null)]
    [InlineData(null, null, null, "#ff0000")]
    [InlineData("", null, null, null)]
    public void Unknown_values_are_refused(string? theme, string? nav, string? density, string? accent) =>
        Assert.NotNull(SharedAppearanceRules.Check(new SharedAppearanceBody(theme, nav, density, null, accent, null)).Problem);

    [Fact]
    public void An_empty_change_is_refused()
    {
        Assert.Equal("Nothing to save.", SharedAppearanceRules.Check(null).Problem);
        Assert.Equal("Nothing to save.", SharedAppearanceRules.Check(new SharedAppearanceBody()).Problem);
    }

    [Fact]
    public void The_endpoints_need_a_signed_in_account()
    {
        var t = typeof(AppearanceController);
        Assert.True(t.IsSubclassOf(typeof(FlowControllerBase)));
        Assert.NotNull(typeof(FlowControllerBase).GetCustomAttribute<AuthorizeAttribute>());
        Assert.Empty(t.GetMethods().Where(m => m.GetCustomAttribute<AllowAnonymousAttribute>() is not null));
        Assert.Equal("api/me/appearance", t.GetMethod("Get")!.GetCustomAttribute<HttpGetAttribute>()!.Template);
        Assert.Equal("api/me/appearance", t.GetMethod("Put")!.GetCustomAttribute<HttpPutAttribute>()!.Template);
    }

    [Fact]
    public async Task Before_the_sql_has_run_get_is_unavailable_and_put_is_not_ready()
    {
        var store = new FakeStore(ready: false);
        var get = Assert.IsType<ObjectResult>(await Controller(store).Get());
        Assert.Equal(200, get.StatusCode);
        var dto = Assert.IsType<SharedAppearanceDto>(get.Value);
        Assert.False(dto.Available);
        Assert.Null(dto.Appearance);

        var put = Assert.IsType<ObjectResult>(await Controller(store).Put(new SharedAppearanceBody(Theme: "dark")));
        Assert.Equal(404, put.StatusCode);
        Assert.Contains("not_ready", System.Text.Json.JsonSerializer.Serialize(put.Value));
        Assert.Equal(0, store.Reads + store.Writes);
    }

    [Fact]
    public async Task A_failing_probe_counts_as_not_ready_and_is_cached()
    {
        var store = new FakeStore(ready: true, throws: true);
        var service = Service(store);
        Assert.False(await service.ReadyAsync());
        Assert.False(await service.ReadyAsync());
        Assert.Equal(1, store.Probes);
    }

    [Fact]
    public async Task A_bad_value_is_refused_before_anything_is_read()
    {
        var store = new FakeStore(ready: true);
        var put = Assert.IsType<ObjectResult>(await Controller(store).Put(new SharedAppearanceBody(Theme: "neon")));
        Assert.Equal(400, put.StatusCode);
        Assert.Contains(SharedAppearanceRules.InvalidCode, System.Text.Json.JsonSerializer.Serialize(put.Value));
        Assert.Equal(0, store.Probes + store.Writes);
    }

    [Fact]
    public async Task A_change_keeps_the_fields_another_app_set()
    {
        var store = new FakeStore(ready: true);
        await Controller(store).Put(new SharedAppearanceBody(Theme: "dark", Accent: "purple", AiRibbon: false));
        var put = Assert.IsType<ObjectResult>(await Controller(store).Put(new SharedAppearanceBody(Nav: "slate", Density: "compact")));
        Assert.Equal(200, put.StatusCode);
        var a = Assert.IsType<SharedAppearanceDto>(put.Value).Appearance!;
        Assert.Equal(("dark", "slate", "compact", "purple", false), (a.Theme, a.Nav, a.Density, a.Accent, a.AiRibbon));
        Assert.Equal(SharedApps.Flow, store.LastApp);

        var get = Assert.IsType<SharedAppearanceDto>(Assert.IsType<ObjectResult>(await Controller(store).Get()).Value);
        Assert.True(get.Available);
        Assert.Equal("slate", get.Appearance!.Nav);
    }

    [Fact]
    public async Task Each_account_reads_only_its_own()
    {
        var store = new FakeStore(ready: true);
        await Controller(store, "user_a").Put(new SharedAppearanceBody(Theme: "dark"));
        var other = Assert.IsType<SharedAppearanceDto>(Assert.IsType<ObjectResult>(await Controller(store, "user_b").Get()).Value);
        Assert.True(other.Available);
        Assert.Null(other.Appearance);
    }

    [Fact]
    public async Task An_account_id_too_long_for_the_table_is_left_in_the_browser()
    {
        var store = new FakeStore(ready: true);
        var get = Assert.IsType<SharedAppearanceDto>(Assert.IsType<ObjectResult>(await Controller(store, new string('x', 51)).Get()).Value);
        Assert.False(get.Available);
        Assert.Equal(0, store.Reads);
    }

    [Theory]
    [InlineData(SqlSharedAppearanceStore.ProbeSql)]
    [InlineData(SqlSharedAppearanceStore.GetSql)]
    [InlineData(SqlSharedAppearanceStore.SaveSql)]
    [InlineData(SqlSharedDetailsStore.ProbeSql)]
    [InlineData(SqlSharedDetailsStore.GetSql)]
    [InlineData(SqlSharedDetailsStore.SetSql)]
    public void The_sql_parses(string sql)
    {
        // Dapper expands a list parameter (IN @Ids) before SQL Server sees it.
        new TSql160Parser(true).Parse(new StringReader(sql.Replace("IN @Ids", "IN (@Ids)")), out var errors);
        Assert.Empty(errors);
    }

    // ── The shared workspace cover and description ─────────────────────────

    private static string? Image(string c) => c.StartsWith("data:image/png;base64,") ? null : "The cover image must be a PNG, JPEG or WebP image.";

    [Fact]
    public void Details_are_checked_as_orbits_cover_is()
    {
        Assert.Null(SharedDetailsRules.Problem(new SharedDetailsChange("What this is for", "data:image/png;base64,AAAA", null), Image));
        Assert.Null(SharedDetailsRules.Problem(new SharedDetailsChange(null, null, "linear-gradient(135deg, #223947, #3b82f6)"), Image));
        Assert.Null(SharedDetailsRules.Problem(new SharedDetailsChange("", "", ""), Image));
        Assert.NotNull(SharedDetailsRules.Problem(new SharedDetailsChange(new string('a', 501)), Image));
        Assert.NotNull(SharedDetailsRules.Problem(new SharedDetailsChange(null, "data:image/png;base64,AAAA", "linear-gradient(red, blue)"), Image));
        Assert.NotNull(SharedDetailsRules.Problem(new SharedDetailsChange(null, "data:image/svg+xml;base64,AAAA"), Image));
        Assert.NotNull(SharedDetailsRules.Problem(new SharedDetailsChange(null, null, "url(https://example.com/x.png)"), Image));
        Assert.NotNull(SharedDetailsRules.Problem(new SharedDetailsChange(null, null, "linear-gradient(red, blue); color: red"), Image));
    }

    [Fact]
    public void Setting_the_photo_clears_the_preset_and_the_other_way_round()
    {
        Assert.Equal((true, "data:image/png;base64,AAAA", true, (string?)null), SqlSharedDetailsStore.CoverColumns(new(null, "data:image/png;base64,AAAA", null)));
        Assert.Equal((true, (string?)null, true, "linear-gradient(red, blue)"), SqlSharedDetailsStore.CoverColumns(new(null, null, " linear-gradient(red, blue) ")));
        Assert.Equal((true, (string?)null, true, (string?)null), SqlSharedDetailsStore.CoverColumns(new(null, "", "")));
        Assert.Equal((false, (string?)null, false, (string?)null), SqlSharedDetailsStore.CoverColumns(new("Only the description")));
    }

    private static (WorkspaceDetailsController C, FakeWorkspaces W, Guid Ws) Details(string role, SharedDetailsService? service = null)
    {
        var w = new FakeWorkspaces();
        var ws = role == FlowRoles.Owner ? w.Add("Sales", ("user_1", role)) : w.Add("Sales", ("owner_0", FlowRoles.Owner), ("user_1", role));
        var c = new WorkspaceDetailsController(w, NullLogger<WorkspaceDetailsController>.Instance, service);
        c.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user_1")], "test")) } };
        return (c, w, ws);
    }

    private sealed class NoDetails : ISharedDetailsStore
    {
        public int Writes;
        public Task<bool> ProbeAsync() => Task.FromResult(false);
        public Task<IReadOnlyList<SharedDetails>> GetAsync(IReadOnlyCollection<Guid> sharedIds) => Task.FromResult<IReadOnlyList<SharedDetails>>([]);
        public Task<bool> SetAsync(Guid sharedId, SharedDetailsChange change, bool onlyIfUnset) { Writes++; return Task.FromResult(true); }
    }

    [Fact]
    public async Task Before_the_sql_has_run_the_cover_is_hidden_and_not_saved()
    {
        var store = new NoDetails();
        var (c, _, ws) = Details(FlowRoles.Owner, new SharedDetailsService(store, NullLogger<SharedDetailsService>.Instance));
        var get = Assert.IsType<WorkspaceDetailsController.DetailsDto>(Assert.IsType<OkObjectResult>((await c.Get(ws)).Result).Value);
        Assert.False(get.Available);
        Assert.True(get.CanEdit);
        var put = Assert.IsType<ConflictObjectResult>((await c.Put(ws, new("For the sales team", null, null))).Result);
        Assert.Contains("not_ready", System.Text.Json.JsonSerializer.Serialize(put.Value));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public async Task Only_owners_and_admins_change_the_cover()
    {
        var (member, _, ws) = Details(FlowRoles.Member);
        Assert.Equal(403, Assert.IsType<ObjectResult>((await member.Put(ws, new("x", null, null))).Result).StatusCode);
        var get = Assert.IsType<WorkspaceDetailsController.DetailsDto>(Assert.IsType<OkObjectResult>((await member.Get(ws)).Result).Value);
        Assert.False(get.CanEdit);
        var (admin, _, ws2) = Details(FlowRoles.Admin);
        Assert.IsType<ConflictObjectResult>((await admin.Put(ws2, new("x", null, null))).Result);
    }

    [Fact]
    public async Task Someone_outside_the_workspace_sees_nothing()
    {
        var (c, w, _) = Details(FlowRoles.Owner);
        var other = w.Add("Elsewhere", ("user_2", FlowRoles.Owner));
        Assert.IsType<NotFoundObjectResult>((await c.Get(other)).Result);
        Assert.IsType<NotFoundObjectResult>((await c.Put(other, new("x", null, null))).Result);
    }

    [Theory]
    [InlineData(null, "data:image/svg+xml;base64,PHN2Zz48L3N2Zz4=", null, "invalid_cover")]
    [InlineData(null, null, "url(https://example.com/a.png)", "invalid_cover")]
    [InlineData(null, "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAAB", "linear-gradient(red, blue)", "invalid_cover")]
    [InlineData("toolong", null, null, "invalid_description")]
    [InlineData(null, null, null, "invalid_details")]
    public async Task Bad_details_are_refused(string? description, string? cover, string? preset, string code)
    {
        var (c, _, ws) = Details(FlowRoles.Owner);
        if (description == "toolong") description = new string('a', 501);
        var put = Assert.IsType<BadRequestObjectResult>((await c.Put(ws, new(description, cover, preset))).Result);
        Assert.Contains(code, System.Text.Json.JsonSerializer.Serialize(put.Value));
    }

    [Fact]
    public void A_real_png_cover_passes_the_image_check()
    {
        var png = "data:image/png;base64," + Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52]);
        Assert.Null(WorkspaceDetailsController.ImageProblem(png));
        Assert.NotNull(WorkspaceDetailsController.ImageProblem("data:image/png;base64," + new string('A', 400_000)));
    }
}
