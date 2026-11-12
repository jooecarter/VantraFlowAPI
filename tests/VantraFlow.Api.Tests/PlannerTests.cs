using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Ai;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>The AI flow builder's planner: what it tells the model, the schema it holds the answer to, and how it reads the answer back.</summary>
public sealed class PlannerTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 9, 0, 0, DateTimeKind.Utc);
    private static readonly FlowLimits Limits = new();

    private static PlannerContext Context(FlowDefinition? current = null) => new(
        ["orbit", "slack"], ["slack", "google"],
        [new PlannerTable("tbl_deals", "Deals", ["Name", "Stage", "Amount"])],
        [new PlannerProject("prj_1", "Website refresh")], "Europe/London", current);

    [Fact]
    public void The_instructions_carry_every_trigger_and_action_in_the_catalog()
    {
        var text = FlowPlanner.Instructions(Context(), Now);
        foreach (var t in FlowCatalog.Triggers) Assert.Contains($"- {t.App} / {t.Key} — {t.Title}.", text);
        foreach (var a in FlowCatalog.Actions) Assert.Contains($"- {a.App} / {a.Key} — {a.Title}.", text);
        // Required keys are marked, and trigger data comes from the catalog's own samples.
        Assert.Contains("channel* (a channel)", text);
        Assert.Contains("{{trigger.record.fields.Name}}", text);
        Assert.Contains("tbl_deals: Deals — Name, Stage, Amount", text);
        Assert.Contains("prj_1: Website refresh", text);
        Assert.Contains("Connected apps: orbit, slack.", text);
        Assert.Contains("Europe/London", text);
    }

    [Fact]
    public void The_schema_is_strict_every_object_closed_with_every_key_required()
    {
        var schema = FlowPlanner.Schema();
        void Check(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                if (o["type"] is JsonValue tv && tv.GetValue<string>() == "object")
                {
                    Assert.False(o["additionalProperties"]!.GetValue<bool>());
                    var props = (o["properties"] as JsonObject)!.Select(p => p.Key).OrderBy(x => x);
                    Assert.Equal(props, (o["required"] as JsonArray)!.Select(x => x!.GetValue<string>()).OrderBy(x => x));
                }
                foreach (var (_, v) in o) Check(v);
            }
            else if (n is JsonArray a) foreach (var x in a) Check(x);
        }
        Check(schema);
        Assert.Equal(["action", "filter", "branch", "paths", "loop", "delay", "formatter", "variable", "wait", "approval"],
            schema["$defs"]!["step"]!["properties"]!["type"]!["enum"]!.AsArray().Select(x => x!.GetValue<string>()));
    }

    private const string WonDeal = """
    {
      "name": "Won deals to Slack and Projects",
      "description": "When a deal is won, tell #sales and start a project.",
      "explanation": "Posts in #sales and creates a project for each won deal.",
      "trigger": { "app": "orbit", "event": "record.matches", "config": [
        { "key": "tableId", "value": "tbl_deals", "json": false },
        { "key": "condition", "value": "{\"match\":\"all\",\"rules\":[{\"left\":\"{{trigger.record.fields.Stage}}\",\"op\":\"equals\",\"right\":\"Won\"}]}", "json": true }
      ] },
      "steps": [
        { "id": "s1", "type": "action", "app": "slack", "action": "message.send", "name": "Post in #sales",
          "config": [ { "key": "channel", "value": "sales", "json": false }, { "key": "text", "value": "Won: {{trigger.record.fields.Name}}", "json": false } ],
          "then": [], "else": [], "do": [], "paths": [] },
        { "id": "s2", "type": "action", "app": "projects", "action": "project.create", "name": "Create the project",
          "config": [ { "key": "name", "value": "{{trigger.record.fields.Name}}", "json": false }, { "key": "description", "value": "", "json": false } ],
          "then": [], "else": [], "do": [], "paths": [] },
        { "id": "s3", "type": "action", "app": "fax", "action": "send", "name": "Fax the board", "config": [], "then": [], "else": [], "do": [], "paths": [] }
      ]
    }
    """;

    [Fact]
    public void A_good_answer_reads_into_a_definition_held_to_the_catalog()
    {
        var d = FlowPlanner.Read(WonDeal, Limits)!;
        Assert.Equal("Won deals to Slack and Projects", d.Name);
        Assert.Equal(("orbit", "record.matches"), (d.Definition.Trigger.App, d.Definition.Trigger.Event));
        Assert.Equal("tbl_deals", d.Definition.Trigger.Config["tableId"]!.GetValue<string>());
        Assert.IsType<JsonObject>(d.Definition.Trigger.Config["condition"]);
        Assert.Equal(["s1", "s2"], d.Definition.Steps.Select(s => s.Id));
        Assert.Equal("sales", d.Definition.Steps[0].Config["channel"]!.GetValue<string>());
        // The made-up step is dropped, and said so.
        Assert.Contains(d.Notes, n => n.Contains("Fax the board"));
        Assert.Empty(DefinitionRules.Validate(d.Definition, Limits));
    }

    [Fact]
    public void What_the_request_didnt_say_is_left_for_the_builder_to_ask_for()
    {
        const string answer = """
        { "name": "Daily note", "description": "", "explanation": "Pick who it goes to.",
          "trigger": { "app": "schedule", "event": "daily", "config": [ { "key": "time", "value": "08:30", "json": false } ] },
          "steps": [ { "id": "s1", "type": "action", "app": "email", "action": "send", "name": "Send it",
            "config": [ { "key": "to", "value": "", "json": false }, { "key": "subject", "value": "Good morning", "json": false } ],
            "then": [], "else": [], "do": [], "paths": [] } ] }
        """;
        var d = FlowPlanner.Read(answer, Limits)!;
        var errors = DefinitionRules.Validate(d.Definition, Limits);
        Assert.Equal("s1", Assert.Single(errors).StepId);
        Assert.Null(d.Description);
    }

    [Fact]
    public void An_unknown_trigger_is_cleared_and_unknown_step_types_dropped()
    {
        const string answer = """
        { "name": "", "description": "", "explanation": "",
          "trigger": { "app": "crm", "event": "deal.won", "config": [] },
          "steps": [ { "id": "s1", "type": "teleport", "app": "", "action": "", "name": "", "config": [], "then": [], "else": [], "do": [], "paths": [] } ] }
        """;
        var d = FlowPlanner.Read(answer, Limits)!;
        Assert.Equal("", d.Definition.Trigger.App);
        Assert.Empty(d.Definition.Steps);
        Assert.Equal("Untitled flow", d.Name);
        Assert.Equal(2, d.Notes.Count);
    }

    [Fact]
    public void Nonsense_is_null() => Assert.Null(FlowPlanner.Read("not json", Limits));

    [Fact]
    public void Branches_loops_and_paths_keep_their_shape_and_loops_dont_nest()
    {
        const string answer = """
        { "name": "n", "description": "", "explanation": "",
          "trigger": { "app": "webhook", "event": "catch", "config": [] },
          "steps": [
            { "id": "s1", "type": "loop", "app": "", "action": "", "name": "Each", "config": [ { "key": "items", "value": "{{trigger.body.items}}", "json": false } ],
              "then": [], "else": [], "paths": [],
              "do": [
                { "id": "s2", "type": "loop", "app": "", "action": "", "name": "Inner", "config": [], "then": [], "else": [], "do": [], "paths": [] },
                { "id": "s3", "type": "action", "app": "email", "action": "send", "name": "Mail", "config": [ { "key": "to", "value": "{{loop.item.email}}", "json": false }, { "key": "subject", "value": "Hi", "json": false } ], "then": [], "else": [], "do": [], "paths": [] }
              ] },
            { "id": "s4", "type": "paths", "app": "", "action": "", "name": "Size", "config": [], "then": [], "else": [], "do": [],
              "paths": [ { "name": "Big", "condition": "{\"match\":\"all\",\"rules\":[{\"left\":\"{{trigger.body.size}}\",\"op\":\"greater_than\",\"right\":\"10\"}]}",
                "steps": [ { "id": "s5", "type": "delay", "app": "", "action": "", "name": "Wait", "config": [ { "key": "amount", "value": "1", "json": false }, { "key": "unit", "value": "hours", "json": false } ], "then": [], "else": [], "do": [], "paths": [] } ] } ] }
          ] }
        """;
        var d = FlowPlanner.Read(answer, Limits)!;
        Assert.Equal(["s1", "s3", "s4", "s5"], d.Definition.AllSteps().Select(s => s.Id));
        Assert.Contains(d.Notes, n => n.Contains("Repeat for each"));
        Assert.NotNull(d.Definition.Steps[1].Paths![0].Condition);
        Assert.Empty(DefinitionRules.Validate(d.Definition, Limits));
    }

    [Fact]
    public void An_approval_gets_its_three_paths_and_no_made_up_approvers()
    {
        const string answer = """
        { "name": "n", "description": "", "explanation": "",
          "trigger": { "app": "webhook", "event": "catch", "config": [] },
          "steps": [ { "id": "s1", "type": "approval", "app": "", "action": "", "name": "Approve the refund",
            "config": [ { "key": "title", "value": "Refund {{trigger.body.amount}}?", "json": false }, { "key": "approvers", "value": "[\"u_made_up\"]", "json": true } ],
            "then": [], "else": [], "do": [],
            "paths": [ { "name": "Rejected", "condition": "", "steps": [ { "id": "s2", "type": "action", "app": "email", "action": "send", "name": "Say no",
                "config": [ { "key": "to", "value": "{{trigger.body.email}}", "json": false }, { "key": "subject", "value": "Sorry", "json": false } ], "then": [], "else": [], "do": [], "paths": [] } ] } ] } ] }
        """;
        var d = FlowPlanner.Read(answer, Limits)!;
        var a = d.Definition.Steps.Single();
        Assert.Equal(["Approved", "Rejected", "Timed out"], a.Paths!.Select(p => p.Name));
        Assert.Equal("s2", a.Paths[1].Steps.Single().Id);
        Assert.Empty(a.Config["approvers"]!.AsArray());
        // What's left is choosing who approves.
        Assert.All(DefinitionRules.Validate(d.Definition, Limits), e => Assert.Equal("s1", e.StepId));
    }

    [Fact]
    public void Bad_or_repeated_ids_are_renumbered_and_tokens_follow()
    {
        const string answer = """
        { "name": "n", "description": "", "explanation": "",
          "trigger": { "app": "webhook", "event": "catch", "config": [] },
          "steps": [
            { "id": "Step-1", "type": "action", "app": "projects", "action": "project.create", "name": "Make", "config": [ { "key": "name", "value": "X", "json": false } ], "then": [], "else": [], "do": [], "paths": [] },
            { "id": "s2", "type": "action", "app": "email", "action": "send", "name": "Tell", "config": [ { "key": "to", "value": "team@example.com", "json": false }, { "key": "subject", "value": "{{steps.Step-1.url}}", "json": false } ], "then": [], "else": [], "do": [], "paths": [] }
          ] }
        """;
        var d = FlowPlanner.Read(answer, Limits)!;
        Assert.Equal(["s1", "s2"], d.Definition.Steps.Select(s => s.Id));
        Assert.Equal("{{steps.s1.url}}", d.Definition.Steps[1].Config["subject"]!.GetValue<string>());
        Assert.Empty(DefinitionRules.Validate(d.Definition, Limits));
    }

    [Fact]
    public void A_flow_longer_than_the_limit_is_cut_short()
    {
        var steps = string.Join(",", Enumerable.Range(1, 30).Select(i =>
            $$"""{ "id": "s{{i}}", "type": "delay", "app": "", "action": "", "name": "", "config": [ { "key": "amount", "value": "1", "json": false } ], "then": [], "else": [], "do": [], "paths": [] }"""));
        var d = FlowPlanner.Read($$"""{ "name": "n", "description": "", "explanation": "", "trigger": { "app": "webhook", "event": "catch", "config": [] }, "steps": [{{steps}}] }""", Limits)!;
        Assert.Equal(Limits.MaxSteps, d.Definition.AllSteps().Count());
    }

    [Fact]
    public void Config_reads_objects_text_and_flags_and_drops_broken_objects()
    {
        var c = FlowPlanner.Config(JsonNode.Parse("""
        [ { "key": "fields", "value": "{\"Name\":\"Ada\"}", "json": true },
          { "key": "values", "value": "not json", "json": true },
          { "key": "replies", "value": "true", "json": true },
          { "key": "text", "value": "Hello", "json": false },
          { "key": "text", "value": "Twice", "json": false } ]
        """));
        Assert.Equal("Ada", c["fields"]!["Name"]!.GetValue<string>());
        Assert.False(c.ContainsKey("values"));
        Assert.True(c["replies"]!.GetValue<bool>());
        Assert.Equal("Hello", c["text"]!.GetValue<string>());
    }

    [Fact]
    public void Secrets_in_the_current_flow_never_reach_the_model_and_come_back_from_the_flow()
    {
        var current = new FlowDefinition
        {
            Trigger = new TriggerDef { App = "webhook", Event = "catch" },
            Steps =
            [
                new StepDef
                {
                    Id = "s1", Type = "action", App = "http", Action = "request", Name = "Call",
                    Config = new JsonObject { ["url"] = "https://example.com/hook", ["headers"] = new JsonObject { ["Authorization"] = "Bearer sk_live_secret" }, ["apiKey"] = "k-123" },
                },
            ],
        };
        var text = FlowPlanner.Instructions(Context(current), Now);
        Assert.DoesNotContain("sk_live_secret", text);
        Assert.DoesNotContain("k-123", text);
        Assert.Contains("https://example.com/hook", text);
        Assert.Contains("Authorization", text);

        const string answer = """
        { "name": "n", "description": "", "explanation": "",
          "trigger": { "app": "webhook", "event": "catch", "config": [] },
          "steps": [
            { "id": "s1", "type": "action", "app": "http", "action": "request", "name": "Call",
              "config": [ { "key": "url", "value": "https://example.com/hook", "json": false }, { "key": "headers", "value": "{\"Authorization\":\"[kept]\"}", "json": true },
                          { "key": "apiKey", "value": "[kept]", "json": false } ], "then": [], "else": [], "do": [], "paths": [] },
            { "id": "s2", "type": "action", "app": "http", "action": "request", "name": "Another",
              "config": [ { "key": "url", "value": "https://example.com/two", "json": false }, { "key": "headers", "value": "{\"Authorization\":\"[kept]\"}", "json": true } ], "then": [], "else": [], "do": [], "paths": [] }
          ] }
        """;
        var d = FlowPlanner.Read(answer, Limits, current)!;
        Assert.Equal("Bearer sk_live_secret", d.Definition.Steps[0].Config["headers"]!["Authorization"]!.GetValue<string>());
        Assert.Equal("k-123", d.Definition.Steps[0].Config["apiKey"]!.GetValue<string>());
        // A new step can't borrow a secret: it's left empty.
        Assert.Equal("", d.Definition.Steps[1].Config["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void Trigger_data_is_read_from_the_catalogs_samples() =>
        Assert.Contains("record.fields.Email", PlannerCatalog.TokensOf(FlowCatalog.Sample(new TriggerDef { App = "orbit", Event = "record.created" }, Now)));

    [Theory]
    [InlineData(true, 200, null, 200)]   // Pro's credits
    [InlineData(true, 0, null, 0)]       // Free and Starter: none
    [InlineData(true, 1000, 300, 300)]   // Ai:MonthlyCap is a ceiling on every plan
    [InlineData(false, 200, null, 200)]  // before plans are in force: the default
    [InlineData(false, 200, 50, 50)]     // … or Ai:MonthlyCap
    public void Drafts_a_month_come_from_the_plan(bool enforced, int credits, int? configured, int cap) =>
        Assert.Equal(cap, AiClient.CapFor(enforced, credits, configured));

    [Fact]
    public void The_per_workspace_share_runs_out()
    {
        var ws = Guid.NewGuid();
        var taken = Enumerable.Range(0, AiClient.PerWorkspacePerMinute + 3).Count(_ => AiClient.TakeWorkspaceTurn(ws));
        Assert.Equal(AiClient.PerWorkspacePerMinute, taken);
        Assert.True(AiClient.TakeWorkspaceTurn(Guid.NewGuid()));
    }
}
