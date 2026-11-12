using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Team;

/// <summary>What an import needs from the workspace it lands in.</summary>
public sealed record PortNeeds(IReadOnlyList<PortApp> Apps, IReadOnlyList<string> Secrets, IReadOnlyList<string> Flows);

/// <summary>An app a flow uses, and the name of the connection it used where it came from (never a token).</summary>
public sealed record PortApp(string App, string? Connection);

/// <summary>A file read for import: the flow (secrets and flows still by name), what it needs, and what's wrong with it.</summary>
public sealed record PortRead(string? Name, string? Description, FlowDefinition? Definition, PortNeeds Needs, IReadOnlyList<string> Errors);

/// <summary>
/// Moving a flow between workspaces: export as a JSON file, import one, and duplicate into another workspace.
/// <para>An export never holds a secret, a sealed value or a connection token. Secrets are referred to by name
/// (<c>auth.secret</c> in place of <c>auth.secretId</c>), "Run another flow" by the flow's name (<c>flow</c> in place of
/// <c>flowId</c>), apps by the name of the connection they used; approvers (people) are left out; and any header, query
/// or form value whose name looks like a credential (<c>Authorization</c>, <c>X-Api-Key</c>, <c>token</c>…) is
/// emptied, with a note in <c>redacted</c>. A webhook trigger's address and signing secret stay behind: an import gets
/// its own.</para>
/// <para>An import is checked strictly: the file's shape (no unknown fields), its size, every trigger and action against
/// the catalog, every step type, step ids, nesting and the step limit. Imported flows always start switched off.</para>
/// </summary>
public static partial class FlowPorter
{
    public const string Format = "vantra-flow";
    public const int FormatVersion = 1;
    /// <summary>The largest file an import takes.</summary>
    public const int MaxFileBytes = 128 * 1024;
    private const int MaxNesting = 4;

    [GeneratedRegex("auth|token|secret|key|pass|pwd|cookie|signature|session|credential|bearer", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    /// <summary>A header, query or form field whose value may be a credential.</summary>
    public static bool IsSensitive(string name) => SensitiveName().IsMatch(name);

    private static readonly JsonSerializerOptions Strict = new(FlowDefinition.Json) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    // ── Export ──

    /// <summary>
    /// The file for a flow. <paramref name="secretNames"/>: the workspace's secrets (id → name); <paramref name="flowNames"/>:
    /// its flows (id → name); <paramref name="connections"/>: its connections (app → name).
    /// </summary>
    public static JsonObject Export(string name, string? description, FlowDefinition def, IReadOnlyDictionary<string, string> secretNames,
        IReadOnlyDictionary<string, string> flowNames, IReadOnlyDictionary<string, string> connections, DateTime now)
    {
        var copy = Clone(def);
        var redacted = new List<string>();
        var secrets = new SortedSet<string>(StringComparer.Ordinal);
        var flows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in copy.AllSteps())
        {
            var label = s.Name is { Length: > 0 } n ? n : s.Id;
            if (s.Config["auth"] is JsonObject auth && auth["secretId"] is { } sid)
            {
                auth.Remove("secretId");
                if (secretNames.TryGetValue(Tokens.Text(sid), out var sn)) { auth["secret"] = sn; secrets.Add(sn); }
            }
            foreach (var field in new[] { "headers", "query", "form" })
                if (s.Config[field] is JsonObject bag)
                    foreach (var key in bag.Select(kv => kv.Key).Where(IsSensitive).ToList())
                    {
                        if (Tokens.Text(bag[key]).Length == 0) continue;
                        bag[key] = "";
                        redacted.Add($"{label}: {key}");
                    }
            if (s.Type == StepTypes.Action && s.App == "flow" && s.Config["flowId"] is { } fid)
            {
                s.Config.Remove("flowId");
                if (flowNames.TryGetValue(Tokens.Text(fid), out var fn)) { s.Config["flow"] = fn; flows.Add(fn); }
            }
            if (s.Type == StepTypes.Approval && s.Config["approvers"] is JsonArray { Count: > 0 })
            {
                s.Config["approvers"] = new JsonArray();
                redacted.Add($"{label}: approvers");
            }
        }
        var apps = Apps(copy).Select(a => new JsonObject { ["app"] = a, ["connection"] = connections.TryGetValue(a, out var c) ? c : null }).ToArray();
        return new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["exportedAt"] = now.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
            ["flow"] = new JsonObject
            {
                ["name"] = name,
                ["description"] = description,
                ["definition"] = JsonSerializer.SerializeToNode(copy, FlowDefinition.Json),
            },
            ["needs"] = new JsonObject
            {
                ["apps"] = new JsonArray(apps.Cast<JsonNode?>().ToArray()),
                ["secrets"] = new JsonArray(secrets.Select(x => (JsonNode?)x).ToArray()),
                ["flows"] = new JsonArray(flows.Select(x => (JsonNode?)x).ToArray()),
            },
            ["redacted"] = new JsonArray(redacted.Select(x => (JsonNode?)x).ToArray()),
        };
    }

    /// <summary>The apps a flow uses that need a connection (Orbit, Projects and the outside apps), trigger first.</summary>
    public static IReadOnlyList<string> Apps(FlowDefinition def)
    {
        var all = new List<string>();
        if (def.Trigger.App.Length > 0) all.Add(def.Trigger.App);
        all.AddRange(def.AllSteps().Where(s => s.Type == StepTypes.Action && s.App is { Length: > 0 }).Select(s => s.App!));
        var needed = FlowCatalog.AppsNeeded(def).Concat(FlowCatalog.ProvidersNeeded(def)).ToHashSet(StringComparer.Ordinal);
        return all.Where(needed.Contains).Distinct().ToList();
    }

    // ── Import ──

    private static readonly HashSet<string> TopKeys = ["format", "version", "exportedAt", "flow", "needs", "redacted"];
    private static readonly HashSet<string> FlowKeys = ["name", "description", "definition"];

    /// <summary>The step types a flow may hold: an app's action, and every kind the catalog describes.</summary>
    public static IReadOnlySet<string> StepTypesKnown => FlowCatalog.Steps.Select(s => s.Type).Append(StepTypes.Action).ToHashSet(StringComparer.Ordinal);

    /// <summary>Reads an exported file, strictly. <paramref name="bytes"/>: its size as sent.</summary>
    public static PortRead Read(JsonNode? doc, int bytes, FlowLimits limits)
    {
        var errors = new List<string>();
        var none = new PortNeeds([], [], []);
        if (bytes > MaxFileBytes) return new(null, null, null, none, [$"That file is too large. The most is {MaxFileBytes / 1024} KB."]);
        if (doc is not JsonObject o) return new(null, null, null, none, ["That isn't a Vantra Flow export."]);
        if (Tokens.Text(o["format"]) != Format) return new(null, null, null, none, ["That isn't a Vantra Flow export."]);
        if (o["version"] is not JsonValue v || !v.TryGetValue<int>(out var ver) || ver != FormatVersion)
            return new(null, null, null, none, ["That export is from a newer version of Vantra Flow."]);
        foreach (var k in o.Select(kv => kv.Key).Where(k => !TopKeys.Contains(k))) errors.Add($"Unexpected field “{Short(k)}”.");
        if (o["flow"] is not JsonObject f) return new(null, null, null, none, [.. errors, "The file has no flow in it."]);
        foreach (var k in f.Select(kv => kv.Key).Where(k => !FlowKeys.Contains(k))) errors.Add($"Unexpected field “flow.{Short(k)}”.");

        var name = f["name"] is JsonValue nv && nv.TryGetValue<string>(out var ns) ? ns.Trim() : null;
        if (name is not { Length: > 0 and <= 160 }) errors.Add("The flow needs a name of up to 160 characters.");
        string? description = null;
        if (f["description"] is { } dn)
        {
            if (dn is JsonValue dv && dv.TryGetValue<string>(out var ds) && ds.Length <= 400) description = ds.Trim() is { Length: > 0 } d ? d : null;
            else errors.Add("The description must be text of up to 400 characters.");
        }

        if (f["definition"] is not JsonObject defNode) return new(name, description, null, none, [.. errors, "The file has no flow definition."]);
        if (Encoding.UTF8.GetByteCount(defNode.ToJsonString()) > FlowLimits.MaxConfigBytes) return new(name, description, null, none, [.. errors, "This flow is too large to import."]);
        FlowDefinition? def;
        try { def = defNode.Deserialize<FlowDefinition>(Strict); }
        catch (JsonException e) { return new(name, description, null, none, [.. errors, $"The flow definition can't be read{Where(e)}."]); }
        if (def is null) return new(name, description, null, none, [.. errors, "The file has no flow definition."]);

        CheckCatalog(def, limits, errors);
        return new(name, description, errors.Count == 0 ? def : null, NeedsOf(def), errors);
    }

    private static string Where(JsonException e) => e.Path is { Length: > 1 } p ? $" at {Short(p.TrimStart('$', '.'))}" : "";
    private static string Short(string s) => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>Every trigger, action and step type against the catalog; ids, nesting and the step limit.</summary>
    public static void CheckCatalog(FlowDefinition def, FlowLimits limits, List<string> errors)
    {
        var t = def.Trigger;
        if (t.App.Length > 0 || t.Event.Length > 0)
        {
            if (FlowCatalog.Trigger(t.App, t.Event) is null) errors.Add($"Vantra Flow has no trigger “{Short(t.App)} {Short(t.Event)}”.");
        }
        var known = StepTypesKnown;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        void Walk(List<StepDef>? steps, int depth)
        {
            if (steps is null) return;
            if (depth > MaxNesting) { errors.Add("Steps are nested too deeply."); return; }
            foreach (var s in steps)
            {
                count++;
                var label = $"Step “{Short(s.Name is { Length: > 0 } n ? n : s.Id)}”";
                if (!DefinitionRules.StepId().IsMatch(s.Id)) errors.Add($"{label} has an id Vantra Flow can't use.");
                else if (!seen.Add(s.Id)) errors.Add($"Two steps share the id “{s.Id}”.");
                if (!known.Contains(s.Type)) errors.Add($"{label} is a kind of step Vantra Flow doesn't have.");
                else if (s.Type == StepTypes.Action)
                {
                    if (FlowCatalog.Action(s.App, s.Action) is null) errors.Add($"{label} uses an action Vantra Flow doesn't have.");
                }
                else if (s.App is not null || s.Action is not null) errors.Add($"{label} can't name an app.");
                if (s.Paths is { Count: > PathRules.MaxPaths }) errors.Add($"{label} has more than {PathRules.MaxPaths} paths.");
                if (s.Then is not null && s.Type != StepTypes.Branch) errors.Add($"{label} can't hold steps that way.");
                if (s.Paths is not null && s.Type is not (StepTypes.Paths or StepTypes.Approval)) errors.Add($"{label} can't hold paths.");
                if (s.Do is not null && s.Type != StepTypes.Loop) errors.Add($"{label} can't repeat steps.");
                foreach (var side in s.Children()) Walk(side, depth + 1);
            }
        }
        Walk(def.Steps, 1);
        if (count > limits.MaxSteps) errors.Add($"A flow can have at most {limits.MaxSteps} steps.");
    }

    /// <summary>What an exported definition needs: its apps, its secrets by name and the flows it runs by name.</summary>
    public static PortNeeds NeedsOf(FlowDefinition def)
    {
        var secrets = new SortedSet<string>(StringComparer.Ordinal);
        var flows = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in def.AllSteps())
        {
            if (s.Config["auth"] is JsonObject auth && Tokens.Text(auth["secret"]) is { Length: > 0 } sn) secrets.Add(sn);
            if (s.Type == StepTypes.Action && s.App == "flow" && Tokens.Text(s.Config["flow"]) is { Length: > 0 } fn) flows.Add(fn);
        }
        return new PortNeeds(Apps(def).Select(a => new PortApp(a, null)).ToList(), secrets.ToList(), flows.ToList());
    }

    /// <summary>The connection names an export recorded for its apps (by app).</summary>
    public static IReadOnlyDictionary<string, string> ConnectionNames(JsonNode? doc)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (doc?["needs"]?["apps"] is JsonArray apps)
            foreach (var a in apps.OfType<JsonObject>())
                if (Tokens.Text(a["app"]) is { Length: > 0 } app && Tokens.Text(a["connection"]) is { Length: > 0 and <= 200 } c) map[app] = c;
        return map;
    }

    /// <summary>
    /// An exported definition made this workspace's: each secret name to the secret chosen for it (<paramref name="secretIds"/>,
    /// name → id; one left unmapped is left empty) and each flow name to this workspace's flow of that name. Returns what
    /// was left to choose again.
    /// </summary>
    public static (FlowDefinition Definition, IReadOnlyList<string> Unmapped) Bind(FlowDefinition portable, IReadOnlyDictionary<string, string> secretIds,
        IReadOnlyDictionary<string, string> flowIds)
    {
        var def = Clone(portable);
        var unmapped = new List<string>();
        foreach (var s in def.AllSteps())
        {
            var label = s.Name is { Length: > 0 } n ? n : s.Id;
            if (s.Config["auth"] is JsonObject auth && auth["secret"] is { } secret)
            {
                var sn = Tokens.Text(secret);
                auth.Remove("secret");
                auth.Remove("secretId");
                if (secretIds.TryGetValue(sn, out var id)) auth["secretId"] = id;
                else unmapped.Add($"{label}: choose the secret");
            }
            if (s.Type == StepTypes.Action && s.App == "flow" && s.Config["flow"] is { } flow)
            {
                var fn = Tokens.Text(flow);
                s.Config.Remove("flow");
                s.Config.Remove("flowId");
                if (flowIds.TryGetValue(fn, out var fid)) s.Config["flowId"] = fid;
                else unmapped.Add($"{label}: choose the flow to run");
            }
            if (s.Type == StepTypes.Approval && (s.Config["approvers"] as JsonArray)?.Count is null or 0) unmapped.Add($"{label}: choose who approves");
        }
        return (def, unmapped);
    }

    /// <summary>
    /// A flow copied into another workspace: its definition kept, its secrets matched by name to the target's, the flows
    /// it runs matched by name, approvers kept only if they're in the target too. The target's own connections are used.
    /// </summary>
    public static (FlowDefinition Definition, IReadOnlyList<string> Unmapped) Retarget(FlowDefinition def, IReadOnlyDictionary<string, string> sourceSecretNames,
        IReadOnlyDictionary<string, string> targetSecretIds, IReadOnlyDictionary<string, string> sourceFlowNames, IReadOnlyDictionary<string, string> targetFlowIds,
        IReadOnlySet<string> targetMembers)
    {
        var copy = Clone(def);
        foreach (var s in copy.AllSteps())
        {
            if (s.Config["auth"] is JsonObject auth && auth["secretId"] is { } sid)
            {
                auth.Remove("secretId");
                if (sourceSecretNames.TryGetValue(Tokens.Text(sid), out var name)) auth["secret"] = name;
            }
            if (s.Type == StepTypes.Action && s.App == "flow" && s.Config["flowId"] is { } fid)
            {
                s.Config.Remove("flowId");
                if (sourceFlowNames.TryGetValue(Tokens.Text(fid), out var fname)) s.Config["flow"] = fname;
            }
            if (s.Type == StepTypes.Approval && s.Config["approvers"] is JsonArray who)
            {
                var keep = who.Select(Tokens.Text).Where(targetMembers.Contains).ToList();
                s.Config["approvers"] = new JsonArray(keep.Select(x => (JsonNode?)x).ToArray());
            }
        }
        // A secret or flow the source had no name for (deleted there) is simply left unset.
        return Bind(copy, targetSecretIds, targetFlowIds);
    }

    private static FlowDefinition Clone(FlowDefinition d) =>
        JsonSerializer.Deserialize<FlowDefinition>(JsonSerializer.Serialize(d, FlowDefinition.Json), FlowDefinition.Json)!;
}
