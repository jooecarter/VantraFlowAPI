using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace VantraFlow.Api.Services.Mail;

/// <summary>
/// The layout of a "Send email" step's message from Vantra Flow's own mailbox: a card with the subject as its heading,
/// the message in paragraphs (a blank line starts one, a line break stays one) with its web links made clickable, and a
/// quiet footer, plus a hidden preview line for the inbox. Table-based with inline styles, as email clients need; the
/// same family as the workspace invitations (<see cref="Workspaces.WorkspaceMail"/>). Everything from the flow is
/// HTML-encoded. A message from the person's own mailbox (Outlook, Gmail, SMTP) keeps its plain layout: that's their mail.
/// </summary>
public static partial class FlowEmailTemplate
{
    /// <summary>The full HTML document for <paramref name="subject"/> and the step's plain-text <paramref name="body"/>.</summary>
    public static string Render(string subject, string body)
    {
        var text = (body ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        var heading = E(OneLine(subject).Trim());
        var paragraphs = Paragraphs(text);
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
<meta charset=""utf-8"">
<meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<meta name=""color-scheme"" content=""light"">
<title>{heading}</title>
</head>
<body style=""margin:0;padding:0;background:#f3f5f4;-webkit-text-size-adjust:100%;"">
  <div style=""display:none;max-height:0;overflow:hidden;opacity:0;color:#f3f5f4;font-size:1px;line-height:1px;"">{E(Preview(text))}&#8203;</div>
  <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""background:#f3f5f4;"">
    <tr><td align=""center"" style=""padding:32px 16px;"">
      <table role=""presentation"" width=""100%"" cellpadding=""0"" cellspacing=""0"" style=""max-width:560px;"">
        <tr><td style=""padding:0 4px 14px;font-family:{Font};"">
          <span style=""display:inline-block;width:22px;height:22px;line-height:22px;border-radius:6px;background:#12855d;color:#ffffff;font-size:13px;font-weight:700;text-align:center;vertical-align:middle;"">V</span>
          <span style=""margin-left:8px;font-size:14px;font-weight:700;color:#0f3d2e;vertical-align:middle;"">Vantra <span style=""font-weight:400;"">Flow</span></span>
        </td></tr>
        <tr><td style=""background:#ffffff;border:1px solid #e1e7e4;border-top:4px solid #12855d;border-radius:12px;padding:30px 32px 26px;font-family:{Font};"">
          <h1 style=""margin:0 0 16px;font-size:21px;line-height:1.3;font-weight:700;color:#15202b;"">{heading}</h1>
{paragraphs}
        </td></tr>
        <tr><td style=""padding:16px 4px 0;font-family:{Font};font-size:12px;line-height:1.5;color:#8a949e;"">
          Sent automatically by a Vantra&nbsp;Flow&nbsp;automation.
        </td></tr>
      </table>
    </td></tr>
  </table>
</body>
</html>";
    }

    private const string Font = "-apple-system,BlinkMacSystemFont,'Segoe UI',Inter,Roboto,Helvetica,Arial,sans-serif";
    private const string ParagraphStyle = "margin:0 0 14px;font-size:15px;line-height:1.65;color:#3c4853;";

    /// <summary>A blank line starts a paragraph; a single line break stays a &lt;br&gt;. An empty message gets no paragraph.</summary>
    internal static string Paragraphs(string text)
    {
        if (text.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var block in BlankLines().Split(text))
        {
            var lines = block.Trim('\n').Split('\n').Select(l => Linkify(E(l.TrimEnd())));
            sb.Append("          <p style=\"").Append(ParagraphStyle).Append("\">").Append(string.Join("<br>", lines)).Append("</p>\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>http(s) links in already-encoded text as anchors; trailing punctuation stays outside the link.</summary>
    internal static string Linkify(string encoded) =>
        Url().Replace(encoded, m =>
        {
            var url = m.Value;
            var tail = "";
            while (url.Length > 0 && ".,;:!?)]'\"".Contains(url[^1])) { tail = url[^1] + tail; url = url[..^1]; }
            return $"<a href=\"{url}\" style=\"color:#12855d;text-decoration:underline;word-break:break-all;\">{url}</a>{tail}";
        });

    /// <summary>The inbox's preview line: the start of the message on one line, at most 110 characters.</summary>
    internal static string Preview(string text)
    {
        var one = Spaces().Replace(text, " ").Trim();
        return one.Length <= 110 ? one : one[..107].TrimEnd() + "…";
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
    private static string OneLine(string s) => new((s ?? "").Select(ch => char.IsControl(ch) ? ' ' : ch).ToArray());

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();

    // Stops at an encoded quote or angle bracket too (the text is HTML-encoded before this runs).
    [GeneratedRegex(@"https?://(?:(?!&quot;|&#39;|&lt;|&gt;)[^\s<>""])+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
