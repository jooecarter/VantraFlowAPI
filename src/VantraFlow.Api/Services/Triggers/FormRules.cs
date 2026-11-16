using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Triggers;

/// <summary>One question on a form: its stable key (the token <c>{{trigger.answers.key}}</c>), label, type, whether it's required and a choice's options.</summary>
public sealed record FormQuestion(string Key, string Label, string Type, bool Required, IReadOnlyList<string> Options);

/// <summary>A form trigger's settings, read from <c>trigger.config</c>.</summary>
public sealed record FormSpec(
    string Title, string? Description, IReadOnlyList<FormQuestion> Questions, string Access, string Slug, bool ShowInApps, string SubmitLabel, string? Confirmation)
{
    public bool Public => Access == FormRules.Public;
}

/// <summary>Someone signed in who fills in a team form (from the shared Vantra identity, never from the page).</summary>
public sealed record FormPerson(string Id, string? Name, string? Email);

/// <summary>
/// "A form" (<c>form / form.submitted</c>): the questions live in the flow's definition (<c>trigger.config</c>); the
/// public address and how it's found live in <c>flow.FlowForms</c> (db/014). One answer starts one run with
/// <c>trigger.answers.{key}</c> (keys slugged from the labels, and kept when a label changes), <c>trigger.submittedBy</c>
/// (team forms: id, name, email) and <c>trigger.submittedAt</c>. Answers are data, never HTML: control characters are
/// stripped, each answer is at most <see cref="MaxAnswer"/> characters and all of them together at most <see cref="MaxTotal"/>.
/// </summary>
public static partial class FormRules
{
    public const string App = "form";
    public const string Event = "form.submitted";
    public const string Team = "team";
    public const string Public = "public";
    public const int MaxQuestions = 20;
    public const int MaxOptions = 30;
    public const int MaxAnswer = 5_000;
    public const int MaxTotal = 40_000;
    public const int MaxLabel = 160;
    public const int MaxTitle = 120;
    public const int MaxDescription = 600;
    public const int MaxConfirmation = 300;
    public const int MaxSubmitLabel = 40;
    public const string DefaultSubmitLabel = "Send";

    public static readonly IReadOnlyList<string> Types =
        ["text", "longText", "email", "number", "date", "dateRange", "choice", "checkbox", "person"];

    public static bool IsForm(TriggerDef t) => t.App == App && t.Event == Event;

    [GeneratedRegex("^[a-z][a-z0-9_]{0,39}$")]
    public static partial Regex KeyPattern();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$")]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^[^@\s]{1,64}@[^@\s]+\.[^@\s]{2,}$")]
    private static partial Regex EmailPattern();

    /// <summary>A key from a label: lower-case letters, numbers and underscores ("Who's asking" → <c>whos_asking</c>).</summary>
    public static string KeyFrom(string? label)
    {
        var s = Fold(label ?? "");
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is '\'' or '’') continue;
            else if (sb.Length > 0 && sb[^1] != '_') sb.Append('_');
        }
        var key = sb.ToString().Trim('_');
        if (key.Length == 0 || !char.IsAsciiLetterLower(key[0])) key = "q_" + key;
        key = key.TrimEnd('_');
        return key.Length > 40 ? key[..40].TrimEnd('_') : key;
    }

    /// <summary>A link's slug from a title: lower-case words joined with hyphens ("Holiday request" → <c>holiday-request</c>).</summary>
    public static string SlugFrom(string? title)
    {
        var s = Fold(title ?? "");
        var sb = new StringBuilder();
        foreach (var c in s)
        {
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (c is '\'' or '’') continue;
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        if (slug.Length > 50) slug = slug[..50].Trim('-');
        return slug.Length == 0 ? "form" : slug;
    }

    public static bool ValidSlug(string? slug) => slug is { Length: > 0 and <= 60 } s && SlugPattern().IsMatch(s);

    private static string Fold(string s)
    {
        var d = s.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(d.Length);
        foreach (var c in d)
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// The form as its trigger config says, made safe to use: keys made from labels where missing, kept unique; unknown
    /// types and options dropped; at most <see cref="MaxQuestions"/>; a person question dropped from a public form.
    /// </summary>
    public static FormSpec Read(JsonObject config)
    {
        var access = Tokens.Text(config["access"]) == Public ? Public : Team;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var questions = new List<FormQuestion>();
        foreach (var node in (config["questions"] as JsonArray ?? []).Take(MaxQuestions))
        {
            if (node is not JsonObject q) continue;
            var label = Clip(Clean(Tokens.Text(q["label"]), false), MaxLabel);
            var type = Tokens.Text(q["type"]);
            if (!Types.Contains(type)) type = "text";
            if (type == "person" && access == Public) continue;
            var key = Tokens.Text(q["key"]).Trim();
            if (!KeyPattern().IsMatch(key)) key = KeyFrom(label.Length > 0 ? label : "question");
            var unique = key;
            for (var i = 2; !seen.Add(unique); i++) unique = $"{(key.Length > 36 ? key[..36] : key)}_{i}";
            var options = type == "choice"
                ? (q["options"] as JsonArray ?? []).Select(o => Clip(Clean(Tokens.Text(o), false), 100)).Where(o => o.Length > 0).Distinct().Take(MaxOptions).ToList()
                : [];
            var required = type != "person" && q["required"] is JsonValue rv && rv.TryGetValue<bool>(out var r) && r;
            questions.Add(new FormQuestion(unique, label, type, required, options));
        }
        var title = Clip(Clean(Tokens.Text(config["title"]), false), MaxTitle);
        var slugGiven = Tokens.Text(config["slug"]).Trim().ToLowerInvariant();
        var slug = ValidSlug(slugGiven) ? slugGiven : SlugFrom(title);
        var submit = Clip(Clean(Tokens.Text(config["submitLabel"]), false), MaxSubmitLabel);
        return new FormSpec(
            title,
            Tokens.Text(config["description"]) is { Length: > 0 } d ? Clip(Clean(d, true), MaxDescription) : null,
            questions, access, slug,
            access == Team && config["showInApps"] is JsonValue sv && sv.TryGetValue<bool>(out var show) && show,
            submit.Length > 0 ? submit : DefaultSubmitLabel,
            Tokens.Text(config["confirmation"]) is { Length: > 0 } c ? Clip(Clean(c, true), MaxConfirmation) : null);
    }

    /// <summary>What's missing or wrong with a form trigger, for the studio (and before it's switched on).</summary>
    public static IEnumerable<string> Check(JsonObject config)
    {
        if (Tokens.Text(config["title"]).Trim().Length == 0) yield return "Give the form a title.";
        if (config["questions"] is not JsonArray list || list.Count == 0) { yield return "Add a question."; yield break; }
        if (list.Count > MaxQuestions) yield return $"A form can have at most {MaxQuestions} questions.";
        var publicForm = Tokens.Text(config["access"]) == Public;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var i = 0;
        foreach (var node in list)
        {
            i++;
            if (node is not JsonObject q) { yield return $"Question {i} can't be read."; continue; }
            var label = Tokens.Text(q["label"]).Trim();
            if (label.Length == 0) yield return $"Question {i} needs a label.";
            var type = Tokens.Text(q["type"]);
            if (!Types.Contains(type)) yield return $"Choose a type for question {i}.";
            if (type == "person" && publicForm) yield return "A person question is only for team members' forms.";
            if (type == "choice" && (q["options"] as JsonArray ?? []).Count(o => Tokens.Text(o).Trim().Length > 0) < 2)
                yield return $"{(label.Length > 0 ? $"“{Clip(label, 40)}”" : $"Question {i}")} needs at least two options.";
            var key = Tokens.Text(q["key"]).Trim();
            if (key.Length > 0 && !KeyPattern().IsMatch(key)) yield return $"Question {i}'s key must be short lower-case letters, numbers and underscores.";
            else if (key.Length > 0 && !keys.Add(key)) yield return "Two questions share a key.";
        }
        var slug = Tokens.Text(config["slug"]).Trim();
        if (slug.Length > 0 && !ValidSlug(slug)) yield return "The link can use lower-case letters, numbers and hyphens.";
    }

    // ── Answers ──

    /// <summary>A checked submission: the answers by key (each a plain value), or the problems by key.</summary>
    public sealed record Checked(JsonObject Answers, IReadOnlyDictionary<string, string> Errors)
    {
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// Checks and cleans what the page sent against the form as it is now: unknown keys ignored, each answer as its
    /// question's type (text cleaned and cut to <see cref="MaxAnswer"/>; the total held to <see cref="MaxTotal"/>), a person
    /// question filled with who is signed in (never what was sent).
    /// </summary>
    public static Checked CheckAnswers(FormSpec form, JsonObject? sent, FormPerson? person)
    {
        var answers = new JsonObject();
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var total = 0;
        foreach (var q in form.Questions)
        {
            if (q.Type == "person")
            {
                answers[q.Key] = person is null ? null : PersonNode(person);
                continue;
            }
            var raw = sent?[q.Key];
            var (value, why) = Value(q, raw);
            if (why is not null) { errors[q.Key] = why; continue; }
            if (value is null)
            {
                if (q.Required) errors[q.Key] = q.Type == "checkbox" ? "Tick this to go on." : "Fill this in.";
                answers[q.Key] = q.Type == "checkbox" ? false : null;
                continue;
            }
            total += Size(value);
            if (total > MaxTotal) { errors[q.Key] = "The answers are too long altogether."; continue; }
            answers[q.Key] = value;
        }
        return new Checked(answers, errors);
    }

    private static int Size(JsonNode n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Length : n.ToJsonString().Length;

    public static JsonObject PersonNode(FormPerson p) => new() { ["id"] = p.Id, ["name"] = p.Name, ["email"] = p.Email };

    private static (JsonNode? Value, string? Error) Value(FormQuestion q, JsonNode? raw)
    {
        switch (q.Type)
        {
            case "checkbox":
            {
                var on = raw is JsonValue v && (v.TryGetValue<bool>(out var b) ? b : Tokens.Text(v) is "true" or "on" or "1");
                return (on ? true : null, null);
            }
            case "dateRange":
            {
                if (raw is not JsonObject o) return (null, raw is null || Empty(raw) ? null : "Choose the dates.");
                var start = Tokens.Text(o["start"]).Trim();
                var end = Tokens.Text(o["end"]).Trim();
                if (start.Length == 0 && end.Length == 0) return (null, null);
                if (!IsDay(start) || !IsDay(end)) return (null, "Choose both dates.");
                if (string.CompareOrdinal(end, start) < 0) return (null, "The end is before the start.");
                return (new JsonObject { ["start"] = start, ["end"] = end }, null);
            }
        }
        if (raw is JsonObject or JsonArray) return (null, "That answer can't be read.");
        var text = raw is null ? "" : Tokens.Text(raw);
        if (text.Length > MaxAnswer * 2) return (null, $"Use at most {MaxAnswer:N0} characters.");
        text = Clean(text, q.Type == "longText").Trim();
        if (text.Length == 0) return (null, null);
        if (text.Length > MaxAnswer) return (null, $"Use at most {MaxAnswer:N0} characters.");
        switch (q.Type)
        {
            case "email":
                return EmailPattern().IsMatch(text) && text.Length <= 254 ? (JsonValue.Create(text), null) : (null, "Enter an email address.");
            case "number":
                var n = text.Replace(",", "").Replace(" ", "");
                return double.TryParse(n, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
                    ? (JsonValue.Create(d), null) : (null, "Enter a number.");
            case "date":
                return IsDay(text) ? (JsonValue.Create(text), null) : (null, "Choose a date.");
            case "choice":
                var match = q.Options.FirstOrDefault(o => string.Equals(o, text, StringComparison.Ordinal));
                return match is null ? (null, "Choose one of the options.") : (JsonValue.Create(match), null);
            default:
                return (JsonValue.Create(text), null);
        }
    }

    private static bool Empty(JsonNode n) => n is JsonValue v && Tokens.Text(v).Trim().Length == 0;

    public static bool IsDay(string s) =>
        DayPattern().IsMatch(s) && DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    /// <summary>Plain text: control characters removed (a long answer keeps its line breaks and tabs).</summary>
    public static string Clean(string s, bool multiline)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (c == '\r') continue;
            if (multiline && c is '\n' or '\t') { sb.Append(c); continue; }
            if (char.IsControl(c) || c is '\u2028' or '\u2029' or '\u200b' or '\u202e' or '\u202d') { if (!multiline && c is '\n' or '\t') sb.Append(' '); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Clip(string s, int max)
    {
        s = s.Trim();
        return s.Length <= max ? s : s[..max].TrimEnd();
    }

    /// <summary>What a submission hands its run.</summary>
    public static JsonObject TriggerData(string formId, FormSpec form, JsonObject answers, FormPerson? person, DateTime at) => new()
    {
        ["form"] = new JsonObject { ["id"] = formId, ["title"] = form.Title },
        ["answers"] = answers.DeepClone(),
        ["submittedBy"] = person is null ? null : PersonNode(person),
        ["submittedAt"] = Formatter.IsoUtc(at),
    };

    /// <summary>The example a test runs with: each question's answer made up from its type.</summary>
    public static JsonObject Sample(JsonObject config, DateTime now)
    {
        var form = Read(config);
        var answers = new JsonObject();
        foreach (var q in form.Questions)
            answers[q.Key] = q.Type switch
            {
                "person" => PersonNode(new FormPerson("vid_sample", "Alex Morgan", "alex@example.com")),
                "email" => "ada@example.com",
                "number" => 3,
                "date" => now.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                "dateRange" => new JsonObject
                {
                    ["start"] = now.AddDays(35).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    ["end"] = now.AddDays(38).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                },
                "choice" => q.Options.FirstOrDefault() ?? "",
                "checkbox" => true,
                "longText" => "Back on the 29th. Ruth is covering the Atlas account.",
                _ => q.Label.Length > 0 ? "Sample answer" : "",
            };
        return TriggerData("frm_sample", form, answers,
            form.Public ? null : new FormPerson("vid_sample", "Alex Morgan", "alex@example.com"), now);
    }
}
