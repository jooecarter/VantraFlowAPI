namespace VantraFlow.SqlCheck;

/// <summary>
/// What to put where the evaluator couldn't work out a value. Numbers and ids
/// are safe stand-ins (the SQL only ever embeds them as literals). Text that
/// the evaluator can't trace must be listed here by <c>file:expression</c>
/// with every value it can take, so a new dynamic piece of SQL is never
/// guessed at silently.
/// </summary>
public static class HoleValues
{
    private static readonly Dictionary<string, string[]> Known = new(StringComparer.Ordinal)
    {
    };

    public static string For(Hole h, List<Hole> unresolved)
    {
        var t = h.Type;
        if (t is Microsoft.CodeAnalysis.INamedTypeSymbol { OriginalDefinition.SpecialType: Microsoft.CodeAnalysis.SpecialType.System_Nullable_T, TypeArguments: [var u] }) t = u;
        switch (t?.SpecialType)
        {
            case Microsoft.CodeAnalysis.SpecialType.System_Int16 or Microsoft.CodeAnalysis.SpecialType.System_Int32
                or Microsoft.CodeAnalysis.SpecialType.System_Int64 or Microsoft.CodeAnalysis.SpecialType.System_Double
                or Microsoft.CodeAnalysis.SpecialType.System_Decimal or Microsoft.CodeAnalysis.SpecialType.System_Byte:
                return "1";
        }
        if (t?.ToDisplayString() == "System.Guid") return "00000000-0000-0000-0000-000000000000";
        if (Known.TryGetValue($"{h.File.Replace('\\', '/')}:{h.Expression}", out var v)) return v[0];
        unresolved.Add(h);
        return "";
    }
}
