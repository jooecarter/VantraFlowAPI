namespace VantraFlow.Api.Services.Engine;

/// <summary>
/// Where a run is: a path to a step, e.g. <c>2</c> (the third top-level step), <c>2.t.0</c> (the first step on
/// that branch's "then" side) or <c>2.e.1</c>. Stored on the run (flow.Runs.Position) so a run waiting on a delay
/// or a retry picks up exactly there, on the definition it started with.
/// </summary>
public static class Positions
{
    public const string First = "0";

    public static StepDef? StepAt(FlowDefinition def, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var (list, index) = Locate(def, path);
        return list is not null && index >= 0 && index < list.Count ? list[index] : null;
    }

    /// <summary>The first step, or null for a flow with none.</summary>
    public static string? Start(FlowDefinition def) => def.Steps.Count > 0 ? First : null;

    /// <summary>
    /// The step after <paramref name="path"/>: its next sibling; at the end of a branch side, the step after the
    /// branch (and so on outwards); null at the end of the flow.
    /// </summary>
    public static string? After(FlowDefinition def, string path)
    {
        var parts = path.Split('.');
        while (true)
        {
            var (list, index) = Locate(def, string.Join('.', parts));
            if (list is null) return null;
            if (index + 1 < list.Count)
            {
                parts[^1] = (index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                return string.Join('.', parts);
            }
            if (parts.Length == 1) return null;
            parts = parts[..^2]; // up to the branch step itself
        }
    }

    /// <summary>Into a branch's side (<c>then</c> / <c>else</c>), or past it when that side has no steps.</summary>
    public static string? Enter(FlowDefinition def, string branchPath, bool then)
    {
        var step = StepAt(def, branchPath);
        var side = then ? step?.Then : step?.Else;
        return side is { Count: > 0 } ? $"{branchPath}.{(then ? "t" : "e")}.0" : After(def, branchPath);
    }

    private static (List<StepDef>? List, int Index) Locate(FlowDefinition def, string path)
    {
        var parts = path.Split('.');
        var list = def.Steps;
        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var idx)) return (null, -1);
            if (i == parts.Length - 1) return (list, idx);
            if (idx >= list.Count || i + 1 >= parts.Length - 1) return (null, -1);
            var step = list[idx];
            list = parts[i + 1] switch { "t" => step.Then ?? [], "e" => step.Else ?? [], _ => null! };
            if (list is null) return (null, -1);
            i++;
        }
        return (null, -1);
    }
}
