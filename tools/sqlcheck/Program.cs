using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using VantraFlow.SqlCheck;

// tools/sqlcheck — parses every SQL batch the API can send, and every db/*.sql
// migration, with Microsoft's T-SQL parser (ScriptDom) at levels 170 and 160
// (Azure SQL). Fails on a syntax error or an unbracketed reserved keyword used
// as an identifier. See tools/sqlcheck/README.md.
//
//   dotnet run --project tools/sqlcheck [-- [--warnings] [--verbose] [--dump <dir>] [--file <x.sql>]]
//
// It checks the checkout it was built from: static SQL is read from that build
// of the API (by reflection), next to the source it compiles with Roslyn.

var opts = Options.Parse(args);
if (opts.Files.Count > 0)
{
    // Ad hoc: check the given .sql files (GO-separated), e.g. a migration before it's committed.
    var adhoc = opts.Files.SelectMany(f => Migrations.SplitBatches(File.ReadAllText(f)).SelectMany(b => SqlLint.Check(SqlLint.ApplyDapperRewrites(b.Sql), $"{f} (batch at line {b.Line})"))).ToList();
    foreach (var f in adhoc) Console.WriteLine($"{f.Severity,-5} {f.Where}: {f.Message}");
    return adhoc.Any(f => f.Severity == "ERROR") ? 1 : 0;
}
var findings = new List<Finding>();
int batches = 0, sites = 0, sitesWithParams = 0;

// ── 1. Migrations ─────────────────────────────────────────────────────────
var migrations = Directory.GetFiles(Path.Combine(opts.Repo, "db"), "*.sql").Order(StringComparer.Ordinal).ToList();
foreach (var file in migrations)
{
    var rel = Path.GetRelativePath(opts.Repo, file);
    foreach (var (sql, line) in Migrations.SplitBatches(File.ReadAllText(file)))
    {
        batches++;
        findings.AddRange(SqlLint.Check(sql, $"{rel} (batch at line {line})"));
        opts.Dump($"{Path.GetFileNameWithoutExtension(file)}_L{line}", sql);
    }
}

// ── 2. The API's SQL, as sent ─────────────────────────────────────────────
var apiDir = Path.Combine(opts.Repo, "src", "VantraFlow.Api");
var compilation = ApiSource.Compile(apiDir);
// CS8795: a [GeneratedRegex] partial whose body the (not run here) source generator writes.
var compileErrors = compilation.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error && d.Id != "CS8795").ToList();
if (compileErrors.Count > 0)
{
    // Unresolved symbols would silently turn SQL into holes; refuse instead.
    foreach (var d in compileErrors.Take(10)) Console.WriteLine($"COMPILE {d}");
    Console.WriteLine($"sqlcheck: the API source didn't compile in-process ({compileErrors.Count} errors) — fix the build first.");
    return 2;
}
var api = Assembly.Load("VantraFlow.Api");
var eval = new SqlEvaluator(compilation, api, opts.Repo);
var fieldCoverage = new Dictionary<IFieldSymbol, int>(SymbolEqualityComparer.Default);

foreach (var tree in compilation.SyntaxTrees.Where(t => t.FilePath.Length > 0))
{
    var model = eval.Model(tree);
    foreach (var inv in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
    {
        if (DapperSites.SqlArgument(inv, model) is not { } sqlExpr) continue;
        sites++;
        var span = inv.GetLocation().GetLineSpan();
        var method = inv.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() switch
        {
            MethodDeclarationSyntax md => md.Identifier.ValueText,
            ConstructorDeclarationSyntax => ".ctor",
            _ => "?",
        };
        var where = $"{Path.GetRelativePath(opts.Repo, span.Path)}:{span.StartLinePosition.Line + 1} {method}";

        var parameters = DapperParameters.Of(inv, model, eval.Model);
        if (parameters is not null) sitesWithParams++;
        eval.BeginSite();
        var variants = eval.EvaluateSite(sqlExpr);
        if (eval.Truncated) findings.Add(new("WARN", where, "more than 512 variants; only the first 512 were checked"));
        var siteOk = true;
        var n = 0;
        foreach (var raw in variants)
        {
            n++;
            var unresolved = new List<Hole>();
            var filled = eval.Fill(raw, h => HoleValues.For(h, unresolved));
            if (unresolved.Count > 0)
            {
                siteOk = false;
                foreach (var h in unresolved.DistinctBy(h => h.Expression))
                    findings.Add(new("ERROR", where, $"can't tell what text `{h.Expression}` ({h.Type?.ToDisplayString() ?? "?"}, {h.File}:{h.Line}) puts into the SQL — add it to HoleValues in tools/sqlcheck/HoleValues.cs"));
                continue;
            }
            var sql = SqlLint.ApplyDapperRewrites(filled, parameters);
            batches++;
            var label = variants.Count > 1 ? $"{where} [variant {n}/{variants.Count}]" : where;
            var f = SqlLint.Check(sql, label, parameters);
            if (f.Any(x => x.Severity == "ERROR")) siteOk = false;
            findings.AddRange(f);
            opts.Dump($"{Path.GetFileNameWithoutExtension(span.Path)}_L{span.StartLinePosition.Line + 1}_v{n}", sql);
        }
        if (opts.Verbose) Console.WriteLine($"site {where}: {variants.Count} variant(s){(siteOk ? "" : " — FAILED")}");
        // Errors inside a fragment are reported against the site that used it.
        foreach (var fld in eval.FieldsUsed) fieldCoverage[fld] = fieldCoverage.GetValueOrDefault(fld) + 1;
    }
}

// ── 3. Every SQL-looking constant / static field, by reflection ───────────
// One that parses on its own is checked as-is; a fragment (a column list, a
// WHERE clause, an OUTER APPLY) must have been checked inside a batch above.
int fragments = 0, standalone = 0;
var sqlish = new Regex(@"\b(SELECT|INSERT\s+INTO|UPDATE|DELETE\s+FROM|MERGE|DECLARE|OUTER\s+APPLY|CROSS\s+APPLY|WHERE|JOIN)\b", RegexOptions.IgnoreCase);
foreach (var tree in compilation.SyntaxTrees)
{
    var model = eval.Model(tree);
    foreach (var v in tree.GetRoot().DescendantNodes().OfType<VariableDeclaratorSyntax>())
    {
        if (model.GetDeclaredSymbol(v) is not IFieldSymbol { IsStatic: true } f || f.Type.SpecialType != SpecialType.System_String) continue;
        var isSqlName = f.Name.EndsWith("Sql", StringComparison.Ordinal) || f.Name.EndsWith("Cols", StringComparison.Ordinal);
        var inData = f.ContainingNamespace.ToDisplayString().EndsWith(".Data", StringComparison.Ordinal);
        string? value;
        try
        {
            var t = api.GetType(f.ContainingType.ContainingNamespace.ToDisplayString() + "." + MetadataName(f.ContainingType));
            value = t?.GetField(f.Name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null) as string;
        }
        catch { value = null; }
        if (value is null || !(isSqlName || (inData && sqlish.IsMatch(value)))) continue;
        var where = $"{Path.GetRelativePath(opts.Repo, tree.FilePath)}:{v.GetLocation().GetLineSpan().StartLinePosition.Line + 1} {f.ContainingType.Name}.{f.Name}";
        var covered = fieldCoverage.GetValueOrDefault(f);
        var own = SqlLint.Check(SqlLint.ApplyDapperRewrites(value), where);
        if (!own.Any(x => x.Severity == "ERROR")) { standalone++; continue; }
        if (covered > 0) { fragments++; if (opts.Verbose) Console.WriteLine($"fragment {where}: checked inside {covered} query site(s)"); continue; }
        findings.Add(new("ERROR", where, "SQL field that doesn't parse on its own and isn't used by any query site, so it was never checked in context"));
        findings.AddRange(own.Where(x => x.Severity == "ERROR"));
    }
}

static string MetadataName(INamedTypeSymbol t) =>
    t.ContainingType is null ? t.MetadataName : MetadataName(t.ContainingType) + "+" + t.MetadataName;

// ── 4. SQL-building helpers (static …Sql methods), run for real ───────────
// Each call in the source, with the caller's own arguments; the result must be
// a complete statement. (Their uses inside query sites are checked above too.)
foreach (var (helper, at, results) in eval.RunSqlHelpers())
    foreach (var r in results)
    {
        var unresolved = new List<Hole>();
        var filled = eval.Fill(r, h => HoleValues.For(h, unresolved));
        if (unresolved.Count > 0) continue;   // reported by the query site that uses it
        batches++;
        findings.AddRange(SqlLint.Check(SqlLint.ApplyDapperRewrites(filled), $"{at} {helper}(…)"));
    }

// ── Report ────────────────────────────────────────────────────────────────
// One line per distinct problem: the same error in several variants of a batch is listed once, with the variants.
var errors = findings.Where(f => f.Severity == "ERROR")
    .GroupBy(f => (Where: Regex.Replace(f.Where, @" \[variant \d+/\d+\]$", ""), f.Message))
    .Select(g => new Finding("ERROR", g.Key.Where + (g.Count() > 1 || g.First().Where != g.Key.Where
        ? $" [variant {string.Join(",", g.Select(f => Regex.Match(f.Where, @"\[variant (\d+)/").Groups[1].Value).Distinct())} of {Regex.Match(g.First().Where, @"/(\d+)\]").Groups[1].Value}]" : ""), g.Key.Message))
    .ToList();
var warns = findings.Where(f => f.Severity == "WARN").DistinctBy(f => (f.Where, f.Message)).ToList();
foreach (var f in errors) Console.WriteLine($"ERROR {f.Where}: {f.Message}");
if (opts.Verbose || opts.Warnings)
{
    foreach (var f in warns) Console.WriteLine($"WARN  {f.Where}: {f.Message}");
    foreach (var c in eval.ReflectionCalls) Console.WriteLine($"ran   {c}");
    foreach (var h in eval.Holes.DistinctBy(h => (h.File, h.Line, h.Expression))) Console.WriteLine($"hole  {h.File}:{h.Line} `{h.Expression}` ({h.Type?.ToDisplayString()})");
}
if (warns.Count > 0 && !(opts.Verbose || opts.Warnings))
    Console.WriteLine("Unbracketed ODBC / future reserved words (fine today; --warnings lists each place): " + string.Join(", ",
        warns.Select(w => Regex.Match(w.Message, "^'([^']+)'").Groups[1].Value).GroupBy(w => w, StringComparer.OrdinalIgnoreCase)
             .OrderByDescending(g => g.Count()).Select(g => $"{g.Key} ({g.Count()})")));
Console.WriteLine();
Console.WriteLine($"sqlcheck: {migrations.Count} migrations, {sites} query sites, {batches} batches parsed (TSql170 + TSql160); " +
                  $"@variables matched to the call's parameters at {sitesWithParams} sites; " +
                  $"{standalone} SQL fields parsed standalone, {fragments} fragments checked in context; " +
                  $"{eval.ReflectionCalls.Count} helper calls run by reflection; " +
                  $"{errors.Count} error(s), {warns.Count} warning(s){(opts.Verbose || opts.Warnings ? "" : " (--warnings to list)")}.");
return errors.Count == 0 ? 0 : 1;

sealed class Options
{
    public string Repo = "";
    public bool Verbose, Warnings;
    public string? DumpDir;
    public readonly List<string> Files = [];

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "--verbose" or "-v": o.Verbose = true; break;
                case "--warnings": o.Warnings = true; break;
                case "--file": o.Files.Add(Path.GetFullPath(args[++i])); break;
                case "--dump": o.DumpDir = Path.GetFullPath(args[++i]); Directory.CreateDirectory(o.DumpDir); break;
                default: throw new ArgumentException($"unknown argument {args[i]} (use --warnings, --verbose, --dump <dir>, --file <x.sql>)");
            }
        // The checkout this build lives in (tools/sqlcheck/bin/…), so source and assembly match.
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "VantraFlowAPI.slnx"))) d = d.Parent;
        o.Repo = d?.FullName ?? throw new InvalidOperationException("sqlcheck must run from its build inside the repo (dotnet run --project tools/sqlcheck)");
        return o;
    }

    public void Dump(string name, string sql)
    {
        if (DumpDir is not null) File.WriteAllText(Path.Combine(DumpDir, name + ".sql"), sql);
    }
}
