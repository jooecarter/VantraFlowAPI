using System.Text.Json.Nodes;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Connectors;

/// <summary>
/// Vantra Orbit's "Export rows" step (<c>orbit / rows.export</c>): config { tableId, viewId, range, format }. Orbit reads the view (its
/// filters and sort, its columns) as the person who connected it — hidden tables and secret fields never go in — and keeps the rows
/// added in the range. Output, for later steps and Paths: <c>file</c> ({ kind: file, id, name }: the .xlsx or .csv, a file the Send
/// email step attaches; db/015), <c>file_name</c>, <c>row_count</c> (every row that matched), <c>rows</c> (the first 500 as a list for
/// For each: { id, url, createdAt, fields: { Column: text } }), <c>rows_cut</c> (more than that matched), <c>truncated</c> (more than
/// the file's 10,000), and <c>table</c>, <c>view</c>, <c>range</c>, <c>link</c>. Before db/015 there's no file; the rest works.
/// </summary>
public static class RowsStep
{
    public const string Action = "rows.export";
    public static readonly string[] Ranges = ["yesterday", "today", "last7", "lastWeek", "monthToDate", "lastMonth", "all"];
    public static readonly string[] Formats = ["xlsx", "csv"];
    /// <summary>The most rows handed on as a list (Orbit's own cap too).</summary>
    public const int MaxRows = 500;

    /// <summary>The step's outputs from Orbit's answer (without the file, which the caller keeps as a run file).</summary>
    public static JsonObject Outputs(JsonObject answer, string appUrl)
    {
        var rows = new JsonArray();
        foreach (var r in (answer["rows"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxRows))
        {
            var id = Tokens.Text(r["id"]);
            rows.Add(new JsonObject
            {
                ["id"] = id,
                ["url"] = $"{appUrl}/r/{Uri.EscapeDataString(id)}",
                ["createdAt"] = r["createdAt"]?.DeepClone(),
                ["fields"] = r["fields"] is JsonObject f ? f.DeepClone() : new JsonObject(),
            });
        }
        var count = answer["rowCount"] is JsonValue c && c.TryGetValue<int>(out var n) ? n : rows.Count;
        return new JsonObject
        {
            ["table"] = Tokens.Text(answer["table"]?["name"]),
            ["view"] = Tokens.Text(answer["view"]?["name"]),
            ["range"] = Tokens.Text(answer["range"]?["label"]),
            ["link"] = Tokens.Text(answer["link"]),
            ["row_count"] = count,
            ["rows"] = rows,
            ["rows_cut"] = count > rows.Count,
            ["truncated"] = answer["truncated"] is JsonValue t && t.TryGetValue<bool>(out var cut) && cut,
            ["columns"] = answer["columns"]?.DeepClone() ?? new JsonArray(),
        };
    }
}

public sealed partial class StepActions
{
    /// <summary>"Export rows": the rows (and the count) for later steps, and the spreadsheet kept as a run file.</summary>
    private async Task<StepOutcome> ExportRowsAsync(StepCall call, CancellationToken ct)
    {
        var tableId = Tokens.Text(call.Input["tableId"]).Trim();
        if (tableId.Length is 0 or > 64) return StepOutcome.Fail("Choose the table.");
        var viewId = Tokens.Text(call.Input["viewId"]).Trim();
        if (viewId.Length > 64) return StepOutcome.Fail("Choose the view.");
        var range = Tokens.Text(call.Input["range"]).Trim();
        if (range.Length == 0) range = "yesterday";
        if (!RowsStep.Ranges.Contains(range)) return StepOutcome.Fail("Choose which rows to export.");
        var format = Tokens.Text(call.Input["format"]).Trim();
        if (format.Length == 0) format = "xlsx";
        if (!RowsStep.Formats.Contains(format)) return StepOutcome.Fail("Choose Excel or CSV.");
        // Before db/015 the step can't keep a file: the count and the rows still work.
        var keep = await files.ReadyAsync();
        var (answer, appUrl) = await connections.CallAsync(call.WorkspaceId, "orbit", async (api, key) =>
            (await ((OrbitApi)api).ExportRowsAsync(key, tableId, viewId.Length > 0 ? viewId : null, range, format, keep, ct), api.AppUrl));
        var output = RowsStep.Outputs(answer, appUrl);
        if (keep && answer["file"] is JsonObject file && Tokens.Text(file["base64"]) is { Length: > 0 } b64)
        {
            var bytes = Convert.FromBase64String(b64);
            if (bytes.Length > RunFilesRepository.MaxBytes) return StepOutcome.Fail("The spreadsheet is too big to keep for later steps (10 MB at most). Choose fewer rows or columns.");
            var name = Tokens.Text(file["name"]) is { Length: > 0 } n ? n : $"Rows.{format}";
            var type = format == "csv" ? "text/csv" : "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
            var id = await files.AddAsync(call.WorkspaceId, call.RunId, call.Step.Id, name, type, bytes, DateTime.UtcNow);
            output["file"] = new JsonObject { ["kind"] = "file", ["id"] = id, ["name"] = name };
            output["file_name"] = name;
        }
        return StepOutcome.Done(output);
    }
}
