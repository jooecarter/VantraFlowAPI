using VantraFlow.Api.Services.Mail;
using Xunit;

namespace VantraFlow.Api.Tests;

public class FlowEmailTemplateTests
{
    [Fact]
    public void The_subject_heads_the_card_and_everything_is_encoded()
    {
        var html = FlowEmailTemplate.Render("New <b>record</b>\r\nInjected: header", "<script>alert(1)</script>");
        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("New &lt;b&gt;record&lt;/b&gt;  Injected: header</h1>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("Sent automatically by a Vantra&nbsp;Flow&nbsp;automation.", html);
    }

    [Fact]
    public void Blank_lines_make_paragraphs_and_single_breaks_stay()
    {
        var p = FlowEmailTemplate.Paragraphs("Hi team\nA record was added.\n\n\nName: Priya");
        Assert.Equal(2, p.Split("<p ").Length - 1);
        Assert.Contains("Hi team<br>A record was added.</p>", p);
        Assert.Contains(">Name: Priya</p>", p);
        Assert.Equal("", FlowEmailTemplate.Paragraphs(""));
    }

    [Fact]
    public void Web_links_become_anchors_without_their_trailing_punctuation_or_quotes()
    {
        var p = FlowEmailTemplate.Paragraphs("Open https://example.com/r?a=1&b=2. Or \"https://example.com/x\" now. javascript:alert(1)");
        Assert.Contains("<a href=\"https://example.com/r?a=1&amp;b=2\"", p);
        Assert.Contains("</a>. Or", p);
        Assert.Contains("<a href=\"https://example.com/x\"", p);
        Assert.Contains("</a>&quot; now", p);
        Assert.DoesNotContain("href=\"javascript", p);
    }

    [Fact]
    public void The_preview_line_is_the_start_of_the_message()
    {
        Assert.Equal("Hi team A record was added.", FlowEmailTemplate.Preview("Hi team\n\nA record was added."));
        var longText = new string('a', 200);
        var preview = FlowEmailTemplate.Preview(longText);
        Assert.True(preview.Length <= 110);
        Assert.EndsWith("…", preview);
    }
}
