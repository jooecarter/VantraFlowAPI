using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Team;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Duplicate, export and import: exports never hold secrets or tokens, imports are checked strictly, permissions hold.</summary>
public sealed class PortTests
{
    private const string Rich = """
    {
      "trigger": { "app": "orbit", "event": "record.created", "config": { "tableId": "tbl_leads" } },
      "steps": [
        { "id": "s1", "type": "action", "app": "http", "action": "request", "name": "Tell the CRM",
          "config": { "method": "POST", "url": "https://api.example.com/leads",
            "headers": { "Authorization": "Bearer sk_live_abc123", "X-Api-Key": "key_987", "Content-Type": "application/json" },
            "query": { "access_token": "tok_555", "page": "1" },
            "auth": { "type": "bearer", "secretId": "sec_1" } } },
        { "id": "s2", "type": "approval", "name": "Sign off", "config": { "title": "Approve?", "approvers": ["priya", "tom"] },
          "paths": [ { "name": "Approved", "steps": [] }, { "name": "Rejected", "steps": [] }, { "name": "Timed out", "steps": [] } ] },
        { "id": "s3", "type": "action", "app": "flow", "action": "run", "name": "Hand over", "config": { "flowId": "flw_b" } },
        { "id": "s4", "type": "action", "app": "slack", "action": "message.send", "config": { "channel": "C1", "text": "New lead" } }
      ],
      "options": { "runOnFlowChanges": false }
    }
    """;

    private static FlowDefinition RichDef => FlowDefinition.Parse(Rich)!;
    private static readonly Dictionary<string, string> SecretNames = new() { ["sec_1"] = "CRM token" };
    private static readonly Dictionary<string, string> FlowNames = new() { ["flw_b"] = "Onboard customer" };
    private static readonly Dictionary<string, string> Connections = new() { ["orbit"] = "Northwind Trading", ["slack"] = "Northwind HQ" };

    private static JsonObject Export() => FlowPorter.Export("Route leads", "Sends leads on", RichDef, SecretNames, FlowNames, Connections, DateTime.UtcNow);

    // ── Export redaction ──

    [Fact]
    public void An_export_never_holds_secrets_sealed_values_or_tokens()
    {
        var doc = Export();
        var text = doc.ToJsonString();
        foreach (var leak in new[] { "sk_live_abc123", "key_987", "tok_555", "sec_1", "secretId", "flw_b", "priya", "hookSecret", "HookId" })
            Assert.DoesNotContain(leak, text, StringComparison.Ordinal);

        var s1 = doc["flow"]!["definition"]!["steps"]![0]!["config"]!;
        Assert.Equal("", (string?)s1["headers"]!["Authorization"]);
        Assert.Equal("", (string?)s1["headers"]!["X-Api-Key"]);
        Assert.Equal("application/json", (string?)s1["headers"]!["Content-Type"]);
        Assert.Equal("1", (string?)s1["query"]!["page"]);
        Assert.Equal("CRM token", (string?)s1["auth"]!["secret"]);
        Assert.Equal("Onboard customer", (string?)doc["flow"]!["definition"]!["steps"]![2]!["config"]!["flow"]);
        Assert.Empty(doc["flow"]!["definition"]!["steps"]![1]!["config"]!["approvers"]!.AsArray());

        // What it needs, by name only.
        Assert.Equal(["CRM token"], doc["needs"]!["secrets"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Equal(["Onboard customer"], doc["needs"]!["flows"]!.AsArray().Select(x => (string)x!).ToArray());
        var apps = doc["needs"]!["apps"]!.AsArray().Select(a => ((string?)a!["app"], (string?)a["connection"])).ToArray();
        Assert.Equal([("orbit", "Northwind Trading"), ("slack", "Northwind HQ")], apps);
        Assert.Contains("Tell the CRM: Authorization", doc["redacted"]!.AsArray().Select(x => (string?)x));
    }

    [Fact]
    public void An_export_reads_back_and_binds_its_secrets_and_flows_by_name()
    {
        var doc = Export();
        var read = FlowPorter.Read(doc, doc.ToJsonString().Length, new FlowLimits());
        Assert.Empty(read.Errors);
        Assert.Equal("Route leads", read.Name);
        Assert.Equal(["CRM token"], read.Needs.Secrets);
        var (def, unmapped) = FlowPorter.Bind(read.Definition!, new Dictionary<string, string> { ["CRM token"] = "sec_here" },
            new Dictionary<string, string> { ["Onboard customer"] = "flw_here" });
        var steps = def.AllSteps().ToList();
        Assert.Equal("sec_here", (string?)steps[0].Config["auth"]!["secretId"]);
        Assert.Null(steps[0].Config["auth"]!["secret"]);
        Assert.Equal("flw_here", (string?)steps.First(s => s.Id == "s3").Config["flowId"]);
        Assert.Equal(["Sign off: choose who approves"], unmapped);
    }

    // ── Import validation ──

    private static JsonObject Doc(string definition, Action<JsonObject>? change = null)
    {
        var doc = new JsonObject
        {
            ["format"] = "vantra-flow", ["version"] = 1,
            ["flow"] = new JsonObject { ["name"] = "Imported", ["definition"] = JsonNode.Parse(definition) },
        };
        change?.Invoke(doc);
        return doc;
    }

    private static IReadOnlyList<string> Errors(JsonNode? doc, int? size = null) =>
        FlowPorter.Read(doc, size ?? doc?.ToJsonString().Length ?? 0, new FlowLimits()).Errors;

    private const string Simple = """{"trigger":{"app":"schedule","event":"interval","config":{}},"steps":[{"id":"s1","type":"action","app":"email","action":"send","config":{}}]}""";

    [Fact]
    public void Imports_are_checked_strictly_against_the_catalog()
    {
        Assert.Empty(Errors(Doc(Simple)));
        Assert.Contains("That isn't a Vantra Flow export.", Errors(JsonNode.Parse("""{"format":"zapier"}""")));
        Assert.Contains("That isn't a Vantra Flow export.", Errors(JsonNode.Parse("[1,2]")));
        Assert.Contains("newer version", Errors(Doc(Simple, d => d["version"] = 2))[0]);
        Assert.Contains("Unexpected field “secrets”.", Errors(Doc(Simple, d => d["secrets"] = "x")));
        Assert.Contains("Unexpected field “flow.enabled”.", Errors(Doc(Simple, d => d["flow"]!["enabled"] = true)));
        Assert.Contains("The flow needs a name of up to 160 characters.", Errors(Doc(Simple, d => d["flow"]!["name"] = "")));

        Assert.Contains("no trigger", Errors(Doc("""{"trigger":{"app":"orbit","event":"record.exploded","config":{}},"steps":[]}"""))[0]);
        Assert.Contains("an action Vantra Flow doesn't have",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"action","app":"email","action":"spam","config":{}}]}"""))[0]);
        Assert.Contains("a kind of step Vantra Flow doesn't have",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"shell","config":{}}]}"""))[0]);
        Assert.Contains("can't name an app",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"delay","app":"http","config":{}}]}"""))[0]);
        Assert.Contains("Two steps share the id “s1”.",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"delay","config":{}},{"id":"s1","type":"delay","config":{}}]}""")));
        Assert.Contains("an id Vantra Flow can't use",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"S 1","type":"delay","config":{}}]}"""))[0]);
        // Unknown fields inside the definition are refused too.
        Assert.Contains("can't be read",
            Errors(Doc("""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"delay","config":{},"script":"rm -rf"}]}"""))[0]);
    }

    [Fact]
    public void Imports_have_a_size_cap_and_the_step_limit()
    {
        Assert.Contains("too large", Errors(Doc(Simple), FlowPorter.MaxFileBytes + 1)[0]);
        var many = string.Join(",", Enumerable.Range(1, 26).Select(i => $$$"""{"id":"s{{{i}}}","type":"delay","config":{}}"""));
        Assert.Contains("A flow can have at most 25 steps.", Errors(Doc($$$"""{"trigger":{"app":"","event":"","config":{}},"steps":[{{{many}}}]}""")));
        var big = new string('x', FlowLimits.MaxConfigBytes);
        Assert.Contains("This flow is too large to import.",
            Errors(Doc($$$"""{"trigger":{"app":"","event":"","config":{}},"steps":[{"id":"s1","type":"delay","config":{"note":"{{{big}}}"}}]}""")));
    }

    // ── The endpoints ──

    private readonly FakeWorkspaces db = new();
    private readonly FakeFlows flows = new();
    private readonly FakeActivity log = new();
    private readonly FakeFirstVersion versions = new();

    private PortController Port(string sub, FakeTeam team) =>
        new(db, flows, team, versions, new ConfigurationBuilder().Build()) { ControllerContext = TeamHttp.As(sub, log) };

    [Fact]
    public async Task Importing_makes_a_switched_off_flow_with_its_secrets_mapped_and_logs_it()
    {
        var ws = db.Add("Northwind", ("alex", FlowRoles.Owner), ("tom", FlowRoles.Member));
        var team = new FakeTeam(flows);
        team.Secrets.Add(new SecretNameRow("sec_9", "CRM token"));
        flows.Add(ws, "flw_x", "Onboard customer", Simple);

        Assert.Equal(404, Http.Status(await Port("stranger", team).Import(ws, new ImportBody(Export(), null, null, null))));
        Assert.Equal(422, Http.Status(await Port("tom", team).Import(ws, new ImportBody(JsonNode.Parse("""{"format":"nope"}"""), null, null, null))));
        Assert.Equal(400, Http.Status(await Port("tom", team).Import(ws, new ImportBody(Export(), null, null,
            new Dictionary<string, string?> { ["CRM token"] = "sec_someone_elses" }))));

        var preview = await Port("tom", team).Preview(ws, new ImportBody(Export(), null, null, null));
        Assert.Equal(200, Http.Status(preview));
        var shown = JsonSerializer.SerializeToNode(Http.Body<object>(preview))!;
        Assert.Equal("sec_9", (string?)shown["secrets"]![0]!["match"]);
        Assert.Equal("Northwind Trading", (string?)shown["apps"]![0]!["from"]);
        Assert.False((bool)shown["apps"]![0]!["connected"]!);

        var made = await Port("tom", team).Import(ws, new ImportBody(Export(), "Route leads (imported)", null, null));
        Assert.Equal(201, Http.Status(made));
        var row = flows.Rows[^1];
        Assert.False(row.Enabled);
        Assert.Equal("Route leads (imported)", row.Name);
        Assert.Equal("tom", row.CreatedBy);
        Assert.Contains("\"secretId\":\"sec_9\"", row.Definition);
        Assert.Contains("\"flowId\":\"flw_x\"", row.Definition);
        Assert.DoesNotContain("sk_live", row.Definition);
        Assert.Equal((row.Id, "Imported from a file"), versions.Kept.Single());
        var line = Assert.Single(log.Lines);
        Assert.Equal((ActivityKinds.FlowCreated, row.Id, "Imported from a file"), (line.Kind, line.FlowId, line.Detail));
    }

    [Fact]
    public async Task Duplicating_into_another_workspace_needs_membership_there_and_uses_its_secrets()
    {
        var a = db.Add("Northwind", ("alex", FlowRoles.Owner), ("tom", FlowRoles.Member), ("priya", FlowRoles.Admin));
        var b = db.Add("Harbour", ("alex", FlowRoles.Owner), ("priya", FlowRoles.Member));
        var c = db.Add("Elsewhere", ("sam", FlowRoles.Owner));
        var team = new FakeTeam(flows);
        team.SecretsByWs[a] = [new SecretNameRow("sec_1", "CRM token")];
        team.SecretsByWs[b] = [new SecretNameRow("sec_b", "CRM token")];
        flows.Add(a, "flw_src", "Route leads", Rich);
        flows.Add(a, "flw_b", "Onboard customer", Simple);

        Assert.Equal(404, Http.Status(await Port("alex", team).Duplicate(a, "flw_src", new DuplicateBody(c, null))));
        Assert.Equal(404, Http.Status(await Port("tom", team).Duplicate(a, "flw_src", new DuplicateBody(b, null))));

        // In the same workspace the definition is kept as it is.
        Assert.Equal(201, Http.Status(await Port("tom", team).Duplicate(a, "flw_src", null)));
        var same = flows.Rows[^1];
        Assert.Equal(("Route leads (copy)", a), (same.Name, same.WorkspaceId));
        Assert.Contains("\"secretId\":\"sec_1\"", same.Definition);

        // In another: its own secrets by name; approvers only if they're in it; a flow it hasn't got left to choose.
        var made = await Port("alex", team).Duplicate(a, "flw_src", new DuplicateBody(b, "Route leads"));
        Assert.Equal(201, Http.Status(made));
        var copy = flows.Rows[^1];
        Assert.Equal(b, copy.WorkspaceId);
        Assert.False(copy.Enabled);
        var def = FlowDefinition.Parse(copy.Definition)!;
        Assert.Equal("sec_b", (string?)def.Steps[0].Config["auth"]!["secretId"]);
        Assert.Equal(["priya"], def.Steps[1].Config["approvers"]!.AsArray().Select(x => (string)x!).ToArray());
        Assert.Null(def.Steps[2].Config["flowId"]);
        Assert.Equal(b, log.Lines[^1].WorkspaceId);
        Assert.Equal("Duplicated from “Route leads” in Northwind", log.Lines[^1].Detail);
    }
}
