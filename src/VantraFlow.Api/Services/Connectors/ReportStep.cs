using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Orbit's "Get a report" step (<c>orbit / report.get</c>): config { dashboardId, range, makePdf }. Orbit works the
/// dashboard out as the person who connected it (its own numbers, only what they can see). Output, for later steps and
/// Paths: each number widget by its title as a slug — <c>revenue_won</c> (the number), <c>revenue_won_text</c> ("£42,300"),
/// <c>revenue_won_change</c> (per cent) and <c>revenue_won_change_text</c> ("+12%") — only when there's a meaningful change — plus <c>dashboard</c>,
/// <c>range</c>, <c>link</c>, <c>numbers</c> (all of it) and, with makePdf, <c>pdf</c> ({ kind: file, id, name }: a file the
/// Send email step attaches; db/015) and <c>pdf_size</c>.
/// </summary>
public static class ReportStep
{
    public const string Action = "report.get";
    public static readonly string[] Ranges = ["lastWeek", "last7", "monthToDate", "lastMonth"];

    /// <summary>The step's outputs from Orbit's answer (without the PDF, which the caller keeps as a file).</summary>
    public static JsonObject Outputs(JsonObject report)
    {
        var o = new JsonObject
        {
            ["dashboard"] = Tokens.Text(report["dashboard"]?["name"]),
            ["range"] = Tokens.Text(report["range"]?["text"]),
            ["link"] = Tokens.Text(report["link"]),
        };
        if (report["numbers"] is JsonObject numbers)
        {
            foreach (var (key, n) in numbers)
            {
                if (n is not JsonObject x) continue;
                o[key] = x["value"]?.DeepClone();
                o[$"{key}_text"] = Tokens.Text(x["text"]);
                // A change only when Orbit has a meaningful one (a "before" to compare with).
                if (x["change"] is JsonValue change && Tokens.Text(x["changeText"]) is { Length: > 0 } changeText)
                {
                    o[$"{key}_change"] = change.DeepClone();
                    o[$"{key}_change_text"] = changeText;
                }
            }
            o["numbers"] = numbers.DeepClone();
        }
        return o;
    }
}

public sealed partial class StepActions
{
    /// <summary>"Get a report": Orbit's numbers, and the PDF kept as a run file for later steps.</summary>
    private async Task<StepOutcome> ReportAsync(StepCall call, CancellationToken ct)
    {
        var dashboardId = Tokens.Text(call.Input["dashboardId"]).Trim();
        if (dashboardId.Length == 0) return StepOutcome.Fail("Choose the report.");
        var range = Tokens.Text(call.Input["range"]).Trim();
        if (range.Length == 0) range = "lastWeek";
        if (!ReportStep.Ranges.Contains(range)) return StepOutcome.Fail("Choose what the report covers.");
        var makePdf = call.Input["makePdf"] is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : Tokens.Text(v) == "true");
        if (makePdf && !await files.ReadyAsync()) return StepOutcome.Fail("Making a PDF in Vantra Flow isn't switched on yet. Turn off Make a PDF, or try again after the next update.");
        var report = await connections.CallAsync(call.WorkspaceId, "orbit", (api, key) => ((OrbitApi)api).ReportAsync(key, dashboardId, range, makePdf, ct));
        var output = ReportStep.Outputs(report);
        if (makePdf && report["pdf"] is JsonObject pdf && Tokens.Text(pdf["base64"]) is { Length: > 0 } b64)
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length > RunFilesRepository.MaxBytes) return StepOutcome.Fail("The report's PDF is too big to keep for later steps.");
            var name = Tokens.Text(pdf["name"]) is { Length: > 0 } n ? n : "Report.pdf";
            var id = await files.AddAsync(call.WorkspaceId, call.RunId, call.Step.Id, name, "application/pdf", bytes, DateTime.UtcNow);
            output["pdf"] = new JsonObject { ["kind"] = "file", ["id"] = id, ["name"] = name };
            output["pdf_size"] = bytes.Length;
        }
        return StepOutcome.Done(output);
    }

    /// <summary>
    /// The Send email step's attachments: files earlier steps of this run made (<c>{{steps.s2.pdf}}</c>), read back from
    /// flow.RunFiles — a list or one. Empty is none; anything that isn't such a file is refused.
    /// </summary>
    private async Task<(IReadOnlyList<Mail.MailFile>? Files, string? Error)> AttachmentsAsync(StepCall call)
    {
        var raw = call.Input["attachments"];
        var items = raw switch
        {
            null => [],
            JsonArray a => a.ToList(),
            JsonValue jv when Tokens.Text(jv).Trim().Length == 0 => [],
            _ => [raw],
        };
        items = items.Where(x => !(x is JsonValue j && Tokens.Text(j).Trim().Length == 0)).ToList();
        if (items.Count == 0) return (null, null);
        if (!await files.ReadyAsync()) return (null, "Attachments in Vantra Flow aren't switched on yet.");
        var list = new List<Mail.MailFile>();
        var total = 0;
        foreach (var it in items)
        {
            if (it is not JsonObject f || Tokens.Text(f["kind"]) != "file" || Tokens.Text(f["id"]) is not { Length: > 0 } id)
                return (null, "An attachment isn't a file from an earlier step.");
            var row = await files.GetAsync(call.WorkspaceId, call.RunId, id, DateTime.UtcNow);
            if (row is null) return (null, "A file to attach has gone: files are kept for 7 days, for the run that made them.");
            total += row.Bytes.Length;
            if (total > RunFilesRepository.MaxEmailBytes) return (null, "The attachments are too big for one email (15 MB at most).");
            list.Add(new Mail.MailFile(row.Name, row.ContentType, row.Bytes));
        }
        return (list, null);
    }
}
