using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Data;

namespace VantraFlow.Api.Controllers;

/// <summary>
/// Settings → Profile: your Vantra account, the same in Flow, Orbit and Projects (<see cref="ProfileStore"/>).
/// <code>
/// GET api/me/profile → { firstName, lastName, email, photo, timeZone, jobTitle, personal, jobTitleReady }
/// PUT api/me/profile { firstName, lastName, jobTitle?, timeZone?, photo? } → the same, as saved
/// </code>
/// The email is read only (it's changed in Projects, which confirms the new address). <c>photo</c>: a PNG, JPEG or WebP
/// data URL of 400,000 characters at most, "" to remove it, left out to keep it. <c>timeZone</c>: an IANA zone, kept in
/// the identity's shape ("Europe/London (GMT)"). Photo and time zone wait for the identity's personal columns
/// (<c>personal</c>); the job title is kept in Flow (db/016, <c>jobTitleReady</c>), as Orbit and Projects each keep
/// their own.
/// </summary>
public sealed class ProfileController(IWorkspaceStore workspaces, IProfileStore profiles, IJobTitleStore titles) : FlowControllerBase(workspaces)
{
    [HttpGet("api/me/profile")]
    public async Task<IActionResult> Get()
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        return await AnswerAsync();
    }

    [HttpPut("api/me/profile")]
    [EnableRateLimiting(MembersController.RateLimitPolicy)]
    public async Task<IActionResult> Put([FromBody] ProfileBody? body)
    {
        if (string.IsNullOrWhiteSpace(Sub)) return Unauthorized();
        if (await profiles.GetAsync(Sub) is null) return NotFound(new { error = "Your Vantra account couldn't be found." });
        var (clean, error) = ProfileRules.Check(body);
        if (error is not null) return BadRequest(new { error });
        await profiles.SaveNameAsync(Sub, clean!.FirstName, clean.LastName);
        if (await profiles.PersonalReadyAsync()) await profiles.SavePersonalAsync(Sub, clean.Photo, clean.TimeZone);
        if (clean.JobTitle is not null && await titles.ReadyAsync()) await titles.SaveJobTitleAsync(Sub, clean.JobTitle.Length == 0 ? null : clean.JobTitle);
        return await AnswerAsync();
    }

    private async Task<IActionResult> AnswerAsync()
    {
        if (await profiles.GetAsync(Sub) is not { } p) return NotFound(new { error = "Your Vantra account couldn't be found." });
        var titleReady = await titles.ReadyAsync();
        return Ok(new
        {
            firstName = p.FirstName ?? "", lastName = p.LastName ?? "", email = p.Email, photo = p.Avatar, timeZone = ProfileRules.ZoneOf(p.Timezone),
            jobTitle = titleReady ? await titles.JobTitleAsync(Sub) : null, personal = await profiles.PersonalReadyAsync(), jobTitleReady = titleReady,
        });
    }

    public sealed record ProfileBody(string? FirstName, string? LastName, string? JobTitle, string? TimeZone, string? Photo);
}

/// <summary>The job title kept in Flow (db/016; <see cref="NotificationsRepository"/>, a fake in tests).</summary>
public interface IJobTitleStore
{
    Task<bool> ReadyAsync();
    Task<string?> JobTitleAsync(string vantraId);
    Task SaveJobTitleAsync(string vantraId, string? title);
}

/// <summary>A profile as checked: trimmed, the zone in the identity's shape; null where it's left as it is.</summary>
public sealed record CleanProfile(string FirstName, string LastName, string? JobTitle, string? TimeZone, string? Photo);

public static class ProfileRules
{
    public const int MaxName = 50;
    public const int MaxTitle = 100;
    public const int MaxPhoto = 400_000;

    public static (CleanProfile? Profile, string? Error) Check(ProfileController.ProfileBody? body)
    {
        var first = (body?.FirstName ?? "").Trim();
        var last = (body?.LastName ?? "").Trim();
        if (first.Length == 0) return (null, "Enter your first name.");
        if (first.Length > MaxName || last.Length > MaxName) return (null, $"Names are {MaxName} characters at most.");
        if (first.Any(char.IsControl) || last.Any(char.IsControl)) return (null, "Enter your name as plain text.");
        var title = body?.JobTitle?.Trim();
        if (title is { Length: > MaxTitle }) return (null, $"Job titles are {MaxTitle} characters at most.");
        if (title is not null && title.Any(char.IsControl)) return (null, "Enter your job title as plain text.");
        string? zone = null;
        if (body?.TimeZone is { } z)
        {
            var id = z.Trim();
            if (id.Length > 0)
            {
                if (Label(id) is not { } label) return (null, "Choose a time zone from the list.");
                zone = label;
            }
            else zone = "";
        }
        var photo = body?.Photo;
        if (photo is { Length: > 0 } && Services.Workspaces.LogoValidator.ImageProblem(photo, MaxPhoto, "photo", "use an image under about 300 KB") is { } bad)
            return (null, bad);
        return (new CleanProfile(first, last, title, zone, photo), null);
    }

    // Projects' picker's own labels, so the same choice reads the same there.
    private static readonly Dictionary<string, string> Known = new(StringComparer.Ordinal)
    {
        ["Europe/London"] = "GMT", ["Europe/Paris"] = "CET", ["America/New_York"] = "EST", ["America/Los_Angeles"] = "PST", ["Asia/Singapore"] = "SGT",
    };

    /// <summary>An IANA zone as the identity keeps it, "Europe/London (GMT)"; null when it isn't a zone.</summary>
    public static string? Label(string iana)
    {
        var id = ZoneOf(iana) ?? "";
        if (id.Length is 0 or > 64 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '_' or '-' or '+'))) return null;
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return null;
        if (Known.TryGetValue(id, out var abbr)) return $"{id} ({abbr})";
        var off = tz.GetUtcOffset(DateTime.UtcNow);
        var sign = off < TimeSpan.Zero ? "-" : "+";
        var label = off == TimeSpan.Zero ? "GMT" : $"GMT{sign}{Math.Abs(off.Hours)}{(off.Minutes != 0 ? $":{Math.Abs(off.Minutes):00}" : "")}";
        return $"{id} ({label})";
    }

    /// <summary>The IANA zone of a stored label ("Europe/London (GMT)" → "Europe/London").</summary>
    public static string? ZoneOf(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored)) return null;
        var s = stored.Trim();
        var i = s.IndexOf(" (", StringComparison.Ordinal);
        return i > 0 ? s[..i] : s;
    }
}
