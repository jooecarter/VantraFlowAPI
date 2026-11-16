using System.Text;
using System.Text.Json.Nodes;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Mail;
using VantraFlow.Api.Services.OAuth;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>Vantra Orbit's "Get a report" step, and attaching its PDF to an email.</summary>
public sealed class ReportStepTests
{
    private static JsonObject Report() => (JsonObject)JsonNode.Parse("""
    {
      "dashboard": { "id": "dsh1", "name": "Sales overview" },
      "range": { "preset": "lastWeek", "label": "Last week", "text": "Mon 9 – Sun 15 Nov 2026", "from": "2026-11-09", "to": "2026-11-15" },
      "link": "https://orbit.example.com/dashboards/dsh1",
      "numbers": {
        "revenue_won": { "title": "Revenue won", "value": 42300, "text": "£42,300", "change": 11.9, "changeText": "+12%", "previous": 37800, "dir": "up" },
        "deals_won": { "title": "Deals won", "value": 18, "text": "18", "change": null, "changeText": "", "previous": 0, "dir": "flat" }
      },
      "pdf": { "name": "Weekly sales report – 9–15 Nov.pdf", "base64": "JVBERi0=" }
    }
    """)!;

    [Fact]
    public void Each_number_is_a_token_by_its_slug_with_its_text_and_change()
    {
        var o = ReportStep.Outputs(Report());
        Assert.Equal(42300, o["revenue_won"]!.GetValue<double>());
        Assert.Equal("£42,300", (string?)o["revenue_won_text"]);
        Assert.Equal(11.9, o["revenue_won_change"]!.GetValue<double>());
        Assert.Equal("+12%", (string?)o["revenue_won_change_text"]);
        // No meaningful change (nothing before to compare with): no change outputs at all.
        Assert.False(o.ContainsKey("deals_won_change"));
        Assert.False(o.ContainsKey("deals_won_change_text"));
        Assert.Equal("Sales overview", (string?)o["dashboard"]);
        Assert.Equal("https://orbit.example.com/dashboards/dsh1", (string?)o["link"]);
        // The PDF's bytes never go into the output (the step keeps them as a run file).
        Assert.DoesNotContain("JVBERi0", o.ToJsonString());
        // A Paths condition reads the number: "Revenue won is less than 40000".
        Assert.Equal("42300", Tokens.Render("{{steps.s2.revenue_won}}", new JsonObject { ["steps"] = new JsonObject { ["s2"] = o } }));
    }

    [Fact]
    public void A_file_output_reads_as_its_name_in_text()
    {
        var file = new JsonObject { ["kind"] = "file", ["id"] = "rf_1", ["name"] = "Weekly sales report.pdf" };
        Assert.Equal("Weekly sales report.pdf", Tokens.Text(file));
        // A whole-token value keeps the file object, so Send email's attachments get the file, not its name.
        var data = new JsonObject { ["steps"] = new JsonObject { ["s2"] = new JsonObject { ["pdf"] = file } } };
        Assert.Equal("rf_1", (string?)Tokens.RenderNode(JsonValue.Create("{{steps.s2.pdf}}"), data)!["id"]);
    }

    [Fact]
    public void Gmail_mime_carries_attachments_as_multipart_mixed()
    {
        var raw = OAuthActions.Mime(["a@example.com"], [], "Report", "<p>Hi</p>", null, [new MailFile("Report – 9–15 Nov.pdf", "application/pdf", [1, 2, 3])]);
        var mime = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(raw));
        Assert.Contains("multipart/mixed", mime);
        Assert.Contains("Content-Disposition: attachment", mime);
        Assert.Contains("Content-Type: application/pdf", mime);
        Assert.Contains(Convert.ToBase64String(new byte[] { 1, 2, 3 }), mime);
        // Without files it's as before: one HTML part.
        var plain = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(OAuthActions.Mime(["a@example.com"], [], "S", "<p>x</p>")));
        Assert.DoesNotContain("multipart", plain);
    }

    [Fact]
    public void Smtp_messages_carry_the_files()
    {
        var m = new OutgoingMail("a@example.com", "Report", "<p>Hi</p>", null, null, [new MailFile("r.pdf", "application/pdf", [1, 2, 3])]);
        Assert.Single(m.Files!);
    }

    [Fact]
    public void The_step_is_in_the_catalogue_and_the_planner_knows_its_outputs()
    {
        var e = Assert.Single(FlowCatalog.Actions, x => x.App == "orbit" && x.Key == "report.get");
        Assert.Equal("Get a report", e.Title);
        Assert.Contains("makePdf", e.Description);
        Assert.Equal(["lastWeek", "last7", "monthToDate", "lastMonth"], ReportStep.Ranges);
    }
}
