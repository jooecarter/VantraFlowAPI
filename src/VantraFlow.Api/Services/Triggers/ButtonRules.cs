using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>
/// "A button on a record" (<c>orbit|projects / button.record</c>): a flow people start from a record in Orbit (config
/// <c>tableId</c>) or a task in Projects (<c>target: task</c>), with an optional label (the flow's name otherwise) and a
/// subtitle for the menu (made from its steps otherwise, e.g. "Email · Slack"). A run gets <c>trigger.record</c> (the
/// record or task, read fresh through the connection when it starts) and <c>trigger.runBy</c> (id, name, email).
/// </summary>
public static class ButtonRules
{
    public const string Event = "button.record";
    public const int MaxBulk = 50;
    public const int MaxLabel = 60;
    public const int MaxSubtitle = 80;

    public static bool IsButton(TriggerDef t) => t.Event == Event && t.App is "orbit" or "projects";

    /// <summary>What a button flow targets: an Orbit table id, or <c>task</c> in Projects.</summary>
    public static string Target(TriggerDef t) =>
        t.App == "orbit" ? Tokens.Text(t.Config["tableId"]).Trim() : Tokens.Text(t.Config["target"]) is "project" ? "project" : "task";

    public static IEnumerable<string> Check(TriggerDef t)
    {
        if (Tokens.Text(t.Config["label"]).Length > MaxLabel) yield return $"Keep the button's label to {MaxLabel} characters.";
        if (Tokens.Text(t.Config["subtitle"]).Length > MaxSubtitle) yield return $"Keep the subtitle to {MaxSubtitle} characters.";
    }

    public static string Label(TriggerDef t, string flowName) =>
        Tokens.Text(t.Config["label"]).Trim() is { Length: > 0 } l ? Clip(l, MaxLabel) : flowName;

    private static readonly Dictionary<string, string> Short = new(StringComparer.Ordinal)
    {
        ["email"] = "Email", ["orbit"] = "Orbit", ["projects"] = "Projects", ["slack"] = "Slack", ["microsoft"] = "Outlook", ["google"] = "Google",
        ["http"] = "HTTP", ["stripe"] = "Stripe", ["hubspot"] = "HubSpot", ["xero"] = "Xero", ["ai"] = "AI", ["flow"] = "Flow",
    };

    /// <summary>The menu's subtitle: the one set, else the apps its steps use ("Email · Slack"), else how many steps.</summary>
    public static string Subtitle(FlowDefinition def)
    {
        if (Tokens.Text(def.Trigger.Config["subtitle"]).Trim() is { Length: > 0 } s) return Clip(s, MaxSubtitle);
        var steps = def.AllSteps().ToList();
        var apps = steps.Where(x => x.Type == StepTypes.Action && x.App is not null).Select(x => Short.GetValueOrDefault(x.App!, x.App!)).Distinct().Take(3).ToList();
        if (steps.Any(x => x.Type == StepTypes.Approval) && apps.Count < 3) apps.Insert(0, "Approval");
        if (apps.Count > 0) return string.Join(" · ", apps);
        return steps.Count == 1 ? "1 step" : $"{steps.Count} steps";
    }

    /// <summary>What a button run hands its flow.</summary>
    public static JsonObject TriggerData(string app, JsonObject record, JsonObject? table, FormPerson by, DateTime at) => new()
    {
        ["record"] = record.DeepClone(),
        ["table"] = table?.DeepClone(),
        ["app"] = app,
        ["runBy"] = FormRules.PersonNode(by),
        ["runAt"] = Formatter.IsoUtc(at),
    };

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd();
}
