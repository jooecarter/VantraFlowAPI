using VantraFlow.Api.Services;
using Xunit;

namespace VantraFlow.Api.Tests;

public class GraphMailErrorsTests
{
    private const string Sender = "flow@example.com";

    [Fact]
    public void A_wrong_or_expired_secret_says_so_and_is_not_retried()
    {
        var wrong = GraphMailErrors.Token(401, """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided. Trace ID: abc","error_codes":[7000215]}""");
        Assert.False(wrong.Retryable);
        Assert.Contains("client secret isn't right", wrong.Reason);
        Assert.DoesNotContain("Trace", wrong.Reason);

        var expired = GraphMailErrors.Token(401, """{"error":"invalid_client","error_description":"AADSTS7000222: The provided client secret keys are expired."}""");
        Assert.Contains("has expired", expired.Reason);
    }

    [Fact]
    public void An_unknown_app_or_tenant_and_an_unlisted_code()
    {
        Assert.Contains("app registration wasn't found", GraphMailErrors.Token(400, """{"error_codes":[700016]}""").Reason);
        Assert.Contains("tenant wasn't found", GraphMailErrors.Token(400, """{"error_codes":[90002]}""").Reason);
        Assert.Contains("(AADSTS50000)", GraphMailErrors.Token(400, """{"error_codes":[50000]}""").Reason);
        Assert.True(GraphMailErrors.Token(503, null).Retryable);
    }

    [Fact]
    public void A_missing_mailbox_or_a_refused_one_names_the_sender()
    {
        var missing = GraphMailErrors.Send(404, """{"error":{"code":"ErrorInvalidUser","message":"The requested user 'flow@example.com' is invalid."}}""", Sender);
        Assert.False(missing.Retryable);
        Assert.Contains("flow@example.com wasn't found", missing.Reason);

        var denied = GraphMailErrors.Send(403, """{"error":{"code":"ErrorAccessDenied","message":"Access is denied."}}""", Sender);
        Assert.Contains("isn't allowed to send from flow@example.com", denied.Reason);
        Assert.Contains("application access policy", denied.Reason);
    }

    [Fact]
    public void Throttling_and_outages_are_retried_and_odd_codes_are_not_echoed()
    {
        Assert.True(GraphMailErrors.Send(429, """{"error":{"code":"ApplicationThrottled"}}""", Sender).Retryable);
        Assert.True(GraphMailErrors.Send(503, "not json", Sender).Retryable);
        // A code that isn't shaped like one is left out, so nothing odd reaches a run.
        Assert.Equal("Microsoft 365 refused it (400).", GraphMailErrors.Send(400, """{"error":{"code":"<b>x</b>"}}""", Sender).Reason);
        Assert.Equal("Microsoft 365 refused it (ErrorInvalidRecipients).", GraphMailErrors.Send(400, """{"error":{"code":"ErrorInvalidRecipients"}}""", Sender).Reason);
    }
}
