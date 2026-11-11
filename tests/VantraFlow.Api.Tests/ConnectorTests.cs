using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.OAuth;
using Xunit;

namespace VantraFlow.Api.Tests;

internal sealed class NoHttp : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => throw new InvalidOperationException("No calls in tests.");
}

public sealed class ConnectorTests
{
    private static IConfiguration Config(params (string, string)[] pairs) =>
        new ConfigurationBuilder().AddInMemoryCollection(pairs.ToDictionary(p => p.Item1, p => (string?)p.Item2)).Build();

    [Fact]
    public void The_connectors_migration_is_one_azure_ready_batch_with_its_probe_index_last()
    {
        var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "db", "004_oauth_connectors.sql"));
        Assert.DoesNotMatch(new Regex(@"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"^\s*USE\s", RegexOptions.Multiline | RegexOptions.IgnoreCase), sql);
        foreach (var parser in new TSqlParser[] { new TSql160Parser(true), new TSql170Parser(true) })
        {
            parser.Parse(new StringReader(sql), out var errors);
            Assert.Empty(errors);
        }
        var outside = Regex.Replace(sql, @"EXEC\(N'(?:[^']|'')*'\);", "EXEC(...);");
        Assert.DoesNotMatch(new Regex(@"\b(CREATE\s+(UNIQUE\s+)?(TABLE|INDEX)|ALTER\s+TABLE)\b", RegexOptions.IgnoreCase), outside);
        var last = Regex.Matches(sql, @"CREATE\s+(?:UNIQUE\s+)?INDEX\s+(\w+)", RegexOptions.IgnoreCase).Last().Groups[1].Value;
        Assert.Equal("IX_PollCursors_Due", last);
        Assert.Contains("IX_PollCursors_Due", VantraFlow.Api.Data.FlowSchema.ConnectorsProbeSql);
        Assert.DoesNotContain("definition", VantraFlow.Api.Data.FlowSchema.ConnectorsProbeSql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("check_constraints", sql, StringComparison.OrdinalIgnoreCase);
        // The new event sources, so a poll's or Slack's event can be queued.
        foreach (var source in new[] { "slack", "microsoft", "google" }) Assert.Contains($"N''{source}''", sql);
    }

    [Fact]
    public void Slack_signatures_are_checked_and_stale_ones_refused()
    {
        const string secret = "8f742231b10e8888abcd99yyyzzz85a5";
        const string body = "{\"type\":\"event_callback\"}";
        var now = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var ts = new DateTimeOffset(now).ToUnixTimeSeconds();
        var sig = SlackSignature.Sign(secret, ts, body);
        Assert.True(SlackSignature.Verify(secret, sig, ts.ToString(), Encoding.UTF8.GetBytes(body), now));
        Assert.False(SlackSignature.Verify(secret, sig, ts.ToString(), Encoding.UTF8.GetBytes(body + " "), now));
        Assert.False(SlackSignature.Verify("another-secret", sig, ts.ToString(), Encoding.UTF8.GetBytes(body), now));
        Assert.False(SlackSignature.Verify(secret, sig, ts.ToString(), Encoding.UTF8.GetBytes(body), now.AddMinutes(6)));
        Assert.False(SlackSignature.Verify(secret, null, ts.ToString(), Encoding.UTF8.GetBytes(body), now));
        Assert.False(SlackSignature.Verify(secret, "v0=zz", ts.ToString(), Encoding.UTF8.GetBytes(body), now));
    }

    [Fact]
    public void Slack_messages_become_trigger_data_and_edits_do_not()
    {
        var ev = new JsonObject { ["type"] = "message", ["channel"] = "C1", ["user"] = "U1", ["text"] = "Hello", ["ts"] = "1794387600.000100" };
        var data = OAuthController.SlackMessage(ev)!;
        Assert.Equal("Hello", data["text"]!.GetValue<string>());
        Assert.Equal("2026-11-11T09:00:00Z", data["sentAt"]!.GetValue<string>());
        Assert.False(data["isReply"]!.GetValue<bool>());
        ev["subtype"] = "message_changed";
        Assert.Null(OAuthController.SlackMessage(ev));
        ev["subtype"] = "channel_join";
        Assert.Null(OAuthController.SlackMessage(ev));
    }

    [Fact]
    public void Messages_Flows_own_Slack_app_posted_are_marked_as_Flows()
    {
        Assert.True(OAuthController.IsOwnMessage(new JsonObject { ["user"] = "UBOT" }, "UBOT:A123"));
        Assert.True(OAuthController.IsOwnMessage(new JsonObject { ["user"] = "U9", ["app_id"] = "A123" }, "UBOT:A123"));
        Assert.True(OAuthController.IsOwnMessage(new JsonObject { ["bot_profile"] = new JsonObject { ["app_id"] = "A123" } }, "UBOT:A123"));
        Assert.False(OAuthController.IsOwnMessage(new JsonObject { ["user"] = "U9" }, "UBOT:A123"));
        Assert.False(OAuthController.IsOwnMessage(new JsonObject { ["user"] = "UBOT" }, null));
    }

    private static FlowEventRow SlackEvent(JsonObject data, string channel = "C1", bool flowCaused = false) =>
        new(1, "fev_1", Guid.NewGuid(), EventSources.Slack, "message", channel, null, data.ToJsonString(), flowCaused, false, null, DateTime.UtcNow, 1);

    [Fact]
    public void A_slack_trigger_hears_its_channel_and_skips_replies_bots_and_its_own_posts_unless_asked()
    {
        var def = new FlowDefinition
        {
            Trigger = new TriggerDef { App = "slack", Event = "message", Config = new JsonObject { ["channel"] = "C1" } },
            Steps = [Build.Http("s1")],
        };
        var msg = OAuthController.SlackMessage(new JsonObject { ["type"] = "message", ["channel"] = "C1", ["user"] = "U1", ["text"] = "Hi", ["ts"] = "1.1" })!;
        Assert.NotNull(TriggerMatcher.Match(def, "flw", SlackEvent(msg), JsonNode.Parse(msg.ToJsonString())));
        Assert.Null(TriggerMatcher.Match(def, "flw", SlackEvent(msg, "C2"), JsonNode.Parse(msg.ToJsonString())));
        Assert.Null(TriggerMatcher.Match(def, "flw", SlackEvent(msg, flowCaused: true), JsonNode.Parse(msg.ToJsonString())));

        var reply = OAuthController.SlackMessage(new JsonObject { ["type"] = "message", ["channel"] = "C1", ["text"] = "Re", ["ts"] = "2.2", ["thread_ts"] = "1.1" })!;
        Assert.Null(TriggerMatcher.Match(def, "flw", SlackEvent(reply), JsonNode.Parse(reply.ToJsonString())));
        def.Trigger.Config["replies"] = true;
        Assert.NotNull(TriggerMatcher.Match(def, "flw", SlackEvent(reply), JsonNode.Parse(reply.ToJsonString())));

        var bot = OAuthController.SlackMessage(new JsonObject { ["type"] = "message", ["subtype"] = "bot_message", ["channel"] = "C1", ["bot_id"] = "B1", ["text"] = "Beep", ["ts"] = "3.3" })!;
        Assert.Null(TriggerMatcher.Match(def, "flw", SlackEvent(bot), JsonNode.Parse(bot.ToJsonString())));
        def.Trigger.Config["bots"] = true;
        Assert.NotNull(TriggerMatcher.Match(def, "flw", SlackEvent(bot), JsonNode.Parse(bot.ToJsonString())));
    }

    [Fact]
    public void A_gmail_message_cant_carry_a_header_in_its_subject()
    {
        var raw = OAuthActions.Mime(["ada@example.com"], [], "Hello\r\nBcc: evil@example.com", "<p>Hi</p>");
        var mime = Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(raw));
        Assert.DoesNotContain("Bcc:", mime);
        Assert.StartsWith("To: ada@example.com\r\n", mime);
        var subject = Regex.Match(mime, @"Subject: =\?UTF-8\?B\?([^?]+)\?=").Groups[1].Value;
        Assert.Equal("Hello Bcc: evil@example.com", Encoding.UTF8.GetString(Convert.FromBase64String(subject)));
    }

    [Theory]
    [InlineData("https://docs.google.com/spreadsheets/d/1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789/edit#gid=0", "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789", "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789")]
    [InlineData("not a sheet", null)]
    public void Spreadsheet_ids_come_from_links_or_as_they_are(string raw, string? expected)
    {
        Assert.Equal(expected, OAuthActions.SpreadsheetId(raw));
    }

    [Fact]
    public void Sheet_ranges_quote_names_and_columns_count_past_z()
    {
        Assert.Equal("'Q4 ''leads'''!A1:ZZ10", OAuthActions.A1("Q4 'leads'", "A1:ZZ10"));
        Assert.Equal("A", OAuthActions.Column(1));
        Assert.Equal("Z", OAuthActions.Column(26));
        Assert.Equal("AA", OAuthActions.Column(27));
        Assert.Equal("BA", OAuthActions.Column(53));
        Assert.Equal(("Ada Lovelace", "ada@example.com"), OAuthPollSource.Address("\"Ada Lovelace\" <ada@example.com>"));
        Assert.Equal(("", "ada@example.com"), OAuthPollSource.Address("ada@example.com"));
    }

    [Fact]
    public void Polls_back_off_doubling_to_an_hour_and_respect_the_providers_wait()
    {
        var interval = TimeSpan.FromMinutes(2);
        Assert.Equal(TimeSpan.FromMinutes(4), PollTriggers.Backoff(interval, 1));
        Assert.Equal(TimeSpan.FromMinutes(16), PollTriggers.Backoff(interval, 3));
        Assert.Equal(TimeSpan.FromHours(1), PollTriggers.Backoff(interval, 20));
        Assert.Equal(TimeSpan.FromMinutes(10), PollTriggers.Backoff(interval, 1, TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromHours(1), PollTriggers.Backoff(interval, 1, TimeSpan.FromHours(5)));
    }

    [Fact]
    public void A_polled_item_has_one_event_id_per_flow_and_a_saved_trigger_starts_afresh()
    {
        Assert.Equal(PollTriggers.EventId("flw_a", "msg1"), PollTriggers.EventId("flw_a", "msg1"));
        Assert.NotEqual(PollTriggers.EventId("flw_a", "msg1"), PollTriggers.EventId("flw_b", "msg1"));
        Assert.True(PollTriggers.EventId("flw_a", "msg1").Length <= 40);
        var at = new DateTime(2026, 11, 11, 9, 0, 0, DateTimeKind.Utc);
        var cfg = new JsonObject { ["sheet"] = "Leads" };
        Assert.Equal(PollTriggers.Fingerprint("sheets.row.new", cfg, at), PollTriggers.Fingerprint("sheets.row.new", cfg, at));
        Assert.NotEqual(PollTriggers.Fingerprint("sheets.row.new", cfg, at), PollTriggers.Fingerprint("sheets.row.new", cfg, at.AddSeconds(1)));
        Assert.NotEqual(PollTriggers.Fingerprint("sheets.row.new", cfg, at), PollTriggers.Fingerprint("sheets.row.new", new JsonObject { ["sheet"] = "Deals" }, at));
    }

    [Fact]
    public void A_provider_is_offered_only_once_its_settings_exist()
    {
        Assert.False(new GoogleProvider(new NoHttp(), Config()).Configured);
        Assert.False(new GoogleProvider(new NoHttp(), Config(("Connectors:Google:ClientId", "id"))).Configured);
        Assert.True(new GoogleProvider(new NoHttp(), Config(("Connectors:Google:ClientId", "id"), ("Connectors:Google:ClientSecret", "s"))).Configured);
        // Slack's trigger needs the signing secret too.
        Assert.False(new SlackProvider(new NoHttp(), Config(("Connectors:Slack:ClientId", "id"), ("Connectors:Slack:ClientSecret", "s"))).Configured);
        Assert.True(new SlackProvider(new NoHttp(), Config(("Connectors:Slack:ClientId", "id"), ("Connectors:Slack:ClientSecret", "s"), ("Connectors:Slack:SigningSecret", "x"))).Configured);
    }

    [Fact]
    public void Consent_urls_carry_the_shared_redirect_the_scopes_and_pkce()
    {
        var (verifier, challenge) = OAuthProvider.Pkce();
        Assert.Matches("^[A-Za-z0-9_-]{43}$", verifier);
        const string redirect = "https://vantraflow.azurewebsites.net/api/oauth/microsoft/callback";
        var ms = new MicrosoftProvider(new NoHttp(), Config(("Connectors:Microsoft:ClientId", "cid"), ("Connectors:Microsoft:ClientSecret", "s"))).AuthorizeUrl("st", redirect, challenge);
        Assert.StartsWith("https://login.microsoftonline.com/common/oauth2/v2.0/authorize?", ms);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(redirect), ms);
        Assert.Contains("code_challenge_method=S256", ms);
        Assert.Contains(Uri.EscapeDataString("Mail.Send"), ms);
        Assert.Contains("offline_access", ms);
        var google = new GoogleProvider(new NoHttp(), Config(("Connectors:Google:ClientId", "cid"))).AuthorizeUrl("st", redirect.Replace("microsoft", "google"), challenge);
        Assert.Contains("access_type=offline", google);
        Assert.Contains(Uri.EscapeDataString("https://www.googleapis.com/auth/spreadsheets"), google);
        var slack = new SlackProvider(new NoHttp(), Config(("Connectors:Slack:ClientId", "cid"))).AuthorizeUrl("st", redirect.Replace("microsoft", "slack"), null);
        Assert.Contains("scope=" + Uri.EscapeDataString("chat:write,"), slack);
        Assert.DoesNotContain("code_challenge", slack);
    }

    [Fact]
    public void The_new_steps_are_validated_and_name_the_apps_they_need()
    {
        var def = new FlowDefinition
        {
            Trigger = new TriggerDef { App = "google", Event = "sheets.row.new", Config = new JsonObject { ["spreadsheet"] = "1AbCdEfGhIjKlMnOpQrStUvWxYz0123456789" } },
            Steps =
            [
                new StepDef { Id = "s1", Type = StepTypes.Action, App = "slack", Action = "message.send", Config = new JsonObject { ["channel"] = "C1" } },
                new StepDef { Id = "s2", Type = StepTypes.Action, App = "microsoft", Action = "event.create", Config = new JsonObject { ["subject"] = "Call", ["start"] = "{{trigger.values.When}}" } },
            ],
        };
        var errors = DefinitionRules.Validate(def, new FlowLimits()).Select(e => $"{e.StepId}: {e.Message}").ToList();
        Assert.Contains(": The trigger needs the sheet.", errors);
        Assert.Contains("s1: Fill in the message.", errors);
        Assert.DoesNotContain(errors, e => e.StartsWith("s2"));
        Assert.Equal(["google", "slack", "microsoft"], FlowCatalog.ProvidersNeeded(def).ToArray());
        Assert.Empty(FlowCatalog.AppsNeeded(def));
    }
}
