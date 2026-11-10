using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace VantraFlow.SqlCheck;

/// <summary>The API's source compiled in-process (Roslyn), so SQL expressions can be bound to symbols.</summary>
public static class ApiSource
{
    // What the Web SDK's ImplicitUsings adds.
    private const string GlobalUsings = """
        global using System;
        global using System.Collections.Generic;
        global using System.IO;
        global using System.Linq;
        global using System.Net.Http;
        global using System.Net.Http.Json;
        global using System.Threading;
        global using System.Threading.Tasks;
        global using Microsoft.AspNetCore.Builder;
        global using Microsoft.AspNetCore.Hosting;
        global using Microsoft.AspNetCore.Http;
        global using Microsoft.AspNetCore.Routing;
        global using Microsoft.Extensions.Configuration;
        global using Microsoft.Extensions.DependencyInjection;
        global using Microsoft.Extensions.Hosting;
        global using Microsoft.Extensions.Logging;
        """;

    public static CSharpCompilation Compile(string apiDir)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Preview);
        var trees = Directory.GetFiles(apiDir, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal)
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), parse, f))
            .Append(CSharpSyntaxTree.ParseText(GlobalUsings, parse))
            .ToList();
        // Everything this tool runs with (the API's own references come along via
        // the project reference) except the API assembly itself, which is the source here.
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Where(p => !Path.GetFileName(p).StartsWith("VantraFlow.", StringComparison.Ordinal))
            .Select(p => MetadataReference.CreateFromFile(p));
        return CSharpCompilation.Create("VantraFlow.Api.SqlCheck", trees, refs,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
    }
}

/// <summary>Dapper calls that send SQL: the extension methods on <c>Dapper.SqlMapper</c>.</summary>
public static class DapperSites
{
    public static ExpressionSyntax? SqlArgument(InvocationExpressionSyntax inv, SemanticModel model)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m) return null;
        var def = m.ReducedFrom ?? m;
        if (def.ContainingType.ToDisplayString() != "Dapper.SqlMapper") return null;
        var ps = m.Parameters;   // the reduced form: without `this IDbConnection`
        var idx = ps.IndexOf(ps.FirstOrDefault(p => p.Name is "sql" or "command")!);
        if (idx < 0) return null;
        var name = ps[idx].Name;
        var args = inv.ArgumentList.Arguments;
        var arg = args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression
                  ?? (idx < args.Count ? args[idx].Expression : null);
        if (arg is null) return null;
        if (name == "command")
            return arg is BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [var first, ..] } ? first.Expression : arg;
        return arg;
    }
}

/// <summary>The parameters a Dapper call sends, when they're spelled out as an anonymous object.</summary>
public static class DapperParameters
{
    /// <summary>Name → whether Dapper expands it as a list; null when the object isn't visible (DynamicParameters, a dictionary, an <c>object</c> argument).</summary>
    public static Dictionary<string, bool>? Of(InvocationExpressionSyntax inv, SemanticModel model, Func<SyntaxTree, SemanticModel> models)
    {
        if (model.GetSymbolInfo(inv).Symbol is not IMethodSymbol m) return null;
        var ps = m.Parameters;
        var args = inv.ArgumentList.Arguments;
        ExpressionSyntax? Arg(string name)
        {
            var i = ps.IndexOf(ps.FirstOrDefault(p => p.Name == name)!);
            return i < 0 ? null : args.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == name)?.Expression
                                  ?? (i < args.Count && args[i].NameColon is null ? args[i].Expression : null);
        }
        var param = Arg("param");
        if (param is null && Arg("command") is BaseObjectCreationExpressionSyntax { ArgumentList: { } cargs })
        {
            param = cargs.Arguments.FirstOrDefault(a => a.NameColon?.Name.Identifier.ValueText == "parameters")?.Expression
                    ?? (cargs.Arguments.Count > 1 && cargs.Arguments[1].NameColon is null ? cargs.Arguments[1].Expression : null);
            if (param is null) return [];
        }
        if (param is null) return Arg("command") is null ? [] : null;
        return Resolve(param, models, 0);
    }

    private static Dictionary<string, bool>? Resolve(ExpressionSyntax e, Func<SyntaxTree, SemanticModel> models, int depth)
    {
        if (depth > 4) return null;
        var model = models(e.SyntaxTree);
        switch (e)
        {
            case AnonymousObjectCreationExpressionSyntax anon:
                var d = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                foreach (var init in anon.Initializers)
                {
                    var name = init.NameEquals?.Name.Identifier.ValueText ?? init.Expression switch
                    {
                        MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                        IdentifierNameSyntax id => id.Identifier.ValueText,
                        _ => null,
                    };
                    if (name is null) return null;
                    d[name] = IsList(model.GetTypeInfo(init.Expression).Type);
                }
                return d;
            case IdentifierNameSyntax when model.GetSymbolInfo(e).Symbol is ILocalSymbol l
                                           && l.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is VariableDeclaratorSyntax { Initializer.Value: var v }:
                return Resolve(v, models, depth + 1);
            case InvocationExpressionSyntax call when model.GetSymbolInfo(call).Symbol is IMethodSymbol target:
                // inserts.Select(InsertParameters): Dapper runs the command once per element.
                if (target.Name == "Select" && call.ArgumentList.Arguments is [var sel]
                    && model.GetSymbolInfo(sel.Expression).Symbol is IMethodSymbol selector)
                    return Body(selector, models, depth);
                return Body(target, models, depth);
        }
        return null;
    }

    private static Dictionary<string, bool>? Body(IMethodSymbol m, Func<SyntaxTree, SemanticModel> models, int depth) =>
        m.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is MethodDeclarationSyntax { ExpressionBody.Expression: var body }
            ? Resolve(body, models, depth + 1) : null;

    private static bool IsList(ITypeSymbol? t) =>
        t is not null && t.SpecialType != SpecialType.System_String
        && !(t is IArrayTypeSymbol { ElementType.SpecialType: SpecialType.System_Byte })
        && (t is IArrayTypeSymbol || t.AllInterfaces.Any(i => i.ToDisplayString() == "System.Collections.IEnumerable")
            || t.ToDisplayString() == "System.Collections.IEnumerable");
}

public static partial class Migrations
{
    /// <summary>Split a script into batches on <c>GO</c> lines, the way sqlcmd / SSMS do (and give each batch's first line).</summary>
    public static IEnumerable<(string Sql, int Line)> SplitBatches(string script)
    {
        var lines = script.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        var start = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            if (Go().IsMatch(lines[i]))
            {
                if (sb.ToString().Trim().Length > 0) yield return (sb.ToString(), start);
                sb.Clear();
                start = i + 2;
                continue;
            }
            sb.Append(lines[i]).Append('\n');
        }
        if (sb.ToString().Trim().Length > 0) yield return (sb.ToString(), start);
    }

    [GeneratedRegex(@"^\s*GO\s*(\d+\s*)?(--.*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex Go();
}
