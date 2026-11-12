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

    private static string Alerts => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "002_failure_alerts.sql"));

    [Fact]
    public void The_alerts_migration_parses_is_azure_ready_and_adds_the_probes_column_last()
    {
        var sql = Alerts;
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        // Every change goes through EXEC() and is guarded, so the batch compiles and re-runs.
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase), outside);
        Assert.Equal(4, Regex.Matches(sql, @"IF COL_LENGTH\(").Count);
        var last = Regex.Matches(sql, @"ADD\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("AlertedAt", last);
        Assert.Contains("AlertedAt", VantraFlow.Api.Data.FlowSchema.AlertsProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.AlertsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public void The_reliability_migration_parses_is_azure_ready_stops_without_db001_and_adds_the_probes_column_last()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "010_reliability.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\bALTER\s+TABLE\b", RegexOptions.IgnoreCase), outside);
        // The guard comes before any change.
        Assert.True(sql.IndexOf("THROW 51010", StringComparison.Ordinal) < sql.IndexOf("EXEC(N'", StringComparison.Ordinal));
        var last = Regex.Matches(sql, @"ADD\s+(\w+)\s+(?:BIT|DATE|TINYINT)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("DigestSentOn", last);
        Assert.Contains("DigestSentOn", VantraFlow.Api.Data.FlowSchema.ReliabilityProbeSql);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".definition", sql, StringComparison.OrdinalIgnoreCase);
        // Existing workspaces get 0, new ones 1.
        Assert.Contains("DEFAULT 0 WITH VALUES", sql);
        Assert.Contains("DEFAULT 1 FOR FailureDigest", sql);
    }
}
