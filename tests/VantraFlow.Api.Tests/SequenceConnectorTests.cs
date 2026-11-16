using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Models;
using VantraFlow.Api.Services.Ai;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using Xunit;

namespace VantraFlow.Api.Tests;

/// <summary>
/// Vantra Orbit's email sequences in Flow: the "Someone replied to a sequence" (and "Booked a meeting", "Finished without a
/// reply") triggers and the "Add to sequence" action — their catalog entries, the AI planner's view of them, the scope a flow
/// subscribes on, the matching of Orbit's sequence events, and the calls to Orbit, including Orbit before it has sequences.
/// </summary>
public sealed class SequenceConnectorTests
{
    private static FlowEventRow Ev(string kind, string? scope, JsonObject payload) =>
        new(1, "fev_1", Guid.Empty, "orbit", kind, scope, null, payload.ToJsonString(), false, false, null, DateTime.UtcNow, 1);

    private static FlowDefinition Flow(string ev, string? sequenceId = null)
    {
        var def = new FlowDefinition();
        def.Trigger = new TriggerDef { App = "orbit", Event = ev, Config = sequenceId is null ? new JsonObject() : new JsonObject { ["sequenceId"] = sequenceId } };
        return def;
    }

    private static JsonObject Replied(string sequenceId = "seq_1") => new()
    {
        ["id"] = "sqx_1", ["type"] = "sequence.replied", ["sequence"] = new JsonObject { ["id"] = sequenceId, ["name"] = "New lead follow-up" },
        ["enrollment"] = new JsonObject { ["id"] = "sqe_1", ["state"] = "replied" }, ["record"] = new JsonObject { ["id"] = "rec_1", ["tableId"] = "tbl_contacts" },
        ["tableId"] = "tbl_contacts", ["email"] = "ada@example.com", ["repliedAt"] = "2026-11-19T09:12:00.000Z",
    };

    [Fact]
    public void The_catalog_has_the_sequence_triggers_and_the_action()
    {
        var replied = FlowCatalog.Trigger("orbit", "sequence.replied");
        Assert.NotNull(replied);
        Assert.Equal("Someone replied to a sequence", replied!.Title);
        Assert.Empty(replied.Required); // any sequence when none is chosen
        Assert.NotNull(FlowCatalog.Trigger("orbit", "sequence.booked"));
        Assert.NotNull(FlowCatalog.Trigger("orbit", "sequence.finished"));
        var add = FlowCatalog.Action("orbit", "sequence.add");
        Assert.NotNull(add);
        Assert.Equal("Add to sequence", add!.Title);
        Assert.Equal(["sequenceId"], add.Required);
        Assert.False(add.Premium);
        // Orbit is a connected app for these, as for its record triggers.
        var def = Flow("sequence.replied");
        Assert.Contains("orbit", FlowCatalog.AppsNeeded(def));
    }

    [Fact]
    public void The_sample_and_the_planner_describe_the_sequence_data_without_any_mail_content()
    {
        var sample = FlowCatalog.Sample(new TriggerDef { App = "orbit", Event = "sequence.replied", Config = new JsonObject { ["sequenceId"] = "seq_9" } }, DateTime.UtcNow);
        Assert.Equal("seq_9", sample["sequence"]!["id"]!.GetValue<string>());
        Assert.NotNull(sample["repliedAt"]);
        Assert.NotNull(sample["record"]!["id"]);
        Assert.Null(sample["subject"]);
        Assert.Null(sample["snippet"]);
        Assert.NotNull(FlowCatalog.Sample(new TriggerDef { App = "orbit", Event = "sequence.booked" }, DateTime.UtcNow)["bookedAt"]);
        var text = FlowPlanner.Instructions(new PlannerContext([], [], [], [], "UTC", null, [new PlannerProject("seq_1", "New lead follow-up")]), DateTime.UtcNow);
        Assert.Contains("- orbit / sequence.replied — Someone replied to a sequence.", text);
        Assert.Contains("{{trigger.repliedAt}}", text);
        Assert.Contains("- orbit / sequence.add — Add to sequence.", text);
        Assert.Contains("sequenceId* (a sequence)", text);
        Assert.Contains("seq_1: New lead follow-up", text);
    }

    [Fact]
    public void A_sequence_trigger_subscribes_on_its_sequence_or_on_every_sequence()
    {
        Assert.Equal("seq:seq_1", TriggerMatcher.SequenceScope(Flow("sequence.replied", "seq_1").Trigger));
        Assert.Equal("seq:*", TriggerMatcher.SequenceScope(Flow("sequence.replied").Trigger));
        Assert.Equal("seq:*", TriggerMatcher.SequenceScope(Flow("sequence.replied", " ").Trigger));
        var row = new FlowRow("f", Guid.Empty, "n", null, false, "{}", 1, "", "", null, null, null, null, null, "u", DateTime.UtcNow, "u", DateTime.UtcNow, null, null);
        Assert.Equal("seq:seq_1", FlowsController.Shape(row, Flow("sequence.replied", "seq_1")).TriggerScope);
        Assert.Equal("seq:*", FlowsController.Shape(row, Flow("sequence.finished")).TriggerScope);
        Assert.Equal(FlowCatalog.SequenceEvents, OrbitApi.EventsFor("seq:*"));
        Assert.Equal(OrbitApi.Events, OrbitApi.EventsFor("tbl_contacts"));
    }

    [Fact]
    public void Sequence_events_start_the_flows_listening_on_their_scope_and_event()
    {
        var data = OrbitApi.SequenceTriggerData(Replied());
        Assert.Equal("sequence.replied", data["event"]!.GetValue<string>());
        Assert.Equal("rec_1", data["record"]!["id"]!.GetValue<string>());
        Assert.Equal("2026-11-19T09:12:00.000Z", data["repliedAt"]!.GetValue<string>());
        // Any sequence: the event that came on the "every sequence" subscription.
        Assert.NotNull(TriggerMatcher.Match(Flow("sequence.replied"), "f", Ev("sequence.replied", "seq:*", data), data));
        // One sequence: only its own subscription's events.
        Assert.NotNull(TriggerMatcher.Match(Flow("sequence.replied", "seq_1"), "f", Ev("sequence.replied", "seq:seq_1", data), data));
        Assert.Null(TriggerMatcher.Match(Flow("sequence.replied", "seq_1"), "f", Ev("sequence.replied", "seq:*", data), data));
        Assert.Null(TriggerMatcher.Match(Flow("sequence.replied", "seq_2"), "f", Ev("sequence.replied", "seq:seq_1", data), data));
        // Another event of the same subscription doesn't start a Replied flow; a Booked one starts on its own.
        Assert.Null(TriggerMatcher.Match(Flow("sequence.replied"), "f", Ev("sequence.booked", "seq:*", data), data));
        Assert.NotNull(TriggerMatcher.Match(Flow("sequence.booked"), "f", Ev("sequence.booked", "seq:*", data), data));
        // A record event never starts a sequence flow, and a sequence event never a record one.
        Assert.Null(TriggerMatcher.Match(Flow("sequence.replied"), "f", Ev("record.created", "seq:*", data), data));
        var recordFlow = new FlowDefinition { Trigger = new TriggerDef { App = "orbit", Event = "record.created", Config = new JsonObject { ["tableId"] = "tbl_contacts" } } };
        Assert.Null(TriggerMatcher.Match(recordFlow, "f", Ev("sequence.replied", "seq:*", data), data));
    }

    // ── The calls to Orbit ──

    private sealed class Orbit(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer) : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Body, string? Auth)> Calls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request.Method.Method, request.RequestUri!.AbsolutePath, body, request.Headers.Authorization?.ToString()));
            var (code, json) = answer(request, body);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (OrbitApi Api, Orbit Handler) Api(Func<HttpRequestMessage, string, (HttpStatusCode, string)> answer)
    {
        var handler = new Orbit(answer);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Connectors:Orbit:ApiUrl"] = "https://orbit.example.com" }).Build();
        return (new OrbitApi(new Factory(handler), config), handler);
    }

    private const string Off = """{"error":{"code":"sequences_off","message":"Sequences aren't switched on in this Orbit workspace yet."}}""";

    [Fact]
    public async Task Add_to_sequence_sends_the_record_with_the_connection_key_and_hands_on_the_enrolment()
    {
        var (api, h) = Api((_, _) => (HttpStatusCode.OK, """{"enrollmentId":"sqe_9","state":"active","recordId":"rec_1","skipped":false}"""));
        var o = await api.ActAsync("orbk_live_key", "sequence.add", new JsonObject { ["sequenceId"] = "seq_1", ["recordId"] = "rec_1", ["tableId"] = "tbl_contacts" }, "idem-1", CancellationToken.None);
        Assert.Equal("POST", h.Calls[0].Method);
        Assert.Equal("/api/v1/sequences/seq_1/enrollments", h.Calls[0].Path);
        Assert.Equal("Bearer orbk_live_key", h.Calls[0].Auth);
        Assert.Contains("\"recordId\":\"rec_1\"", h.Calls[0].Body);
        Assert.Equal("sqe_9", o!["enrollmentId"]!.GetValue<string>());
        Assert.False(o["skipped"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Someone_who_was_skipped_is_handed_on_with_the_reason_not_a_failure()
    {
        var (api, _) = Api((_, _) => (HttpStatusCode.OK, """{"skipped":true,"reason":"in_sequence","message":"They're already in a sequence.","recordId":"rec_1"}"""));
        var o = await api.ActAsync("k", "sequence.add", new JsonObject { ["sequenceId"] = "seq_1", ["email"] = "ada@example.com" }, "idem", CancellationToken.None);
        Assert.True(o!["skipped"]!.GetValue<bool>());
        Assert.Equal("in_sequence", o["reason"]!.GetValue<string>());
        Assert.Equal("skipped", o["state"]!.GetValue<string>());
        await Assert.ThrowsAsync<AppRefusedException>(() => api.ActAsync("k", "sequence.add", new JsonObject { ["sequenceId"] = "seq_1" }, "i", CancellationToken.None));
        await Assert.ThrowsAsync<AppRefusedException>(() => api.ActAsync("k", "sequence.add", new JsonObject { ["recordId"] = "rec_1" }, "i", CancellationToken.None));
    }

    [Fact]
    public async Task A_reports_send_now_keeps_orbits_own_reason_and_code()
    {
        var (api, h) = Api((_, _) => (HttpStatusCode.BadGateway, """{"error":{"code":"send_failed","message":"The email couldn't be sent."}}"""));
        var e = await Assert.ThrowsAsync<AppUnavailableException>(() => api.SendReportNowAsync("k", "rps_1", CancellationToken.None));
        Assert.Equal("/api/v1/reports/schedules/rps_1/send", h.Calls[0].Path);
        Assert.Equal("send_failed", e.Code);
        Assert.Equal("The email couldn't be sent.", e.AppMessage);
        Assert.Equal("Vantra Orbit is unavailable.", e.Message); // the generic text other callers show is unchanged
        Assert.False(e.TimedOut);
        Assert.True(OrbitApi.SendNowTimeout > AppApi.Timeout);
    }

    [Fact]
    public async Task Before_orbit_has_sequences_flow_says_so_in_orbits_words()
    {
        var (api, _) = Api((_, _) => (HttpStatusCode.ServiceUnavailable, Off));
        var e = await Assert.ThrowsAsync<AppUnavailableException>(() =>
            api.ActAsync("k", "sequence.add", new JsonObject { ["sequenceId"] = "seq_1", ["recordId"] = "rec_1" }, "i", CancellationToken.None));
        Assert.Equal("Sequences aren't switched on in this Orbit workspace yet.", e.Message);
        var sub = await Assert.ThrowsAsync<AppUnavailableException>(() => api.SubscribeAsync("k", "seq:*", "https://flow.example.com/api/events/orbit/fsb_1", CancellationToken.None));
        Assert.Equal("Sequences aren't switched on in this Orbit workspace yet.", sub.Message);
        // The pickers still get the tables; the sequence list is empty, with the reason.
        var (both, _) = Api((r, _) => r.RequestUri!.AbsolutePath == "/api/v1/tables" ? (HttpStatusCode.OK, "[]") : (HttpStatusCode.ServiceUnavailable, Off));
        var schema = await both.SchemaAsync("k", CancellationToken.None);
        Assert.Empty(schema["sequences"]!.AsArray());
        Assert.Equal("Sequences aren't switched on in this Orbit workspace yet.", schema["sequencesOff"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_sequence_scope_registers_a_sequence_webhook_and_removes_it_by_its_own_route()
    {
        var (api, h) = Api((r, _) => r.Method == HttpMethod.Delete ? (HttpStatusCode.NoContent, "") : (HttpStatusCode.Created, """{"id":"sqh_1","secret":"whsec_abc"}"""));
        var hook = await api.SubscribeAsync("k", "seq:seq_1", "https://flow.example.com/api/events/orbit/fsb_1", CancellationToken.None);
        Assert.Equal("sqh_1", hook.Id);
        Assert.Equal("/api/v1/sequences/webhooks", h.Calls[0].Path);
        Assert.Contains("\"sequenceId\":\"seq_1\"", h.Calls[0].Body);
        Assert.Contains("sequence.replied", h.Calls[0].Body);
        await api.SubscribeAsync("k", "seq:*", "https://flow.example.com/x", CancellationToken.None);
        Assert.Contains("\"sequenceId\":null", h.Calls[1].Body);
        await api.UnsubscribeAsync("k", "sqh_1", CancellationToken.None);
        Assert.Equal("/api/v1/sequences/webhooks/sqh_1", h.Calls[2].Path);
        await api.UnsubscribeAsync("k", "who_1", CancellationToken.None);
        Assert.Equal("/api/v1/webhooks/who_1", h.Calls[3].Path);
    }
}
