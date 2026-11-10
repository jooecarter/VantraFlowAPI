using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace VantraFlow.SqlCheck;

public sealed record Finding(string Severity, string Where, string Message);

/// <summary>
/// Parses one batch the way SQL Server would see it — with Microsoft's own T-SQL
/// parser at the latest level (170) and Azure SQL's compatibility level 160 —
/// and lists every unquoted identifier that is a reserved keyword.
/// </summary>
public static partial class SqlLint
{
    private static readonly TSqlParser[] Parsers = [new TSql170Parser(true), new TSql160Parser(true)];

    /// <summary>
    /// Dapper rewrites a list parameter in place before the text reaches SQL
    /// Server: <c>IN @Ids</c> becomes <c>IN (@Ids1, @Ids2, …)</c>. A literal
    /// <c>{=Name}</c> is replaced by the value. Do the same, so the parser sees
    /// what the server sees.
    /// </summary>
    /// <remarks>
    /// With the call's parameters known (<paramref name="parameters"/>), exactly
    /// Dapper's rule: every <c>@Name</c> of a list parameter is expanded, and a
    /// scalar after <c>IN</c> is left alone (SQL Server would reject it). Without
    /// them, every <c>IN @Name</c> is taken to be a list.
    /// </remarks>
    public static string ApplyDapperRewrites(string sql, IReadOnlyDictionary<string, bool>? parameters = null)
    {
        if (parameters is null)
            sql = InList().Replace(sql, m => $"{m.Groups[1].Value} ({m.Groups[2].Value}1, {m.Groups[2].Value}2)");
        else
            foreach (var (name, isList) in parameters)
                if (isList)
                    sql = Regex.Replace(sql, $@"[@]{Regex.Escape(name)}(?!\w)", m => $"({m.Value}1, {m.Value}2)", RegexOptions.IgnoreCase);
        sql = Literal().Replace(sql, "1");
        return sql;
    }

    [GeneratedRegex(@"\b(IN)\s+(@\w+)(?!\w)(?!\s*\()", RegexOptions.IgnoreCase)]
    private static partial Regex InList();

    [GeneratedRegex(@"\{=\w+\}")]
    private static partial Regex Literal();

    /// <summary>
    /// Errors from either parser, then reserved-word findings (only when it
    /// parses), then — when the call's parameters are known — every <c>@variable</c>
    /// the batch reads that is neither sent nor DECLAREd ("Must declare the scalar variable").
    /// </summary>
    public static List<Finding> Check(string sql, string where, IReadOnlyDictionary<string, bool>? parameters = null)
    {
        var findings = new List<Finding>();
        TSqlFragment? tree = null;
        var failedBy = new List<string>();
        foreach (var parser in Parsers)
        {
            using var reader = new StringReader(sql);
            var fragment = parser.Parse(reader, out var errors);
            if (errors.Count == 0) { tree ??= fragment; continue; }
            var level = parser is TSql160Parser ? "160" : "170";
            failedBy.Add(level);
            if (failedBy.Count > 1) continue;   // the same error at both levels: say it once
            foreach (var e in errors.Take(3))
                findings.Add(new Finding("ERROR", where, $"L{e.Line}:{e.Column} {OneLine(e.Message)}  >>> {Excerpt(sql, e.Line, e.Column)}"
                    + (e.Message.Contains("end comment mark") ? " (T-SQL comments nest: a '/*' inside a /* … */ comment, e.g. in a path like /api/x/*, opens another one)" : "")));
        }
        if (failedBy.Count > 0)
            for (var i = 0; i < findings.Count; i++)
                findings[i] = findings[i] with { Message = findings[i].Message + $"  [TSql{string.Join("+", failedBy)}]" };
        if (tree is not null && findings.Count == 0)
        {
            var v = new ReservedIdentifierVisitor();
            tree.Accept(v);
            foreach (var (id, line, col) in v.Hits)
            {
                var word = id.Value;
                if (Keywords.TSql.Contains(word))
                    findings.Add(new Finding("ERROR", where, $"unbracketed T-SQL reserved keyword '{word}' used as an identifier at L{line}:{col} — write [{word}]  >>> {Excerpt(sql, line, col)}"));
                else if (Keywords.Odbc.Contains(word) || Keywords.Future.Contains(word))
                    findings.Add(new Finding("WARN", where, $"'{word}' is an {(Keywords.Odbc.Contains(word) ? "ODBC" : "future")} reserved keyword used unbracketed as an identifier (works today)"));
            }
            if (parameters is not null)
            {
                var vars = new VariableVisitor();
                tree.Accept(vars);
                foreach (var (name, line, col) in vars.Read.DistinctBy(r => r.Name.ToUpperInvariant()))
                    if (!vars.Declared.Contains(name) && !parameters.ContainsKey(name.TrimStart('@'))
                        && !(parameters.TryGetValue(name.TrimStart('@').TrimEnd('1', '2'), out var isList) && isList))   // Dapper's @Ids1, @Ids2
                        findings.Add(new Finding("ERROR", where, $"{name} is read at L{line}:{col} but the call doesn't send it and the batch doesn't DECLARE it — SQL Server: \"Must declare the scalar variable {name}\""));
            }
        }
        return findings;
    }

    private sealed class VariableVisitor : TSqlFragmentVisitor
    {
        private readonly HashSet<VariableReference> procParams = new(ReferenceEqualityComparer.Instance);
        public readonly HashSet<string> Declared = new(StringComparer.OrdinalIgnoreCase);
        public readonly List<(string Name, int Line, int Column)> Read = [];

        public override void Visit(ExecuteParameter node) { if (node.Variable is not null) procParams.Add(node.Variable); }
        public override void Visit(DeclareVariableElement node) => Declared.Add(node.VariableName.Value);
        public override void Visit(DeclareTableVariableBody node) => Declared.Add(node.VariableName.Value);
        public override void Visit(VariableReference node)
        {
            if (!procParams.Contains(node)) Read.Add((node.Name, node.StartLine, node.StartColumn));
        }
        public override void Visit(VariableTableReference node) => Read.Add((node.Variable.Name, node.StartLine, node.StartColumn));
    }

    private static string OneLine(string s) => s.Length > 160 ? OneLine(s[..160]) + "…" : s.Replace("\r", "").Replace("\n", " ⏎ ");

    public static string Excerpt(string sql, int line, int col)
    {
        var lines = sql.Split('\n');
        if (line < 1 || line > lines.Length) return "";
        var l = lines[line - 1].TrimEnd('\r');
        var start = Math.Max(0, col - 50);
        return l.Substring(Math.Min(start, l.Length), Math.Min(100, Math.Max(0, l.Length - start))).Trim();
    }

    /// <summary>
    /// Collects the unquoted identifiers that name something — tables, columns,
    /// aliases, constraints, CTEs, OPENJSON columns — but not function names,
    /// type names or date parts (<c>DATEADD(DAY, …)</c>), which are keywords by design.
    /// </summary>
    private sealed class ReservedIdentifierVisitor : TSqlFragmentVisitor
    {
        private static readonly HashSet<string> DatePartFunctions = new(StringComparer.OrdinalIgnoreCase)
        { "DATEADD", "DATEDIFF", "DATEDIFF_BIG", "DATENAME", "DATEPART", "DATETRUNC", "DATE_BUCKET" };

        private readonly HashSet<Identifier> skip = new(ReferenceEqualityComparer.Instance);
        public readonly List<(Identifier Id, int Line, int Column)> Hits = [];

        public override void Visit(FunctionCall node)
        {
            skip.Add(node.FunctionName);
            if (DatePartFunctions.Contains(node.FunctionName.Value) && node.Parameters.Count > 0
                && node.Parameters[0] is ColumnReferenceExpression c && c.MultiPartIdentifier is not null)
                foreach (var i in c.MultiPartIdentifier.Identifiers) skip.Add(i);
        }

        public override void Visit(DataTypeReference node)
        {
            if (node is DataTypeReference { } d && d.Name is not null)
                foreach (var i in d.Name.Identifiers) skip.Add(i);
        }

        public override void Visit(Identifier node)
        {
            if (node.QuoteType != QuoteType.NotQuoted || skip.Contains(node)) return;
            Hits.Add((node, node.StartLine, node.StartColumn));
        }
    }
}
