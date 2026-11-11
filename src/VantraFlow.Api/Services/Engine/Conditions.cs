using System.Globalization;
using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>One rule: a left side (usually a token), an operator, and a right side (a token or text).</summary>
public sealed record ConditionRule(string Left, string Op, string? Right);

/// <summary>
/// Rules joined with <c>all</c> (and) or <c>any</c> (or), and groups of rules (each with its own all / any) joined the
/// same way: "(A and B) or (C and D)".
/// </summary>
public sealed record Condition(string Match, IReadOnlyList<ConditionRule> Rules, IReadOnlyList<Condition>? Groups = null)
{
    /// <summary>How many rules in all, groups included.</summary>
    public int Count => Rules.Count + (Groups?.Sum(g => g.Count) ?? 0);
}

/// <summary>
/// The conditions of a branch, a path, a filter and the "record matches" trigger: <c>{ match: all | any, rules: [{ left, op,
/// right }], groups: [{ match, rules }] }</c> (groups are one level deep). Both sides are rendered with <see cref="Tokens"/> first. Text compares without regard to case;
/// greater / less compare numbers when both sides are numbers, else dates, else text.
/// </summary>
public static class Conditions
{
    public static readonly IReadOnlySet<string> Ops = new HashSet<string>
    {
        "equals", "not_equals", "contains", "not_contains", "starts_with", "ends_with",
        "is_empty", "is_not_empty", "greater_than", "less_than", "is_true", "is_false",
    };

    public const int MaxGroups = 10;

    public static Condition? Parse(JsonNode? node) => Parse(node, nested: false);

    private static Condition? Parse(JsonNode? node, bool nested)
    {
        if (node is not JsonObject o || (o["rules"] is not JsonArray && o["groups"] is not JsonArray)) return null;
        var list = new List<ConditionRule>();
        foreach (var r in o["rules"] as JsonArray ?? [])
        {
            if (r is not JsonObject ro) continue;
            var op = Tokens.Text(ro["op"]);
            var left = Tokens.Text(ro["left"]);
            if (!Ops.Contains(op) || string.IsNullOrWhiteSpace(left)) continue;
            list.Add(new ConditionRule(left, op, ro["right"] is null ? null : Tokens.Text(ro["right"])));
        }
        var groups = new List<Condition>();
        if (!nested)
            foreach (var g in (o["groups"] as JsonArray ?? []).Take(MaxGroups))
                if (Parse(g, nested: true) is { Rules.Count: > 0 } group) groups.Add(group);
        var match = Tokens.Text(o["match"]) == "any" ? "any" : "all";
        return new Condition(match, list, groups);
    }

    public static bool Evaluate(Condition c, JsonNode? data)
    {
        if (c.Count == 0) return false;
        var results = c.Rules.Select(r => Test(Tokens.Render(r.Left, data), r.Op, r.Right is null ? null : Tokens.Render(r.Right, data)))
            .Concat((c.Groups ?? []).Select(g => Evaluate(g, data)));
        return c.Match == "any" ? results.Any(x => x) : results.All(x => x);
    }

    /// <summary>The rules with both sides filled in, for the run log (groups as their own lists).</summary>
    public static JsonObject Explain(Condition? c, JsonNode? data)
    {
        JsonArray Rules(IEnumerable<ConditionRule> rules) => new(rules.Select(r => (JsonNode)new JsonObject
        {
            ["left"] = Tokens.Render(r.Left, data),
            ["op"] = r.Op,
            ["right"] = r.Right is null ? null : Tokens.Render(r.Right, data),
            ["passed"] = Test(Tokens.Render(r.Left, data), r.Op, r.Right is null ? null : Tokens.Render(r.Right, data)),
        }).ToArray());
        var o = new JsonObject { ["match"] = c?.Match ?? "all", ["rules"] = Rules(c?.Rules ?? []) };
        if (c?.Groups is { Count: > 0 } groups)
            o["groups"] = new JsonArray(groups.Select(g => (JsonNode)new JsonObject { ["match"] = g.Match, ["rules"] = Rules(g.Rules), ["passed"] = Evaluate(g, data) }).ToArray());
        return o;
    }

    public static bool Test(string left, string op, string? right)
    {
        var l = left.Trim();
        var r = (right ?? "").Trim();
        return op switch
        {
            "equals" => string.Equals(l, r, StringComparison.OrdinalIgnoreCase) || NumEq(l, r),
            "not_equals" => !(string.Equals(l, r, StringComparison.OrdinalIgnoreCase) || NumEq(l, r)),
            "contains" => r.Length > 0 && l.Contains(r, StringComparison.OrdinalIgnoreCase),
            "not_contains" => r.Length == 0 || !l.Contains(r, StringComparison.OrdinalIgnoreCase),
            "starts_with" => r.Length > 0 && l.StartsWith(r, StringComparison.OrdinalIgnoreCase),
            "ends_with" => r.Length > 0 && l.EndsWith(r, StringComparison.OrdinalIgnoreCase),
            "is_empty" => l.Length == 0 || l is "[]" or "{}" or "null",
            "is_not_empty" => !(l.Length == 0 || l is "[]" or "{}" or "null"),
            "greater_than" => Compare(l, r) > 0,
            "less_than" => Compare(l, r) < 0,
            "is_true" => l is "true" or "True" or "1" or "yes" or "Yes",
            "is_false" => l is "" or "false" or "False" or "0" or "no" or "No",
            _ => false,
        };
    }

    private static bool NumEq(string l, string r) =>
        double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
        && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && a == b;

    private static int? Compare(string l, string r)
    {
        if (double.TryParse(l, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(r, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return a.CompareTo(b);
        if (DateTime.TryParse(l, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var da)
            && DateTime.TryParse(r, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var db)) return da.CompareTo(db);
        if (l.Length == 0 || r.Length == 0) return null;
        return string.Compare(l, r, StringComparison.OrdinalIgnoreCase);
    }
}
