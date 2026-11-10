using System.Globalization;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VantraFlow.SqlCheck;

/// <summary>A value the evaluator couldn't work out, left in the text as a marker.</summary>
public sealed record Hole(string Expression, ITypeSymbol? Type, string File, int Line);

/// <summary>
/// Works out the exact text of a SQL string expression in the API's source:
/// literals, interpolations and <c>+</c>; constants (Roslyn folds them) and
/// <c>static readonly</c> fields (read by reflection from the built assembly, so
/// they're the real values); locals (every value assigned to them); the
/// <c>? :</c>, <c>??</c> and <c>switch</c> branches (all of them, so every
/// shape of the batch is checked); <c>string.Format</c> / <c>Join</c> /
/// <c>Concat</c>; and calls to the API's own helpers — static ones run by
/// reflection with the real arguments, the rest inlined with their parameters
/// bound. A parameter of the method that issues the query is resolved from
/// every caller. Anything else becomes a <see cref="Hole"/>.
/// </summary>
public sealed class SqlEvaluator
{
    private const int MaxVariants = 512;
    private const char HoleStart = '\u0001', HoleEnd = '\u0002', ArgSep = '\u0003',   // none can occur in SQL text
                       ChoiceStart = '\u0004', ChoiceEnd = '\u0005';

    private readonly CSharpCompilation compilation;
    private readonly Assembly api;
    private readonly string root;
    private readonly Dictionary<SyntaxTree, SemanticModel> models = [];
    private Dictionary<IMethodSymbol, List<(InvocationExpressionSyntax Call, SemanticModel Model)>>? callers;

    public readonly List<Hole> Holes = [];
    /// <summary>Fields read while evaluating the current site (for fragment coverage).</summary>
    public HashSet<IFieldSymbol> FieldsUsed { get; private set; } = new(SymbolEqualityComparer.Default);
    /// <summary>Helpers that were run by reflection, with the arguments used.</summary>
    public readonly SortedSet<string> ReflectionCalls = new(StringComparer.Ordinal);
    public bool Truncated { get; private set; }

    public SqlEvaluator(CSharpCompilation compilation, Assembly api, string root)
    {
        this.compilation = compilation;
        this.api = api;
        this.root = root;
    }

    public SemanticModel Model(SyntaxTree t)
    {
        if (!models.TryGetValue(t, out var m)) models[t] = m = compilation.GetSemanticModel(t);
        return m;
    }

    public void BeginSite()
    {
        FieldsUsed = new(SymbolEqualityComparer.Default);
        Truncated = false;
        choices.Clear();
        choiceIds.Clear();
    }

    // A local read twice in one batch has the same value both times (and the two
    // halves of `var (col, val) = c ? (a, b) : (x, y)` come from the same branch):
    // each read is a marker for one shared choice, expanded once per option at the end.
    private readonly List<List<string[]>> choices = [];
    private readonly Dictionary<(SyntaxNode, Env), int> choiceIds = [];

    private string Choice(SyntaxNode key, Env env, Func<List<string[]>> options, int index)
    {
        if (!choiceIds.TryGetValue((key, env), out var id))
        {
            var o = options();
            id = choices.Count;
            choices.Add(o);
            choiceIds[(key, env)] = id;
        }
        return $"{ChoiceStart}{id}:{index}{ChoiceEnd}";
    }

    private List<string> Expand(IEnumerable<string> values)
    {
        var done = new List<string>();
        var todo = new Stack<string>(values);
        while (todo.Count > 0 && done.Count < MaxVariants)
        {
            var s = todo.Pop();
            var i = s.IndexOf(ChoiceStart);
            if (i < 0) { done.Add(s); continue; }
            var id = int.Parse(s[(i + 1)..s.IndexOf(':', i)], CultureInfo.InvariantCulture);
            foreach (var option in choices[id])
            {
                var x = s;
                for (var k = 0; k < option.Length; k++) x = x.Replace($"{ChoiceStart}{id}:{k}{ChoiceEnd}", option[k]);
                todo.Push(x);
            }
        }
        if (todo.Count > 0) Truncated = true;
        return done.Distinct().ToList();
    }

    public static bool HasHoles(string s) => s.Contains(HoleStart);

    /// <summary>Replace each hole marker with <paramref name="fill"/>(hole).</summary>
    public string Fill(string s, Func<Hole, string> fill)
    {
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] != HoleStart) { sb.Append(s[i]); continue; }
            var end = s.IndexOf(HoleEnd, i);
            sb.Append(fill(Holes[int.Parse(s[(i + 1)..end], CultureInfo.InvariantCulture)]));
            i = end;
        }
        return sb.ToString();
    }

    public IEnumerable<Hole> HolesIn(string s)
    {
        for (var i = s.IndexOf(HoleStart); i >= 0; i = s.IndexOf(HoleStart, i + 1))
            yield return Holes[int.Parse(s[(i + 1)..s.IndexOf(HoleEnd, i)], CultureInfo.InvariantCulture)];
    }

    private sealed class Env
    {
        public readonly Dictionary<ISymbol, List<string>> Bound = new(SymbolEqualityComparer.Default);
        public int Depth;
        /// <summary>Resolve an unbound parameter from the method's callers (only for the query's own method chain).</summary>
        public bool ExpandCallers = true;
    }

    public List<string> EvaluateSite(ExpressionSyntax e) => Expand(Eval(e, new Env()));

    private string NewHole(ExpressionSyntax e, SemanticModel m)
    {
        var span = e.GetLocation().GetLineSpan();
        Holes.Add(new Hole(e.ToString(), m.GetTypeInfo(e).Type, Path.GetRelativePath(root, span.Path), span.StartLinePosition.Line + 1));
        return $"{HoleStart}{Holes.Count - 1}{HoleEnd}";
    }

    private List<string> Product(IEnumerable<List<string>> parts)
    {
        var acc = new List<string> { "" };
        foreach (var p in parts)
        {
            var next = new List<string>();
            foreach (var a in acc)
            foreach (var b in p)
            {
                if (next.Count >= MaxVariants) { Truncated = true; break; }
                next.Add(a + b);
            }
            acc = next.Distinct().ToList();
        }
        return acc;
    }

    private List<string> Union(params List<string>[] lists)
    {
        var r = lists.SelectMany(x => x).Distinct().ToList();
        if (r.Count > MaxVariants) { Truncated = true; r = r.Take(MaxVariants).ToList(); }
        return r;
    }

    private static string Format(object? v) => v switch
    {
        null => "",
        bool b => b ? "True" : "False",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    private void NoteFields(SyntaxNode e, SemanticModel m)
    {
        foreach (var n in e.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            if (m.GetSymbolInfo(n).Symbol is IFieldSymbol f) NoteField(f);
    }

    /// <summary>A field, and every field its initializer is built from (a const folded into another const).</summary>
    private void NoteField(IFieldSymbol f)
    {
        if (!FieldsUsed.Add(f)) return;
        if (Initializer(f) is { } init) NoteFields(init, Model(init.SyntaxTree));
    }

    private List<string> Eval(ExpressionSyntax e, Env env)
    {
        var m = Model(e.SyntaxTree);
        if (env.Depth > 12) return [NewHole(e, m)];

        var c = m.GetConstantValue(e);
        if (c.HasValue)
        {
            NoteFields(e, m);
            return [Format(c.Value)];
        }

        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return Eval(p.Expression, env);
            case CastExpressionSyntax cast: return Eval(cast.Expression, env);
            case AwaitExpressionSyntax aw: return Eval(aw.Expression, env);
            case PostfixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.SuppressNullableWarningExpression } bang: return Eval(bang.Operand, env);
            case ConditionalExpressionSyntax ce: return Union(Eval(ce.WhenTrue, env), Eval(ce.WhenFalse, env));
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.CoalesceExpression } co: return Union(Eval(co.Left, env), Eval(co.Right, env));
            case BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } add
                when m.GetTypeInfo(add).Type?.SpecialType == SpecialType.System_String:
                return Product([Eval(add.Left, env), Eval(add.Right, env)]);
            case SwitchExpressionSyntax sw: return Union(sw.Arms.Select(a => Eval(a.Expression, env)).ToArray());
            case InterpolatedStringExpressionSyntax interp: return EvalInterpolated(interp, env);
            case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression }: return [""];
            case InvocationExpressionSyntax inv: return EvalInvocation(inv, env, m);
            case IdentifierNameSyntax or MemberAccessExpressionSyntax or QualifiedNameSyntax:
                return EvalSymbol(e, env, m);
        }
        return [NewHole(e, m)];
    }

    private List<string> EvalInterpolated(InterpolatedStringExpressionSyntax s, Env env)
    {
        var parts = new List<List<string>>();
        foreach (var c in s.Contents)
        {
            if (c is InterpolatedStringTextSyntax t) { parts.Add([t.TextToken.ValueText]); continue; }
            var i = (InterpolationSyntax)c;
            var m = Model(i.SyntaxTree);
            var cv = m.GetConstantValue(i.Expression);
            if (cv.HasValue && i.FormatClause is not null && cv.Value is IFormattable f)
            {
                NoteFields(i.Expression, m);
                parts.Add([f.ToString(i.FormatClause.FormatStringToken.ValueText, CultureInfo.InvariantCulture)]);
            }
            else parts.Add(Eval(i.Expression, env));
        }
        return Product(parts);
    }

    private List<string> EvalSymbol(ExpressionSyntax e, Env env, SemanticModel m)
    {
        var sym = m.GetSymbolInfo(e).Symbol;
        switch (sym)
        {
            case IFieldSymbol f:
                NoteField(f);
                if (f.IsStatic && Reflect(f) is { } fv) return [fv];
                if (Initializer(f) is { } fi) return Eval(fi, Deeper(env));
                break;
            case IPropertySymbol p:
                if (p.IsStatic && Reflect(p) is { } pv) return [pv];
                if (PropertyBody(p) is { } pb) return Eval(pb, Deeper(env));
                break;
            case ILocalSymbol l:
                return EvalLocal(l, env, e);
            case IParameterSymbol prm:
                if (env.Bound.TryGetValue(prm, out var bound)) return bound;
                if (env.ExpandCallers && prm.ContainingSymbol is IMethodSymbol owner && FromCallers(owner, prm, env) is { Count: > 0 } fromCallers)
                    return fromCallers;
                break;
        }
        return [NewHole(e, m)];
    }

    private static Env Deeper(Env env, bool keepBindings = false)
    {
        var n = new Env { Depth = env.Depth + 1, ExpandCallers = false };
        if (keepBindings) foreach (var kv in env.Bound) n.Bound[kv.Key] = kv.Value;
        return n;
    }

    private static ExpressionSyntax? Initializer(IFieldSymbol f) =>
        f.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<VariableDeclaratorSyntax>().FirstOrDefault()?.Initializer?.Value;

    private static ExpressionSyntax? PropertyBody(IPropertySymbol p)
    {
        var d = p.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault();
        if (d is null) return null;
        if (d.ExpressionBody is not null) return d.ExpressionBody.Expression;
        if (d.Initializer is not null) return d.Initializer.Value;
        var get = d.AccessorList?.Accessors.FirstOrDefault(a => a.IsKind(SyntaxKind.GetAccessorDeclaration));
        return get?.ExpressionBody?.Expression
            ?? (get?.Body?.Statements.Count == 1 && get.Body.Statements[0] is ReturnStatementSyntax r ? r.Expression : null);
    }

    // ── reflection: the real values from the built API assembly ─────────────

    private Type? ReflectType(INamedTypeSymbol t)
    {
        var name = t.MetadataName;
        for (var outer = t.ContainingType; outer is not null; outer = outer.ContainingType)
            name = outer.MetadataName + "+" + name;
        var ns = t.ContainingNamespace.IsGlobalNamespace ? "" : t.ContainingNamespace.ToDisplayString() + ".";
        return api.GetType(ns + name);
    }

    private string? Reflect(IFieldSymbol f)
    {
        try
        {
            var fi = ReflectType(f.ContainingType)?.GetField(f.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return fi?.GetValue(null) is string s ? s : null;
        }
        catch { return null; }
    }

    private string? Reflect(IPropertySymbol p)
    {
        try
        {
            var pi = ReflectType(p.ContainingType)?.GetProperty(p.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            return pi?.GetValue(null) is string s ? s : null;
        }
        catch { return null; }
    }

    // ── locals ───────────────────────────────────────────────────────────────

    private List<string> EvalLocal(ILocalSymbol l, Env env, ExpressionSyntax at)
    {
        var decl = l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        var m = Model(at.SyntaxTree);
        if (decl is null) return [NewHole(at, m)];

        // The assignments that can have run before this read.
        var scope = decl.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or AccessorDeclarationSyntax);
        var assignments = scope is null ? [] : scope.DescendantNodes().OfType<AssignmentExpressionSyntax>()
            .Where(a => a.SpanStart > decl.Span.End && a.SpanStart < at.SpanStart
                        && SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(a.Left).Symbol, l))
            .OrderBy(a => a.SpanStart).ToList();

        if (decl is SingleVariableDesignationSyntax d && d.Parent is ParenthesizedVariableDesignationSyntax pvd
            && pvd.Parent is DeclarationExpressionSyntax de && de.Parent is AssignmentExpressionSyntax da)
        {
            var index = pvd.Variables.IndexOf(d);
            if (assignments.Count == 0 && TupleOptions(da.Right, pvd.Variables.Count, env) is not null)
                return [Choice(da, env, () => TupleOptions(da.Right, pvd.Variables.Count, env)!, index)];
            return Values(EvalTupleElement(da.Right, index, env));
        }
        if (decl is VariableDeclaratorSyntax vd)
            return Values(vd.Initializer is { } init ? Eval(init.Value, env) : []);
        return [NewHole(at, m)];

        List<string> Values(List<string> initial)
        {
            var key = (SyntaxNode?)assignments.LastOrDefault() ?? decl;
            return [Choice(key, env, () => Assigned(initial).Select(v => new[] { v }).ToList(), 0)];
        }

        List<string> Assigned(List<string> values)
        {
            // Every later assignment: one in the declaring block's own statement list
            // always runs (it replaces); one nested in an if / loop may or may not (union).
            var declStatement = decl.Ancestors().OfType<StatementSyntax>().FirstOrDefault();
            foreach (var a in assignments)
            {
                var stmt = a.Parent as ExpressionStatementSyntax;
                var definite = stmt is not null && stmt.Parent == declStatement?.Parent;
                var rhs = Eval(a.Right, env);
                var assigned = a.Kind() switch
                {
                    SyntaxKind.SimpleAssignmentExpression => rhs,
                    SyntaxKind.AddAssignmentExpression => Product([values, rhs]),
                    SyntaxKind.CoalesceAssignmentExpression => Union(values, rhs),
                    _ => [NewHole(a.Right, m)],
                };
                values = definite ? assigned : Union(values, assigned);
            }
            return values.Count > 0 ? values : [NewHole(at, m)];
        }
    }

    /// <summary>Each way a tuple expression can come out, as its elements (null when it isn't a tuple / conditional of tuples).</summary>
    private List<string[]>? TupleOptions(ExpressionSyntax e, int arity, Env env)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p: return TupleOptions(p.Expression, arity, env);
            case ConditionalExpressionSyntax c:
                var t = TupleOptions(c.WhenTrue, arity, env);
                var f = TupleOptions(c.WhenFalse, arity, env);
                return t is null || f is null ? null : t.Concat(f).ToList();
            case TupleExpressionSyntax tuple when tuple.Arguments.Count == arity:
                return Product(tuple.Arguments.Select(a => Eval(a.Expression, env).Select(x => x + ArgSep).ToList()))
                    .Select(x => x.Split(ArgSep)[..^1]).ToList();
            default: return null;
        }
    }

    private List<string> EvalTupleElement(ExpressionSyntax e, int index, Env env) => e switch
    {
        ParenthesizedExpressionSyntax p => EvalTupleElement(p.Expression, index, env),
        ConditionalExpressionSyntax c => Union(EvalTupleElement(c.WhenTrue, index, env), EvalTupleElement(c.WhenFalse, index, env)),
        TupleExpressionSyntax t => Eval(t.Arguments[index].Expression, env),
        _ => [NewHole(e, Model(e.SyntaxTree))],
    };

    // ── calls ────────────────────────────────────────────────────────────────

    private List<string> EvalInvocation(InvocationExpressionSyntax inv, Env env, SemanticModel m)
    {
        if (m.GetSymbolInfo(inv).Symbol is not IMethodSymbol method) return [NewHole(inv, m)];
        var args = inv.ArgumentList.Arguments;

        if (method.ContainingType.SpecialType == SpecialType.System_String)
        {
            switch (method.Name)
            {
                case "Format" when args.Count >= 1 && method.Parameters[0].Type.SpecialType == SpecialType.System_String:
                {
                    var fmts = Eval(args[0].Expression, env);
                    var rest = args.Skip(1).Select(a => Eval(a.Expression, env)).ToList();
                    var combos = Product(rest.Select(r => r.Select(x => x + ArgSep).ToList()))
                        .Select(s => s.Split(ArgSep)[..^1]).ToList();
                    return fmts.SelectMany(f => combos.Select(cb => string.Format(CultureInfo.InvariantCulture, f, cb.Cast<object>().ToArray()))).Distinct().ToList();
                }
                case "Join" when args.Count == 2:
                    return EvalJoin(args[0].Expression, args[1].Expression, env, m);
                case "Concat":
                    return Product(args.Select(a => Eval(a.Expression, env)));
                case "Trim" or "TrimEnd" or "TrimStart" or "ToString" when inv.Expression is MemberAccessExpressionSyntax ma && args.Count == 0:
                {
                    var recv = Eval(ma.Expression, env);
                    return recv.Select(r => method.Name switch { "Trim" => r.Trim(), "TrimEnd" => r.TrimEnd(), "TrimStart" => r.TrimStart(), _ => r }).ToList();
                }
                case "Replace" when inv.Expression is MemberAccessExpressionSyntax ma2 && args.Count == 2:
                {
                    var recv = Eval(ma2.Expression, env);
                    var a = Eval(args[0].Expression, env); var b = Eval(args[1].Expression, env);
                    return recv.SelectMany(r => a.SelectMany(x => b.Select(y => r.Replace(x, y)))).Distinct().ToList();
                }
            }
            return [NewHole(inv, m)];
        }

        var def = method.ReducedFrom ?? method;
        var syntax = def.DeclaringSyntaxReferences.Select(r => r.GetSyntax()).FirstOrDefault();
        if (syntax is null || !ReturnsString(def)) return [NewHole(inv, m)];

        // Bind each parameter to the argument's values (or its default).
        var bindings = new Dictionary<IParameterSymbol, List<string>>(SymbolEqualityComparer.Default);
        var receiverArg = method.ReducedFrom is not null && inv.Expression is MemberAccessExpressionSyntax rma ? rma.Expression : null;
        var ps = def.Parameters;
        for (var i = 0; i < ps.Length; i++)
        {
            ExpressionSyntax? a = null;
            if (receiverArg is not null && i == 0) a = receiverArg;
            else
            {
                var pos = receiverArg is not null ? i - 1 : i;
                a = args.FirstOrDefault(x => x.NameColon?.Name.Identifier.ValueText == ps[i].Name)?.Expression
                    ?? (pos < args.Count && args[pos].NameColon is null ? args[pos].Expression : null);
            }
            if (a is not null)
            {
                var v = IsTextual(ps[i].Type) ? Expand(Eval(a, env)) : [NewHole(a, Model(a.SyntaxTree))];
                // An unknown flag (e.g. "has db/009 run?"): try both.
                if (ps[i].Type.SpecialType == SpecialType.System_Boolean && v.All(HasHoles)) v = ["True", "False"];
                bindings[ps[i]] = v;
            }
            else if (ps[i].HasExplicitDefaultValue) bindings[ps[i]] = [Format(ps[i].ExplicitDefaultValue)];
        }

        // A static helper whose arguments are all known: run the real code.
        if (def.IsStatic && ps.All(p => IsTextual(p.Type) && bindings.TryGetValue(p, out var b) && b.All(x => !HasHoles(x))))
        {
            var mi = FindMethod(def);
            if (mi is not null)
            {
                var results = new List<string>();
                var combos = Product(ps.Select(p => bindings[p].Select(x => x + ArgSep).ToList())).Select(s => s.Split(ArgSep)[..^1]);
                var ok = true;
                foreach (var cb in combos)
                {
                    try
                    {
                        var argv = ps.Select((p, i) => Convert(cb[i], p.Type)).ToArray();
                        var r = mi.Invoke(null, argv);
                        if (r is Task<string> ts) r = ts.GetAwaiter().GetResult();
                        if (r is string s) { results.Add(s); ReflectionCalls.Add($"{def.ContainingType.Name}.{def.Name}({string.Join(", ", cb.Select(x => Short(x)))})"); }
                        else ok = false;
                    }
                    catch { ok = false; }
                }
                if (ok && results.Count > 0) return results.Distinct().ToList();
            }
        }

        // Otherwise inline: every `return` (or the expression body) with the parameters bound.
        var inner = new Env { Depth = env.Depth + 1, ExpandCallers = false };
        foreach (var kv in bindings) inner.Bound[kv.Key] = kv.Value;
        var returns = ReturnExpressions(syntax).ToList();
        if (returns.Count == 0) return [NewHole(inv, m)];
        return Union(returns.Select(r => Eval(r, inner)).ToArray());
    }

    private static string Short(string s) => s.Length <= 40 ? $"\"{s}\"" : $"\"{s[..20].Replace("\n", " ").Trim()}…\" ({s.Length} chars)";

    private static bool ReturnsString(IMethodSymbol m) =>
        m.ReturnType.SpecialType == SpecialType.System_String
        || m.ReturnType is INamedTypeSymbol { Name: "Task" or "ValueTask", TypeArguments: [{ SpecialType: SpecialType.System_String }] };

    private static bool IsTextual(ITypeSymbol t) =>
        t.SpecialType is SpecialType.System_String or SpecialType.System_Boolean or SpecialType.System_Int32 or SpecialType.System_Int64
        || (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var u] } && IsTextual(u));

    private static object? Convert(string v, ITypeSymbol t)
    {
        if (t is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var u] }) t = u;
        return t.SpecialType switch
        {
            SpecialType.System_Boolean => v == "True",
            SpecialType.System_Int32 => int.Parse(v, CultureInfo.InvariantCulture),
            SpecialType.System_Int64 => long.Parse(v, CultureInfo.InvariantCulture),
            _ => v,
        };
    }

    private MethodInfo? FindMethod(IMethodSymbol def)
    {
        var t = ReflectType(def.ContainingType);
        return t?.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FirstOrDefault(x => x.Name == def.Name && x.GetParameters().Length == def.Parameters.Length
                                 && x.GetParameters().Select(p => p.Name).SequenceEqual(def.Parameters.Select(p => p.Name)));
    }

    private static IEnumerable<ExpressionSyntax> ReturnExpressions(SyntaxNode decl)
    {
        var (exprBody, block) = decl switch
        {
            MethodDeclarationSyntax md => (md.ExpressionBody?.Expression, (SyntaxNode?)md.Body),
            LocalFunctionStatementSyntax lf => (lf.ExpressionBody?.Expression, lf.Body),
            _ => (null, null),
        };
        if (exprBody is not null) { yield return exprBody; yield break; }
        if (block is null) yield break;
        foreach (var r in block.DescendantNodes(n => n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                     .OfType<ReturnStatementSyntax>())
            if (r.Expression is not null) yield return r.Expression;
    }

    private List<string> EvalJoin(ExpressionSyntax sepExpr, ExpressionSyntax collExpr, Env env, SemanticModel m)
    {
        var seps = Eval(sepExpr, env);
        List<List<string>>? items = null;
        if (m.GetSymbolInfo(collExpr).Symbol is ILocalSymbol l
            && l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax vd)
        {
            items = [];
            var scope = vd.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax);
            if (vd.Initializer?.Value is BaseObjectCreationExpressionSyntax { Initializer: { } oi })
                items.AddRange(oi.Expressions.Select(x => Eval(x, env)));
            foreach (var call in scope?.DescendantNodes().OfType<InvocationExpressionSyntax>() ?? [])
                if (call.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Add" } ma
                    && SymbolEqualityComparer.Default.Equals(m.GetSymbolInfo(ma.Expression).Symbol, l)
                    && call.ArgumentList.Arguments.Count == 1)
                    items.Add(Eval(call.ArgumentList.Arguments[0].Expression, env));
        }
        else if (collExpr is CollectionExpressionSyntax ce)
            items = ce.Elements.OfType<ExpressionElementSyntax>().Select(x => Eval(x.Expression, env)).ToList();
        else if (collExpr is ImplicitArrayCreationExpressionSyntax ia)
            items = ia.Initializer.Expressions.Select(x => Eval(x, env)).ToList();
        else if (collExpr is ArrayCreationExpressionSyntax { Initializer: { } ai })
            items = ai.Expressions.Select(x => Eval(x, env)).ToList();

        if (items is null || items.Count == 0) return [NewHole(collExpr, m)];
        // Every item on its own, and all of them together.
        var result = new List<string>();
        foreach (var sep in seps)
        {
            result.Add(string.Join(sep, items.Select(i => i[0])));
            result.AddRange(items.SelectMany(i => i));
        }
        return result.Distinct().ToList();
    }

    // ── callers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Every call in the source to a static method named <c>…Sql</c> that returns
    /// text: run for real (by reflection) with each argument the caller passes.
    /// </summary>
    public IEnumerable<(string Helper, string Where, List<string> Results)> RunSqlHelpers()
    {
        callers ??= IndexCalls();
        foreach (var (method, calls) in callers)
        {
            if (!method.IsStatic || !method.Name.EndsWith("Sql", StringComparison.Ordinal) || !ReturnsString(method)
                || method.DeclaringSyntaxReferences.Length == 0) continue;
            foreach (var (call, m) in calls)
            {
                var span = call.GetLocation().GetLineSpan();
                BeginSite();
                yield return ($"{method.ContainingType.Name}.{method.Name}", $"{Path.GetRelativePath(root, span.Path)}:{span.StartLinePosition.Line + 1}",
                              Expand(EvalInvocation(call, new Env(), m)));
            }
        }
    }

    private List<string>? FromCallers(IMethodSymbol owner, IParameterSymbol prm, Env env)
    {
        callers ??= IndexCalls();
        if (!callers.TryGetValue(owner.OriginalDefinition, out var calls) || env.Depth > 4) return null;
        var idx = owner.Parameters.IndexOf(prm);
        var all = new List<List<string>>();
        foreach (var (call, cm) in calls)
        {
            var a = call.ArgumentList.Arguments.FirstOrDefault(x => x.NameColon?.Name.Identifier.ValueText == prm.Name)?.Expression
                    ?? (idx < call.ArgumentList.Arguments.Count ? call.ArgumentList.Arguments[idx].Expression : null);
            if (a is null) continue;
            all.Add(Eval(a, new Env { Depth = env.Depth + 1, ExpandCallers = true }));
        }
        return all.Count == 0 ? null : Union(all.ToArray());
    }

    private Dictionary<IMethodSymbol, List<(InvocationExpressionSyntax, SemanticModel)>> IndexCalls()
    {
        var d = new Dictionary<IMethodSymbol, List<(InvocationExpressionSyntax, SemanticModel)>>(SymbolEqualityComparer.Default);
        foreach (var t in compilation.SyntaxTrees)
        {
            var m = Model(t);
            foreach (var inv in t.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
                if (m.GetSymbolInfo(inv).Symbol is IMethodSymbol s)
                {
                    var key = (s.ReducedFrom ?? s).OriginalDefinition;
                    if (!d.TryGetValue(key, out var list)) d[key] = list = [];
                    list.Add((inv, m));
                }
        }
        return d;
    }
}
