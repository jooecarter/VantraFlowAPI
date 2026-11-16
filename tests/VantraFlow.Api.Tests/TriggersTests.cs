using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.Triggers;
using Xunit;

namespace VantraFlow.Api.Tests;

public sealed class FormRulesTests
{
    private static JsonObject Holiday(string access = "team") => new()
    {
        ["title"] = "Holiday request",
        ["description"] = "Ask for time off.",
        ["access"] = access,
        ["showInApps"] = true,
        ["questions"] = new JsonArray(
            new JsonObject { ["key"] = "whos_asking", ["label"] = "Who's asking", ["type"] = "person" },
            new JsonObject { ["key"] = "dates", ["label"] = "Dates", ["type"] = "dateRange", ["required"] = true },
            new JsonObject { ["key"] = "anything_we_should_know", ["label"] = "Anything we should know?", ["type"] = "longText" }),
    };

    [Theory]
    [InlineData("Who's asking", "whos_asking")]
    [InlineData("Anything we should know?", "anything_we_should_know")]
    [InlineData("  Café  Name ", "cafe_name")]
    [InlineData("2nd choice", "q_2nd_choice")]
    [InlineData("???", "q")]
    public void Keys_are_slugged_from_labels(string label, string key) => Assert.Equal(key, FormRules.KeyFrom(label));

    [Theory]
    [InlineData("Holiday request", "holiday-request")]
    [InlineData("Expenses — Q4 (UK)", "expenses-q4-uk")]
    [InlineData("", "form")]
    public void Slugs_come_from_the_title(string title, string slug) => Assert.Equal(slug, FormRules.SlugFrom(title));

    [Fact]
    public void Reading_keeps_keys_makes_missing_ones_unique_and_drops_a_person_from_a_public_form()
    {
        var c = Holiday();
        ((JsonArray)c["questions"]!).Add(new JsonObject { ["label"] = "Dates", ["type"] = "text" });
        ((JsonArray)c["questions"]!).Add(new JsonObject { ["label"] = "Size", ["type"] = "nonsense" });
        var team = FormRules.Read(c);
        Assert.Equal(["whos_asking", "dates", "anything_we_should_know", "dates_2", "size"], team.Questions.Select(q => q.Key));
        Assert.Equal("text", team.Questions[^1].Type);
        Assert.True(team.ShowInApps);
        Assert.Equal("holiday-request", team.Slug);
        Assert.Equal("Send", team.SubmitLabel);

        c["access"] = "public";
        var open = FormRules.Read(c);
        Assert.DoesNotContain(open.Questions, q => q.Type == "person");
        Assert.False(open.ShowInApps); // only team forms show in Orbit and Projects
    }

    [Fact]
    public void Checks_say_what_is_missing()
    {
        Assert.Empty(FormRules.Check(Holiday()));
        Assert.Contains("Give the form a title.", FormRules.Check(new JsonObject { ["questions"] = new JsonArray(new JsonObject { ["label"] = "A", ["type"] = "text" }) }));
        Assert.Contains("Add a question.", FormRules.Check(new JsonObject { ["title"] = "T" }));
        var many = new JsonArray();
        for (var i = 0; i < 21; i++) many.Add(new JsonObject { ["label"] = $"Q{i}", ["type"] = "text" });
        Assert.Contains("A form can have at most 20 questions.", FormRules.Check(new JsonObject { ["title"] = "T", ["questions"] = many }));
        var choice = new JsonObject { ["title"] = "T", ["questions"] = new JsonArray(new JsonObject { ["label"] = "Size", ["type"] = "choice", ["options"] = new JsonArray("S") }) };
        Assert.Contains("“Size” needs at least two options.", FormRules.Check(choice));
        var pub = Holiday("public");
        Assert.Contains("A person question is only for team members' forms.", FormRules.Check(pub));
        Assert.Contains("The link can use lower-case letters, numbers and hyphens.", FormRules.Check(new JsonObject
        {
            ["title"] = "T", ["slug"] = "Not A Slug", ["questions"] = new JsonArray(new JsonObject { ["label"] = "A", ["type"] = "text" }),
        }));
    }

    [Fact]
    public void The_definition_check_includes_the_form()
    {
        var def = new FlowDefinition { Trigger = new TriggerDef { App = "form", Event = "form.submitted", Config = new JsonObject() }, Steps = [Build.Email("s1")] };
        var errors = DefinitionRules.Validate(def, new FlowLimits());
        Assert.Contains(errors, e => e.Message == "Give the form a title.");
        def.Trigger.Config = Holiday();
        Assert.Empty(DefinitionRules.Validate(def, new FlowLimits()));
    }

    [Fact]
    public void Answers_are_checked_as_their_type_and_the_person_is_who_is_signed_in()
    {
        var form = FormRules.Read(Holiday());
        var me = new FormPerson("vid_1", "Alex Morgan", "alex@example.com");
        var sent = new JsonObject
        {
            ["whos_asking"] = new JsonObject { ["id"] = "vid_evil", ["name"] = "Someone else" },
            ["dates"] = new JsonObject { ["start"] = "2026-12-21", ["end"] = "2026-12-24" },
            ["anything_we_should_know"] = "Back on the 29th.\nRuth is covering.",
            ["unknown"] = "ignored",
        };
        var ok = FormRules.CheckAnswers(form, sent, me);
        Assert.True(ok.Ok);
        Assert.Equal("vid_1", ok.Answers["whos_asking"]!["id"]!.GetValue<string>());
        Assert.Equal("2026-12-24", ok.Answers["dates"]!["end"]!.GetValue<string>());
        Assert.Contains("\n", ok.Answers["anything_we_should_know"]!.GetValue<string>());
        Assert.False(ok.Answers.ContainsKey("unknown"));

        var missing = FormRules.CheckAnswers(form, new JsonObject(), me);
        Assert.Equal("Fill this in.", missing.Errors["dates"]);
        var backwards = FormRules.CheckAnswers(form, new JsonObject { ["dates"] = new JsonObject { ["start"] = "2026-12-24", ["end"] = "2026-12-21" } }, me);
        Assert.Equal("The end is before the start.", backwards.Errors["dates"]);
    }

    [Fact]
    public void Answers_are_plain_data_held_to_their_size()
    {
        var form = FormRules.Read(new JsonObject
        {
            ["title"] = "Contact", ["access"] = "public",
            ["questions"] = new JsonArray(
                new JsonObject { ["key"] = "email", ["label"] = "Email", ["type"] = "email", ["required"] = true },
                new JsonObject { ["key"] = "seats", ["label"] = "Seats", ["type"] = "number" },
                new JsonObject { ["key"] = "plan", ["label"] = "Plan", ["type"] = "choice", ["options"] = new JsonArray("Starter", "Pro") },
                new JsonObject { ["key"] = "note", ["label"] = "Note", ["type"] = "text" },
                new JsonObject { ["key"] = "terms", ["label"] = "Terms", ["type"] = "checkbox", ["required"] = true }),
        });
        var bad = FormRules.CheckAnswers(form, new JsonObject { ["email"] = "nope", ["seats"] = "lots", ["plan"] = "Gold", ["note"] = new string('x', 5001) }, null);
        Assert.Equal("Enter an email address.", bad.Errors["email"]);
        Assert.Equal("Enter a number.", bad.Errors["seats"]);
        Assert.Equal("Choose one of the options.", bad.Errors["plan"]);
        Assert.Equal("Use at most 5,000 characters.", bad.Errors["note"]);
        Assert.Equal("Tick this to go on.", bad.Errors["terms"]);

        var good = FormRules.CheckAnswers(form, new JsonObject
        {
            ["email"] = " ada@example.com ", ["seats"] = "1,200", ["plan"] = "Pro", ["note"] = "<script>alert(1)</script>\u0007 hi\nthere", ["terms"] = true,
        }, null);
        Assert.True(good.Ok);
        Assert.Equal("ada@example.com", good.Answers["email"]!.GetValue<string>());
        Assert.Equal(1200d, good.Answers["seats"]!.GetValue<double>());
        // Kept as text (never rendered as HTML anywhere), control characters out, one line for short text.
        Assert.Equal("<script>alert(1)</script> hi there", good.Answers["note"]!.GetValue<string>());
        Assert.Null(FormRules.TriggerData("frm_1", form, good.Answers, null, DateTime.UtcNow)["submittedBy"]);
    }

    [Fact]
    public void The_answers_altogether_are_clamped()
    {
        var qs = new JsonArray();
        for (var i = 0; i < 10; i++) qs.Add(new JsonObject { ["key"] = $"q{i}", ["label"] = $"Q{i}", ["type"] = "longText" });
        var form = FormRules.Read(new JsonObject { ["title"] = "Big", ["access"] = "public", ["questions"] = qs });
        var sent = new JsonObject();
        for (var i = 0; i < 10; i++) sent[$"q{i}"] = new string('a', 4_900);
        var r = FormRules.CheckAnswers(form, sent, null);
        Assert.False(r.Ok);
        Assert.Contains(r.Errors.Values, v => v == "The answers are too long altogether.");
    }

    [Fact]
    public void A_submission_hands_on_answers_who_and_when()
    {
        var form = FormRules.Read(Holiday());
        var data = FormRules.TriggerData("frm_1", form, new JsonObject { ["dates"] = "x" }, new FormPerson("vid_1", "Alex", "alex@example.com"), new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc));
        Assert.Equal("alex@example.com", Tokens.Render("{{trigger.submittedBy.email}}", new JsonObject { ["trigger"] = data }));
        Assert.Equal("2026-11-16T09:00:00Z", Tokens.Text(data["submittedAt"]));
        Assert.Equal("Holiday request", Tokens.Text(data["form"]!["title"]));
    }

    [Fact]
    public void Stamps_catch_bots_and_stale_pages()
    {
        var guard = new FormGuard(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = new string('k', 40) }).Build());
        var now = new DateTime(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
        var stamp = guard.IssueStamp("tok12345", now);
        Assert.Equal("fast", guard.CheckStamp("tok12345", stamp, now.AddSeconds(1)));
        Assert.Equal("ok", guard.CheckStamp("tok12345", stamp, now.AddSeconds(30)));
        Assert.Equal("stale", guard.CheckStamp("tok12345", stamp, now.AddHours(25)));
        Assert.Equal("bad", guard.CheckStamp("othertok", stamp, now.AddSeconds(30)));
        Assert.Equal("bad", guard.CheckStamp("tok12345", "123.abc", now));
        Assert.Equal("bad", guard.CheckStamp("tok12345", null, now));
        Assert.False(guard.CaptchaEnabled);
        var ip = guard.IpHash(System.Net.IPAddress.Parse("203.0.113.9"));
        Assert.Equal(16, ip!.Length);
        Assert.DoesNotContain("203", ip);
    }

    [Fact]
    public void Tokens_are_long_and_unguessable()
    {
        var a = FormLinks.NewToken();
        Assert.Equal(12, a.Length);
        Assert.Matches("^[a-z2-9]+$", a);
        Assert.NotEqual(a, FormLinks.NewToken());
    }
}

public sealed class DateRulesTests
{
    private static TriggerDef T(string app, JsonObject config, string evt = "date.relative") => new() { App = app, Event = evt, Config = config };

    private static DateSpec Spec(int amount, string unit, string direction, string time = "09:00", string zone = "Europe/London", JsonObject? condition = null) =>
        DateRules.Read(T("orbit", new JsonObject
        {
            ["tableId"] = "tbl_customers", ["field"] = "fld_renewal", ["amount"] = amount, ["unit"] = unit, ["direction"] = direction, ["time"] = time, ["zone"] = zone,
            ["condition"] = condition,
        }), "UTC");

    private static DateTime Utc(int y, int mo, int d, int h = 0, int mi = 0) => new(y, mo, d, h, mi, 0, DateTimeKind.Utc);

    [Fact]
    public void Three_days_before_a_date_is_nine_in_the_morning_local_on_either_side_of_a_clock_change()
    {
        var spec = Spec(3, "days", "before");
        // Winter (GMT): 09:00 local is 09:00 UTC.
        Assert.Equal(Utc(2026, 11, 23, 9), DateRules.FireAt(DateRules.Parse("2026-11-26")!.Value, spec));
        // Summer (BST): 09:00 local is 08:00 UTC.
        Assert.Equal(Utc(2026, 7, 7, 8), DateRules.FireAt(DateRules.Parse("2026-07-10")!.Value, spec));
        // Across the October change: the 28th is GMT, three days before is still 09:00 local (BST, 08:00 UTC).
        Assert.Equal(Utc(2026, 10, 22, 8), DateRules.FireAt(DateRules.Parse("2026-10-25")!.Value, spec));
    }

    [Fact]
    public void Other_zones_and_on_the_day()
    {
        var ny = Spec(0, "days", "on", "08:30", "America/New_York");
        Assert.Equal(Utc(2026, 12, 1, 13, 30), DateRules.FireAt(DateRules.Parse("2026-12-01")!.Value, ny));
        Assert.Equal("on@08:30", ny.OffsetKey);
        var syd = Spec(1, "weeks", "after", "09:00", "Australia/Sydney");
        Assert.Equal(Utc(2026, 12, 7, 22), DateRules.FireAt(DateRules.Parse("2026-12-01")!.Value, syd)); // 09:00 AEDT on the 8th
    }

    [Fact]
    public void A_time_the_clocks_skip_moves_on_and_one_they_repeat_is_the_first()
    {
        var london = DateRules.FindZone("Europe/London")!;
        // 29 March 2026: 01:00 → 02:00. 01:30 doesn't exist: it fires at 02:00 local (01:00 UTC).
        Assert.Equal(Utc(2026, 3, 29, 1), DateRules.ToUtc(new DateTime(2026, 3, 29, 1, 30, 0), london));
        // 25 October 2026: 01:30 happens twice; the first is BST (00:30 UTC).
        Assert.Equal(Utc(2026, 10, 25, 0, 30), DateRules.ToUtc(new DateTime(2026, 10, 25, 1, 30, 0), london));
    }

    [Fact]
    public void Date_only_and_date_and_time_values()
    {
        Assert.True(DateRules.Parse("2026-11-26")!.Value.IsDay);
        Assert.True(DateRules.Parse("2026-11-26T00:00:00")!.Value.IsDay); // Projects' due day
        var instant = DateRules.Parse("2026-11-26T14:30:00Z")!.Value;
        Assert.False(instant.IsDay);
        Assert.Equal("2026-11-26T14:30:00Z", instant.Key);
        Assert.Equal(Utc(2026, 11, 26, 14, 30), DateRules.Parse("2026-11-26T15:30:00+01:00")!.Value.Instant);
        Assert.Null(DateRules.Parse("soon"));
        Assert.Null(DateRules.Parse(null));

        // A date and time keeps its own time: two hours before is by the clock…
        Assert.Equal(Utc(2026, 11, 26, 12, 30), DateRules.FireAt(instant, Spec(2, "hours", "before")));
        // …and a day before keeps its local time across a clock change (14:30 BST on 24 Oct = 13:30 UTC).
        Assert.Equal(Utc(2026, 10, 24, 13, 30), DateRules.FireAt(DateRules.Parse("2026-10-25T14:30:00Z")!.Value, Spec(1, "days", "before")));
        // A date-only value with hours counts from the time of day.
        Assert.Equal(Utc(2026, 11, 26, 7), DateRules.FireAt(DateRules.Parse("2026-11-26")!.Value, Spec(2, "hours", "before")));
    }

    [Fact]
    public void The_scan_window_reaches_back_a_day_at_most_and_never_before_the_flow_was_switched_on()
    {
        var now = Utc(2026, 11, 16, 12);
        Assert.Equal(now.AddMinutes(-8), DateRules.WindowStart(now, now.AddMinutes(-3), now.AddDays(-30)));
        // The worker was down for three days: only the last 24 hours fire.
        Assert.Equal(now.AddHours(-24), DateRules.WindowStart(now, now.AddDays(-3), now.AddDays(-30)));
        // Switched on an hour ago: nothing older.
        Assert.Equal(now.AddHours(-1), DateRules.WindowStart(now, now.AddDays(-3), now.AddHours(-1)));
    }

    [Fact]
    public void The_range_asked_for_covers_the_offset()
    {
        var (from, to) = DateRules.ValueRange(Spec(3, "days", "before"), Utc(2026, 11, 16, 9), Utc(2026, 11, 16, 12));
        Assert.True(from <= new DateOnly(2026, 11, 19) && to >= new DateOnly(2026, 11, 19));
        Assert.True(to.DayNumber - from.DayNumber <= 6);
    }

    private static JsonObject Rec(string id, string? renewal, string status = "Active") => new()
    {
        ["id"] = id, ["fields"] = new JsonObject { ["Name"] = id, ["Renewal date"] = renewal, ["Status"] = status },
    };

    private static List<DateDue> Due(DateSpec spec, IEnumerable<JsonObject> rows, DateTime from, DateTime to, bool overdue = false) =>
        DateRules.Due(spec, overdue, rows, r => Tokens.Text(r["id"]), r => overdue ? r["dueDate"] : r["fields"]?["Renewal date"],
            r => r["done"] is JsonValue v && v.GetValue<bool>(),
            (r, v, at) => new JsonObject { ["record"] = r.DeepClone(), ["date"] = new JsonObject { ["value"] = v.Key }, ["scheduledFor"] = Formatter.IsoUtc(at) },
            from, to);

    /// <summary>A scan as the worker does it: what's due, each fired once through the ledger.</summary>
    private static List<string> Scan(HashSet<string> ledger, DateSpec spec, IEnumerable<JsonObject> rows, DateTime from, DateTime to, bool overdue = false) =>
        Due(spec, rows, from, to, overdue).Where(d => ledger.Add($"{d.RecordId}|{d.DateValue}|{spec.OffsetKey}")).Select(d => d.RecordId).ToList();

    [Fact]
    public void Each_date_fires_once_and_a_changed_date_moves_its_run()
    {
        var spec = Spec(3, "days", "before");
        var ledger = new HashSet<string>();
        var rows = new List<JsonObject> { Rec("harbour", "2026-11-26"), Rec("atlas", "2026-12-01"), Rec("blank", null) };
        // 23 Nov 09:00 is Harbour's time.
        Assert.Equal(["harbour"], Scan(ledger, spec, rows, Utc(2026, 11, 23, 8, 55), Utc(2026, 11, 23, 9, 1)));
        // The next scan overlaps the last: nothing fires twice.
        Assert.Empty(Scan(ledger, spec, rows, Utc(2026, 11, 23, 8, 58), Utc(2026, 11, 23, 9, 4)));
        // Harbour's renewal moves to 4 Dec: a new key, so it fires again on 1 Dec.
        rows[0] = Rec("harbour", "2026-12-04");
        Assert.Equal(["harbour"], Scan(ledger, spec, rows, Utc(2026, 12, 1, 8, 58), Utc(2026, 12, 1, 9, 1)));
    }

    [Fact]
    public void Only_if_is_checked_on_the_record()
    {
        var cond = new JsonObject
        {
            ["match"] = "all",
            ["rules"] = new JsonArray(new JsonObject { ["left"] = "{{trigger.record.fields.Status}}", ["op"] = "equals", ["right"] = "Active" }),
        };
        var spec = Spec(3, "days", "before", condition: cond);
        var due = Due(spec, [Rec("a", "2026-11-26"), Rec("b", "2026-11-26", "Cancelled")], Utc(2026, 11, 23, 8), Utc(2026, 11, 23, 10));
        Assert.Equal(["a"], due.Select(d => d.RecordId));
        Assert.Equal("2026-11-23T09:00:00Z", Tokens.Text(due[0].Data["scheduledFor"]));
    }

    [Fact]
    public void An_overdue_task_fires_the_morning_after_once_and_a_done_one_never()
    {
        var spec = DateRules.Read(T("projects", new JsonObject { ["time"] = "09:00", ["zone"] = "Europe/London" }, "task.overdue"), "UTC");
        Assert.Equal("after:1d@09:00", spec.OffsetKey);
        var tasks = new List<JsonObject>
        {
            new() { ["id"] = "t1", ["dueDate"] = "2026-11-19T00:00:00", ["done"] = false },
            new() { ["id"] = "t2", ["dueDate"] = "2026-11-19T00:00:00", ["done"] = true },
            new() { ["id"] = "t3", ["dueDate"] = "2026-11-20T00:00:00", ["done"] = false },
        };
        var ledger = new HashSet<string>();
        Assert.Equal(["t1"], Scan(ledger, spec, tasks, Utc(2026, 11, 20, 8), Utc(2026, 11, 20, 10), overdue: true));
        Assert.Empty(Scan(ledger, spec, tasks, Utc(2026, 11, 20, 9), Utc(2026, 11, 20, 12), overdue: true));
        // A new due date for t1 is a new key: overdue again the morning after it.
        tasks[0]["dueDate"] = "2026-11-24T00:00:00";
        Assert.Equal(["t1"], Scan(ledger, spec, tasks, Utc(2026, 11, 25, 8, 59), Utc(2026, 11, 25, 9, 1), overdue: true));
    }

    [Fact]
    public void Settings_are_checked()
    {
        Assert.Empty(DateRules.Check(T("orbit", new JsonObject { ["amount"] = 3, ["unit"] = "days", ["direction"] = "before", ["time"] = "09:00", ["zone"] = "Europe/London" })));
        Assert.Contains("Choose how long before or after: 1 to 3,650.", DateRules.Check(T("orbit", new JsonObject { ["amount"] = 0, ["direction"] = "after" })));
        Assert.Contains("Choose a time, like 09:00.", DateRules.Check(T("orbit", new JsonObject { ["time"] = "9am" })));
        Assert.Contains("Choose a time zone.", DateRules.Check(T("orbit", new JsonObject { ["zone"] = "Mars/Olympus" })));
        var def = new FlowDefinition { Trigger = T("orbit", new JsonObject { ["tableId"] = "tbl_1" }), Steps = [Build.Email("s1")] };
        Assert.Contains(DefinitionRules.Validate(def, new FlowLimits()), e => e.Message == "The trigger needs the date field.");
    }
}

/// <summary>Spends nonces in memory.</summary>
internal sealed class MemoryNonces : INonceStore
{
    private readonly HashSet<string> seen = [];
    public Task<bool> SpendAsync(string audience, string nonce) => Task.FromResult(seen.Add(audience + "|" + nonce));
}

public sealed class AppAssertionTests
{
    private const string Key = "a-shared-key-of-at-least-thirty-two-characters!";
    private static readonly Guid Ws = Guid.NewGuid();

    private static AppAssertion Make(string key = Key) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:Key"] = key }).Build(), new MemoryNonces());

    [Fact]
    public async Task A_fresh_assertion_for_this_app_verifies_once()
    {
        var a = Make();
        var token = AppAssertion.Sign(Key, "orbit", "vid_1", Ws, "Alex Morgan");
        var (caller, why) = await a.ReadAsync("orbit", token);
        Assert.Equal("ok", why);
        Assert.Equal("vid_1", caller!.VantraId);
        Assert.Equal(Ws, caller.AppWorkspaceId);
        Assert.Equal("orbit", caller.App);
        // The same token again is a replay.
        Assert.Equal(AppAssertionReason.Replayed, (await a.ReadAsync("orbit", token)).Reason);
    }

    [Fact]
    public async Task An_expired_one_another_apps_or_a_wrong_key_is_refused()
    {
        var a = Make();
        Assert.Equal(AppAssertionReason.Expired,
            (await a.ReadAsync("orbit", AppAssertion.Sign(Key, "orbit", "vid_1", Ws, now: DateTime.UtcNow.AddMinutes(-10)))).Reason);
        // Orbit's assertion can't be used as Projects'.
        Assert.Equal(AppAssertionReason.Audience, (await a.ReadAsync("projects", AppAssertion.Sign(Key, "orbit", "vid_1", Ws))).Reason);
        Assert.Equal(AppAssertionReason.Signature,
            (await a.ReadAsync("orbit", AppAssertion.Sign("another-key-entirely-thirty-two-characters-long", "orbit", "vid_1", Ws))).Reason);
        Assert.Equal(AppAssertionReason.Lifetime, (await a.ReadAsync("orbit", AppAssertion.Sign(Key, "orbit", "vid_1", Ws, life: TimeSpan.FromHours(1)))).Reason);
        Assert.Equal(AppAssertionReason.Claims, (await a.ReadAsync("orbit", AppAssertion.Sign(Key, "orbit", "vid_1", Guid.Empty))).Reason);
        Assert.Equal(AppAssertionReason.Claims, (await a.ReadAsync("orbit", AppAssertion.Sign(Key, "orbit", "vid_1", Ws, jti: "short"))).Reason);
        Assert.Equal(AppAssertionReason.Missing, (await a.ReadAsync("orbit", null)).Reason);
        Assert.Equal(AppAssertionReason.Audience, (await a.ReadAsync("slack", AppAssertion.Sign(Key, "orbit", "vid_1", Ws))).Reason);
        Assert.Equal(AppAssertionReason.NotConfigured, (await Make("short").ReadAsync("orbit", AppAssertion.Sign(Key, "orbit", "vid_1", Ws))).Reason);
    }

    [Fact]
    public async Task A_session_token_is_never_an_assertion()
    {
        // A Flow session (audience vantra-flow) signed with the same key.
        var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
        var session = handler.WriteToken(new System.IdentityModel.Tokens.Jwt.JwtSecurityToken("vantra", "vantra-flow",
            [new("sub", "vid_1"), new("app_ws", Ws.ToString()), new("jti", Guid.NewGuid().ToString("N"))], DateTime.UtcNow, DateTime.UtcNow.AddMinutes(2),
            new Microsoft.IdentityModel.Tokens.SigningCredentials(new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes(Key)), "HS256")));
        Assert.Equal(AppAssertionReason.Audience, (await Make().ReadAsync("orbit", session)).Reason);
    }
}

/// <summary>Flows, workspaces and links in memory.</summary>
internal sealed class FakeLinks : IAppLinkStore
{
    public readonly List<ConnectionRow> Connections = [];
    public readonly Dictionary<(Guid, string), string> Roles = [];
    public readonly List<FlowRow> Flows = [];

    public Task<IReadOnlyList<ConnectionRow>> LinkingAsync(string app, Guid appWorkspaceId) =>
        Task.FromResult<IReadOnlyList<ConnectionRow>>(Connections.Where(c => c.App == app && c.RemoteWorkspaceId == appWorkspaceId && c.Status != "revoked").ToList());
    public Task<string?> RoleAsync(Guid workspaceId, string vantraId) => Task.FromResult(Roles.GetValueOrDefault((workspaceId, vantraId)));
    public Task<IReadOnlyList<FlowRow>> ButtonFlowsAsync(IReadOnlyCollection<Guid> workspaceIds, string app) =>
        Task.FromResult<IReadOnlyList<FlowRow>>(Flows.Where(f => workspaceIds.Contains(f.WorkspaceId) && f.TriggerApp == app && f.TriggerEvent == "button.record" && f.Enabled).ToList());
    public Task<FlowRow?> FlowAsync(Guid workspaceId, string flowId) => Task.FromResult(Flows.FirstOrDefault(f => f.WorkspaceId == workspaceId && f.Id == flowId));

    public static ConnectionRow Link(Guid flowWs, string app, Guid appWs) =>
        new(Guid.NewGuid(), flowWs, app, appWs, "Northwind", "key_1", [], "active", null, "vid_owner", null, DateTime.UtcNow, null);

    public static FlowRow Button(string id, Guid ws, string app, string target, bool enabled = true, string name = "Send welcome pack", params StepDef[] steps)
    {
        var def = new FlowDefinition
        {
            Trigger = new TriggerDef { App = app, Event = "button.record", Config = app == "orbit" ? new JsonObject { ["tableId"] = target } : new JsonObject { ["target"] = target } },
            Steps = steps.Length > 0 ? steps.ToList() : [Build.Email("s1")],
        };
        return new FlowRow(id, ws, name, null, enabled, def.ToJson(), 1, app, "button.record", null, null, null, null, null, "u", DateTime.UtcNow, "u", DateTime.UtcNow, null, null);
    }
}

public sealed class AppLinksTests
{
    private static readonly Guid FlowWs = Guid.NewGuid();
    private static readonly Guid OrbitWs = Guid.NewGuid();
    private static AppCaller Caller(string vid = "vid_1", string app = "orbit", Guid? ws = null) => new(app, vid, ws ?? OrbitWs, Guid.NewGuid().ToString("N"), null);

    private static FakeLinks Store()
    {
        var s = new FakeLinks();
        s.Connections.Add(FakeLinks.Link(FlowWs, "orbit", OrbitWs));
        s.Roles[(FlowWs, "vid_1")] = "member";
        s.Flows.Add(FakeLinks.Button("flw_welcome", FlowWs, "orbit", "tbl_customers", steps: [Build.Email("s1"), Build.Http("s2")]));
        s.Flows.Add(FakeLinks.Button("flw_other_table", FlowWs, "orbit", "tbl_contacts", name: "Contacts flow"));
        s.Flows.Add(FakeLinks.Button("flw_off", FlowWs, "orbit", "tbl_customers", enabled: false, name: "Off flow"));
        s.Flows.Add(FakeLinks.Button("flw_projects", FlowWs, "projects", "task", name: "Projects flow"));
        return s;
    }

    [Fact]
    public async Task A_member_of_a_linked_workspace_sees_the_tables_button_flows()
    {
        var links = new AppLinks(Store());
        var list = await links.ButtonsAsync(Caller(), "tbl_customers");
        var f = Assert.Single(list);
        Assert.Equal("flw_welcome", f.Id);
        Assert.Equal("Send welcome pack", f.Label);
        Assert.Equal("Email · HTTP", f.Subtitle);
    }

    [Fact]
    public async Task Someone_who_isnt_a_member_or_a_workspace_that_isnt_linked_sees_nothing()
    {
        var links = new AppLinks(Store());
        Assert.Empty(await links.ButtonsAsync(Caller("vid_stranger"), "tbl_customers"));
        Assert.Empty(await links.ButtonsAsync(Caller(ws: Guid.NewGuid()), "tbl_customers"));
        Assert.Equal("not_linked", (await links.RunnableAsync(Caller("vid_stranger"), "flw_welcome", "tbl_customers")).Code);
    }

    [Fact]
    public async Task A_run_needs_the_flow_on_and_to_be_this_apps_button_for_this_table()
    {
        var links = new AppLinks(Store());
        Assert.Equal("flw_welcome", (await links.RunnableAsync(Caller(), "flw_welcome", "tbl_customers")).Flow!.Id);
        Assert.Equal("off", (await links.RunnableAsync(Caller(), "flw_off", "tbl_customers")).Code);
        Assert.Equal("wrong_target", (await links.RunnableAsync(Caller(), "flw_welcome", "tbl_contacts")).Code);
        Assert.Equal("not_button", (await links.RunnableAsync(Caller(), "flw_projects", "tbl_customers")).Code);
        Assert.Equal("no_flow", (await links.RunnableAsync(Caller(), "flw_nope", "tbl_customers")).Code);
        // A revoked connection links nothing.
        var store = Store();
        store.Connections[0] = store.Connections[0] with { Status = "revoked" };
        Assert.Equal("not_linked", (await new AppLinks(store).RunnableAsync(Caller(), "flw_welcome", "tbl_customers")).Code);
    }

    [Fact]
    public async Task Projects_asks_for_its_own_flows_only()
    {
        var store = Store();
        var projectsWs = Guid.NewGuid();
        store.Connections.Add(FakeLinks.Link(FlowWs, "projects", projectsWs));
        var links = new AppLinks(store);
        var list = await links.ButtonsAsync(Caller(app: "projects", ws: projectsWs), "task");
        Assert.Equal(["flw_projects"], list.Select(f => f.Id));
        // An Orbit-linked workspace id presented as Projects' links nothing.
        Assert.Empty(await links.ButtonsAsync(Caller(app: "projects"), "task"));
    }

    [Fact]
    public void Subtitles_come_from_the_steps_unless_set()
    {
        var def = new FlowDefinition { Trigger = new TriggerDef { App = "orbit", Event = "button.record", Config = new JsonObject { ["tableId"] = "t" } }, Steps = [Build.Delay("d1", 1)] };
        Assert.Equal("1 step", ButtonRules.Subtitle(def));
        def.Trigger.Config["subtitle"] = "Email · PDF · Slack note";
        Assert.Equal("Email · PDF · Slack note", ButtonRules.Subtitle(def));
        Assert.Equal("Run it", ButtonRules.Label(new TriggerDef { Config = new JsonObject { ["label"] = "Run it" } }, "Flow"));
    }
}

public sealed class TriggerRunsTests
{
    private static FlowRow Flow(bool enabled = true) => FakeLinks.Button("flw_1", Guid.NewGuid(), "orbit", "tbl_customers", enabled);

    [Fact]
    public async Task Starts_one_run_with_the_trigger_data()
    {
        var store = new FakeStore();
        var poked = 0;
        var r = await new TriggerRuns(store, new FlowLimits(), () => poked++).StartAsync(Flow(), new JsonObject { ["record"] = new JsonObject { ["id"] = "rec_1" } }, "btn:n:rec_1", DateTime.UtcNow);
        Assert.True(r.Started);
        var run = Assert.Single(store.Runs);
        Assert.Equal(RunStatuses.Queued, run.Status);
        Assert.Contains("rec_1", run.TriggerData);
        Assert.Equal(1, poked);
        // The same claim again starts nothing.
        Assert.Equal("duplicate", (await new TriggerRuns(store, new FlowLimits()).StartAsync(Flow(), new JsonObject(), "btn:n:rec_1", DateTime.UtcNow)).Code);
    }

    [Fact]
    public async Task A_switched_off_flow_the_loop_breaker_and_the_quota_stop_it()
    {
        var store = new FakeStore();
        Assert.Equal("off", (await new TriggerRuns(store, new FlowLimits()).StartAsync(Flow(enabled: false), new JsonObject(), null, DateTime.UtcNow)).Code);

        store.RecentForFlow = 60;
        Assert.Equal("paused", (await new TriggerRuns(store, new FlowLimits(FlowRunsPerMinute: 60)).StartAsync(Flow(), new JsonObject(), null, DateTime.UtcNow)).Code);
        Assert.Single(store.Paused);

        store.RecentForFlow = 0;
        store.Quota = 0;
        var form = await new TriggerRuns(store, new FlowLimits()).StartAsync(Flow(), new JsonObject(), null, DateTime.UtcNow);
        Assert.Equal("quota", form.Code);
        Assert.Empty(store.Runs); // a form or button is told, nothing is logged

        var date = await new TriggerRuns(store, new FlowLimits()).StartAsync(Flow(), new JsonObject(), null, DateTime.UtcNow, logSkipped: true);
        Assert.Equal("quota", date.Code);
        Assert.Equal(RunStatuses.Skipped, Assert.Single(store.Runs).Status); // a date's skipped run is logged
    }
}

public sealed class NewTriggerCatalogTests
{
    [Theory]
    [InlineData("form", "form.submitted")]
    [InlineData("orbit", "date.relative")]
    [InlineData("orbit", "button.record")]
    [InlineData("projects", "date.relative")]
    [InlineData("projects", "task.overdue")]
    [InlineData("projects", "button.record")]
    public void Each_new_trigger_is_in_the_catalog_with_a_description_and_its_own_sample(string app, string evt)
    {
        var entry = FlowCatalog.Trigger(app, evt);
        Assert.NotNull(entry);
        Assert.False(string.IsNullOrWhiteSpace(entry!.Description));
        var sample = FlowCatalog.Sample(new TriggerDef { App = app, Event = evt }, DateTime.UtcNow);
        Assert.NotEqual(FlowCatalog.Sample(new TriggerDef(), DateTime.UtcNow).ToJsonString(), sample.ToJsonString());
        // The AI builder hears of it.
        Assert.Contains($"{app} / {evt}", VantraFlow.Api.Services.Ai.PlannerCatalog.Describe(DateTime.UtcNow));
    }

    [Fact]
    public void A_forms_sample_answers_each_question()
    {
        var config = new JsonObject
        {
            ["title"] = "Holiday request",
            ["questions"] = new JsonArray(new JsonObject { ["key"] = "dates", ["label"] = "Dates", ["type"] = "dateRange" }, new JsonObject { ["key"] = "who", ["label"] = "Who", ["type"] = "person" }),
        };
        var sample = FlowCatalog.Sample(new TriggerDef { App = "form", Event = "form.submitted", Config = config }, DateTime.UtcNow);
        Assert.NotNull(sample["answers"]!["dates"]!["start"]);
        Assert.Equal("Alex Morgan", Tokens.Text(sample["answers"]!["who"]!["name"]));
        Assert.NotNull(sample["submittedBy"]);
    }

    [Fact]
    public void Buttons_and_date_triggers_need_no_webhook_but_do_need_their_app()
    {
        var def = new FlowDefinition { Trigger = new TriggerDef { App = "orbit", Event = "button.record", Config = new JsonObject { ["tableId"] = "tbl_1" } }, Steps = [Build.Email("s1")] };
        Assert.Contains("orbit", FlowCatalog.AppsNeeded(def));
        Assert.Null(TriggerMatcher.Match(def, "flw_1", new FlowEventRow(1, "fev_1", Guid.NewGuid(), "orbit", "record.created", "tbl_1", null, "{}", false, false, null, DateTime.UtcNow, 1), new JsonObject()));
    }
}

public sealed class TriggersMigrationTests
{
    [Fact]
    public void Db014_is_one_azure_ready_idempotent_batch_with_the_probes_index_last()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "014_triggers.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(CREATE\s+(UNIQUE\s+)?INDEX|CREATE\s+TABLE|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase), outside);
        Assert.True(sql.IndexOf("THROW 51014", StringComparison.Ordinal) < sql.IndexOf("EXEC(N'", StringComparison.Ordinal));
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_DateCursors_Due", last);
        Assert.Contains("IX_DateCursors_Due", VantraFlow.Api.Data.TriggersRepository.ProbeSql);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.TriggersRepository.ProbeSql, StringComparison.OrdinalIgnoreCase);
        // Each table and index is guarded, so the batch re-runs.
        Assert.Equal(Regex.Matches(sql, @"CREATE\s+TABLE", RegexOptions.IgnoreCase).Count, Regex.Matches(sql, @"IF OBJECT_ID\(N'flow\.\w+', N'U'\) IS NULL\s+EXEC").Count);
        Assert.Equal(Regex.Matches(sql, @"CREATE\s+(UNIQUE\s+)?INDEX", RegexOptions.IgnoreCase).Count, Regex.Matches(sql, @"IF NOT EXISTS \(SELECT 1 FROM sys\.indexes").Count);
    }
}
