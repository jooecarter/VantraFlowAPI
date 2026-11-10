using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>db/001 is paste-ready for Azure SQL: one batch, no USE or GO, parses, and the probe's index comes last.</summary>
public sealed class MigrationTests
{
    private static string Sql => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "001_flow.sql"));

    [Fact]
    public void The_migration_parses_and_is_azure_ready()
    {
        var sql = Sql;
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void Indexes_and_tables_go_through_exec_so_the_batch_compiles_first()
    {
        var outside = Regex.Replace(Sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\bCREATE\s+(UNIQUE\s+)?INDEX\b", RegexOptions.IgnoreCase), outside);
        Assert.DoesNotMatch(new Regex(@"\bCREATE\s+TABLE\b", RegexOptions.IgnoreCase), outside);
    }

    [Fact]
    public void The_probes_index_is_made_last_and_no_probe_reads_constraint_definitions()
    {
        var sql = Sql;
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_Runs_Due", last);
        Assert.Contains("IX_Runs_Due", VantraFlow.Api.Data.FlowSchema.ProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.ProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql.Replace("sys.indexes", ""), StringComparison.OrdinalIgnoreCase);
    }
}
