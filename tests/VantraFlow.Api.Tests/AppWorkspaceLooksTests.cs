using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Shared;
using Xunit;

namespace VantraFlow.Api.Tests;

public class AppWorkspaceLooksTests
{
    private static readonly Guid Shared = Guid.NewGuid();
    private static readonly Guid OrbitLocal = Guid.NewGuid();
    private const string Logo = "data:image/png;base64,iVBORw0KGgo=";

    private static SharedPlace Place(Guid id, string? color, string? logo, params SharedLink[] links) =>
        new(id, new SharedLook("Example Studio", color, logo, "Europe/London"), "owner-1", "owner", links, ["orbit"]);

    [Fact]
    public void A_linked_workspace_gets_its_shared_logo_and_colour_by_its_local_id()
    {
        var list = new[] { new AppWorkspace(OrbitLocal, "Example Studio", "owner", true) };
        var places = new[] { Place(Shared, "#2563eb", Logo, new SharedLink("orbit", OrbitLocal, true)) };
        var r = Assert.Single(AppWorkspaceLooks.Apply("orbit", list, places));
        Assert.Equal("#2563eb", r.Color);
        Assert.Equal(Logo, r.Logo);
        Assert.Equal(OrbitLocal, r.Id);
    }

    [Fact]
    public void A_workspace_with_the_shared_id_matches_without_a_link_for_this_app()
    {
        var list = new[] { new AppWorkspace(Shared, "Example Studio", "admin", true) };
        var r = Assert.Single(AppWorkspaceLooks.Apply("projects", list, [Place(Shared, "#16a34a", Logo)]));
        Assert.Equal(Logo, r.Logo);
    }

    [Fact]
    public void Another_apps_link_with_the_same_local_id_does_not_match()
    {
        var list = new[] { new AppWorkspace(OrbitLocal, "Example Studio", "owner", true) };
        var places = new[] { Place(Shared, "#2563eb", Logo, new SharedLink("projects", OrbitLocal, true)) };
        var r = Assert.Single(AppWorkspaceLooks.Apply("orbit", list, places));
        Assert.Null(r.Logo);
        Assert.Null(r.Color);
    }

    [Fact]
    public void A_removed_logo_and_no_shared_record_show_none()
    {
        var list = new[] { new AppWorkspace(Shared, "A", "owner", true), new AppWorkspace(Guid.NewGuid(), "B", "member", false) };
        var r = AppWorkspaceLooks.Apply("orbit", list, [Place(Shared, null, SharedAppRules.NoLogo)]);
        Assert.Null(r[0].Logo);
        Assert.Null(r[1].Logo);
        Assert.Null(r[1].Color);
        Assert.False(r[1].CanConnect);
    }
}
