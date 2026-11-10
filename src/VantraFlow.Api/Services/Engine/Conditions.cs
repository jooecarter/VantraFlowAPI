using System.Globalization;
using System.Text.Json.Nodes;

namespace VantraFlow.Api.Services.Engine;

/// <summary>One rule: a left side (usually a token), an operator, and a right side (a token or text).</summary>
public sealed record ConditionRule(string Left, string Op, string? Right);

/// <summary>Rules joined with <c>all</c> (and) or <c>any</c> (or).</summary>
public sealed record Condition(string Match, IReadOnlyList<ConditionRule> Rules);

/// <summary>
/// The conditions of a branch and of the "record matches" trigger: <c>{ match: all | any, rules: [{ left, op,
/// right }] }</c>. Both sides are rendered with <see cref="Tokens"/> first. Text compares without regard to case;
/// greater / less compare numbers when both sides are numbers, else dates, else text.
/// </summary>
public static class Conditions
{
    public static readonly IReadOnlySet<string> Ops = new HashSet<string>
    {
        "equals", "not_equals", "contains", "not_contains", "starts_with", "ends_with",
        "is_empty", "is_not_empty", "greater_than", "less_than", "is_true", "is_false",
    };

    public static Condition? Parse(JsonNode? node)
    {
        if (node is not JsonObject o || o["rules"] is not JsonArray rules) return null;
        var list = new List<ConditionRule>();
        foreach (var r in rules)
        {
            if (r is not JsonObject ro) continue;
            var op = ro["op"]?.GetValue<string>() ?? "";
            var left = Tokens.Text(ro["left"]);
            if (!Ops.Contains(op) || string.IsNullOrWhiteSpace(left)) continue;
            list.Add(new ConditionRule(left, op, ro["right"] is null ? null : Tokens.Text(ro["right"])));
        }
        var match = o["match"]?.GetValue<string>() == "any" ? "any" : "all";
        return new Condition(match, list);
    }

    public static bool Evaluate(Condition c, JsonNode? data)
    {
        if (c.Rules.Count == 0) return false;
        var results = c.Rules.Select(r => Test(Tokens.Render(r.Left, data), r.Op, r.Right is null ? null : Tokens.Render(r.Right, data)));
        return c.Match == "any" ? results.Any(x => x) : results.All(x => x);
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
