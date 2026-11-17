using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Schedule;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Vantra Orbit's "Export rows" step: its outputs, the file handed to Send email, the row cap, and Orbit's spreadsheet schedules on the Schedule page.</summary>
public sealed class RowsStepTests
{
    private static JsonObject Answer(int rows = 3, int count = 14) => new()
    {
        ["table"] = new JsonObject { ["id"] = "tbl_leads", ["name"] = "Leads" },
        ["view"] = new JsonObject { ["id"] = "v_all", ["name"] = "All leads" },
        ["range"] = new JsonObject { ["preset"] = "yesterday", ["label"] = "Yesterday", ["words"] = "yesterday" },
        ["link"] = "https://orbit.example.com/t/tbl_leads/v/v_all",
        ["rowCount"] = count,
        ["truncated"] = false,
        ["columns"] = new JsonArray("Company", "Value"),
        ["rows"] = new JsonArray(Enumerable.Range(0, rows).Select(i => (JsonNode?)new JsonObject
        {
            ["id"] = $"rec_{i}", ["createdAt"] = "2026-11-16T09:12:00.000Z", ["fields"] = new JsonObject { ["Company"] = $"Company {i}", ["Value"] = "£6,800" },
        }).ToArray()),
        ["rowsCut"] = count > rows,
        ["file"] = new JsonObject { ["name"] = "Leads – added 16 Nov.xlsx", ["base64"] = "UEsDBA==" },
    };

    [Fact]
    public void The_outputs_are_the_count_the_rows_for_for_each_and_the_view()
    {
        var o = RowsStep.Outputs(Answer(), "https://orbit.example.com");
        Assert.Equal(14, o["row_count"]!.GetValue<int>());
        Assert.True(o["rows_cut"]!.GetValue<bool>());
        Assert.False(o["truncated"]!.GetValue<bool>());
        Assert.Equal("Leads", (string?)o["table"]);
        Assert.Equal("All leads", (string?)o["view"]);
        Assert.Equal("Yesterday", (string?)o["range"]);
        var rows = o["rows"]!.AsArray();
        Assert.Equal(3, rows.Count);
        Assert.Equal("https://orbit.example.com/r/rec_0", (string?)rows[0]!["url"]);
        Assert.Equal("Company 0", (string?)rows[0]!["fields"]!["Company"]);
        // The file's bytes never go into the output (the step keeps them as a run file).
        Assert.DoesNotContain("UEsDBA", o.ToJsonString());
        // Paths reads the count: "Row count more than 0".
        var data = new JsonObject { ["steps"] = new JsonObject { ["s2"] = o } };
        Assert.Equal("14", Tokens.Render("{{steps.s2.row_count}}", data));
        // A For each reads each row's fields.
        Assert.Equal("Company 1", Tokens.Render("{{item.fields.Company}}", new JsonObject { ["item"] = rows[1]!.DeepClone() }));
    }

    [Fact]
    public void At_most_500_rows_are_handed_on()
    {
        var o = RowsStep.Outputs(Answer(rows: 620, count: 9000), "https://orbit.example.com");
        Assert.Equal(RowsStep.MaxRows, o["rows"]!.AsArray().Count);
        Assert.Equal(9000, o["row_count"]!.GetValue<int>());
        Assert.True(o["rows_cut"]!.GetValue<bool>());
        Assert.Equal(500, RowsStep.MaxRows);
    }

    [Fact]
    public void The_spreadsheet_passes_to_send_email_as_a_file()
    {
        // What the step adds once it has kept the file (db/015).
        var file = new JsonObject { ["kind"] = "file", ["id"] = "rf_1", ["name"] = "Leads – added 16 Nov.xlsx" };
        var data = new JsonObject { ["steps"] = new JsonObject { ["s2"] = new JsonObject { ["file"] = file } } };
        var attached = Tokens.RenderNode(JsonValue.Create("{{steps.s2.file}}"), data)!;
        Assert.Equal("file", (string?)attached["kind"]);
        Assert.Equal("rf_1", (string?)attached["id"]);
        Assert.Equal("Leads – added 16 Nov.xlsx", Tokens.Text(file));
    }

    private sealed class Orbit(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body));
            var (code, json) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    [Fact]
    public async Task Orbit_is_asked_for_the_view_s_rows_with_the_connection_key_and_at_most_500_rows()
    {
        var handler = new Orbit((_, _) => (HttpStatusCode.OK, Answer().ToJsonString()));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Orbit:ApiUrl"] = "https://orbit.example.com" }).Build();
        var api = new OrbitApi(new Factory(handler), config);
        var got = await api.ExportRowsAsync("orbk_live_x", "tbl_leads", "v_all", "yesterday", "xlsx", true, CancellationToken.None);
        var call = Assert.Single(handler.Calls);
        Assert.Equal("POST", call.Method);
        Assert.Equal("/api/v1/tables/tbl_leads/export", call.Path);
        var body = JsonNode.Parse(call.Body)!;
        Assert.Equal("v_all", (string?)body["viewId"]);
        Assert.Equal("yesterday", (string?)body["range"]);
        Assert.Equal("xlsx", (string?)body["format"]);
        Assert.True(body["makeFile"]!.GetValue<bool>());
        Assert.Equal(500, body["rowLimit"]!.GetValue<int>());
        Assert.Equal(14, got["rowCount"]!.GetValue<int>());
        // The generic action path refuses it: StepActions runs it, so the file is kept.
        await Assert.ThrowsAsync<AppRefusedException>(() => api.ActAsync("k", RowsStep.Action, new JsonObject(), "i", CancellationToken.None));
    }

    [Fact]
    public void The_step_is_in_the_catalogue_and_the_planner_knows_its_outputs()
    {
        var e = Assert.Single(FlowCatalog.Actions, x => x.App == "orbit" && x.Key == "rows.export");
        Assert.Equal("Export rows", e.Title);
        Assert.Contains("tableId", e.Required);
        Assert.Contains("secret fields never go in", e.Description);
        Assert.Contains("orbit/rows.export", Services.Ai.PlannerCatalog.Hints.Keys);
        Assert.Equal(["yesterday", "today", "last7", "lastWeek", "monthToDate", "lastMonth", "all"], RowsStep.Ranges);
        Assert.Equal(["xlsx", "csv"], RowsStep.Formats);
    }

    [Fact]
    public void Orbit_s_spreadsheet_schedules_show_on_the_schedule_page_with_their_own_line_and_open_in_orbit()
    {
        var s = new JsonObject
        {
            ["id"] = "rws_1", ["kind"] = "rows", ["title"] = "Leads", ["dashboardName"] = "Leads", ["summary"] = "Every day 08:00", ["cadence"] = "day",
            ["rangeLabel"] = "added yesterday", ["meta"] = "Spreadsheet · Leads · added yesterday", ["recipientCount"] = 2, ["canChange"] = true, ["canEdit"] = false,
            ["link"] = "https://orbit.example.com/t/tbl_leads/v/v_all?rows=rws_1", ["runs"] = new JsonArray("2026-11-18T08:00:00.000Z"),
        };
        var from = new DateTime(2026, 11, 17, 0, 0, 0, DateTimeKind.Utc);
        var row = ScheduleBoard.Report(s, TimeZoneInfo.Utc, from, from.AddDays(14))!;
        Assert.Equal("report", (string?)row["kind"]);
        Assert.Equal("rows", (string?)row["reportKind"]);
        Assert.Equal("Spreadsheet · Leads · added yesterday", (string?)row["meta"]);
        Assert.False(row["canEdit"]!.GetValue<bool>());
        Assert.True(row["canChange"]!.GetValue<bool>());
        // A dashboard's report reads as before, and can be edited from Flow.
        var dash = ScheduleBoard.Report(new JsonObject { ["id"] = "rps_1", ["title"] = "Weekly", ["runs"] = new JsonArray() }, TimeZoneInfo.Utc, from, from.AddDays(14))!;
        Assert.Equal("dashboard", (string?)dash["reportKind"]);
        Assert.True(dash["canEdit"]!.GetValue<bool>());
        Assert.Null(dash["meta"]);
    }
}
