using VantraFlow.Api.Data;
using VantraFlow.Api.Services.Engine;

namespace VantraFlow.Api.Services.Ai;

/// <summary>A model call's answer: its JSON, or why there's none and whether it's worth trying again later.</summary>
public sealed record AiReply(string? Content, string? Error = null, bool Retryable = false)
{
    public static AiReply Fail(string error, bool retryable = false) => new(null, error, retryable);
}

/// <summary>The model AI steps call (<see cref="AiClient"/>; tests use a fake).</summary>
public interface IAiModel
{
    bool Configured { get; }
    Task<AiReply> CompleteAsync(AiAsk ask, CancellationToken ct);
}

/// <summary>A credit taken for one AI step, or why not (<see cref="AiStepRules.CreditsUsedUp"/>).</summary>
public sealed record AiCreditTake(bool Ok, int Used, int Cap, string? Error = null);

/// <summary>The plan's AI credits, one per AI step that calls the model (<see cref="AiCredits"/>; tests use a fake).</summary>
public interface IAiCredits
{
    Task<AiCreditTake> TakeAsync(Guid workspaceId, DateTime now);
    Task RefundAsync(Guid workspaceId, DateTime now);
}

/// <summary>
/// Runs an AI step: checks the server is set up, prepares the ask from the step's own inputs, takes one of the plan's
/// AI credits, calls the model and reads its strict JSON answer into the step's output. A credit is given back when the
/// call doesn't produce an answer (it failed, timed out, or the answer couldn't be read). Out of credits, the step fails
/// with "AI credits used up" (not retried); a 5xx, a 429 or a timeout is retried, by the client at once and then by the
/// engine with its backoff.
/// </summary>
public sealed class AiSteps(IAiModel model, IAiCredits credits, Func<DateTime>? clock = null)
{
    public async Task<StepOutcome> RunAsync(StepCall call, CancellationToken ct)
    {
        if (!model.Configured) return StepOutcome.Fail(AiStepRules.NotSetUp);
        var (prepared, why) = AiStepRules.Prepare(call.Step.Action, call.Input);
        if (prepared is null) return StepOutcome.Fail(why!);

        var now = clock?.Invoke() ?? DateTime.UtcNow;
        var take = await credits.TakeAsync(call.WorkspaceId, now);
        if (!take.Ok) return StepOutcome.Fail(take.Error ?? AiStepRules.CreditsUsedUp(take.Cap, null));

        AiReply reply;
        try { reply = await model.CompleteAsync(prepared.Ask, ct); }
        catch (OperationCanceledException)
        {
            await credits.RefundAsync(call.WorkspaceId, now);
            throw;
        }
        if (reply.Content is null)
        {
            await credits.RefundAsync(call.WorkspaceId, now);
            return StepOutcome.Fail(reply.Error ?? "The AI didn't answer.", reply.Retryable);
        }
        var (output, error) = AiStepRules.Read(call.Step.Action, reply.Content, prepared);
        if (output is null)
        {
            await credits.RefundAsync(call.WorkspaceId, now);
            return StepOutcome.Fail(error ?? "The AI's answer couldn't be read.", retryable: true);
        }
        return StepOutcome.Done(output);
    }
}

/// <summary>
/// The plan's AI credits for AI steps: the same monthly count as the AI flow builder (<c>flow.Usage.AiDrafts</c>, db/006),
/// so a workspace has one pool of AI credits, as its plan says. Until db/006 has run, AI steps can't count, so they don't run.
/// </summary>
public sealed class AiCredits(AiRepository repo, FlowSchema schema, Billing.Plans plans, WorkspacesRepository workspaces, AiClient ai) : IAiCredits
{
    public async Task<AiCreditTake> TakeAsync(Guid workspaceId, DateTime now)
    {
        if (!await schema.InsightsReadyAsync()) return new AiCreditTake(false, 0, 0, AiStepRules.NotReady);
        if (await workspaces.GetAsync(workspaceId) is not { } ws) return new AiCreditTake(false, 0, 0, "This workspace is gone.");
        var (plan, _) = await plans.ForAsync(ws, now);
        var cap = AiClient.CapFor(plan.Enforced, plan.Limits.AiCredits, ai.ConfiguredCap);
        var (ok, used) = await repo.TryCountAsync(workspaceId, now, cap);
        if (ok) return new AiCreditTake(true, used, cap);
        var more = plan.Enforced ? plans.Catalog.Cheapest(l => AiClient.CapFor(true, l.AiCredits, ai.ConfiguredCap) > cap) : null;
        return new AiCreditTake(false, used, cap, AiStepRules.CreditsUsedUp(cap, more));
    }

    public Task RefundAsync(Guid workspaceId, DateTime now) => repo.RefundAsync(workspaceId, now);
}
