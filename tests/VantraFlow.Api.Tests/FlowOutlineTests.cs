using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>The Flows list's outline of a flow: what it watches and where it sends, with nothing secret or long.</summary>
public sealed class FlowOutlineTests
{
    private static FlowDefinition Def() => FlowDefinition.Parse("""
    {
      "trigger": { "app": "orbit", "event": "record.created", "config": { "tableId": "tbl_leads", "condition": { "match": "all", "rules": [] } } },
      "steps": [
        { "id": "s1", "type": "paths", "config": {}, "paths": [ { "name": "Big", "steps": [
          { "id": "s2", "type": "action", "app": "email", "action": "send", "name": "To the sales team",
            "config": { "to": "sales@example.com, {{trigger.record.fields.Email}}", "subject": "New lead", "body": "A very long body that must not travel" } } ] } ],
          "else": [ { "id": "s3", "type": "action", "app": "microsoft", "action": "teams.post", "config": { "teamId": "team-sales", "channelId": "19:pipeline", "text": "x" } } ] },
        { "id": "s4", "type": "action", "app": "http", "action": "request",
          "config": { "url": "https://api.example.com/v1/x?token=abc", "auth": { "type": "bearer", "secretId": "sec_1" }, "headers": { "X-Key": "k" } } },
        { "id": "s5", "type": "action", "app": "orbit", "action": "record.update", "config": { "tableId": "tbl_deals" } }
      ]
    }
    """)!;

    [Fact]
    public void It_names_the_trigger_and_the_first_actions_in_running_order()
    {
        var o = FlowOutline.Build(Def());
        Assert.Equal("orbit", (string?)o["trigger"]!["app"]);
        Assert.Equal("tbl_leads", (string?)o["trigger"]!["config"]!["tableId"]);
        Assert.Null(o["trigger"]!["config"]!["condition"]);
        var actions = o["actions"]!.AsArray();
        Assert.Equal(FlowOutline.MaxTargets, actions.Count);
        Assert.Equal(4, o["actionCount"]!.GetValue<int>());
        Assert.Equal(["email", "microsoft", "http"], actions.Select(a => (string)a!["app"]!));
        Assert.Equal("sales@example.com, {{trigger.record.fields.Email}}", (string?)actions[0]!["config"]!["to"]);
        Assert.Equal("To the sales team", (string?)actions[0]!["name"]);
        Assert.Equal("19:pipeline", (string?)actions[1]!["config"]!["channelId"]);
        Assert.Equal("api.example.com", (string?)actions[2]!["config"]!["host"]);
    }

    [Fact]
    public void Nothing_secret_or_long_goes_in()
    {
        var json = FlowOutline.Build(Def()).ToJsonString();
        foreach (var leak in new[] { "token=abc", "sec_1", "X-Key", "bearer", "A very long body", "subject", "\"text\"" }) Assert.DoesNotContain(leak, json);
        var def = Def();
        def.Steps[1].Config["url"] = "https://{{steps.s1.host}}/x";
        Assert.Null(FlowOutline.Build(def)["actions"]![2]!["config"]!["host"]);
        def.Trigger.Config["title"] = new string('x', 500);
        Assert.Equal(FlowOutline.MaxText, ((string)FlowOutline.Build(def)["trigger"]!["config"]!["title"]!).Length);
    }
}
