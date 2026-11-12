using Dapper;

namespace VantraFlow.Api.Data;

/// <summary>
/// Has db/001 run (and db/007, <see cref="StepsReadyAsync"/>; db/005, <see cref="InvitesReadyAsync"/>; db/002, <see cref="AlertsReadyAsync"/>; db/003, <see cref="VersionsReadyAsync"/>)? Until it has, every endpoint but sign-in answers 503 <c>not_ready</c> and the worker waits —
/// so the API can be deployed before the migration. The probe looks for the index db/001 makes last
/// (<c>IX_Runs_Due</c>), by name in <c>sys.indexes</c> (never a constraint's definition). A positive answer is
/// remembered for the life of the process, a negative one for <see cref="NegativeTtl"/>.
/// </summary>
public sealed class FlowSchema(SqlConnectionFactory factory) : IWorkspaceFeatures
{
    public static readonly TimeSpan NegativeTtl = TimeSpan.FromSeconds(30);

    public const string ProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Due' AND object_id = OBJECT_ID(N'flow.Runs'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/002 (failure alerts): the column it adds last, by name in <c>sys.columns</c>.</summary>
    public const string AlertsProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID(N'flow.Flows') AND name = N'AlertedAt')
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/003 (versions): its table, by name.</summary>
    public const string VersionsProbeSql = @"
SELECT CASE WHEN OBJECT_ID(N'flow.FlowVersions', N'U') IS NOT NULL THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/004 (Slack, Microsoft 365, Google): the index it makes last, by name.</summary>
    public const string ConnectorsProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PollCursors_Due' AND object_id = OBJECT_ID(N'flow.PollCursors'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/005 (the workspace look and invitations): the index it makes last, by name.</summary>
    public const string InvitesProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Invites_Token' AND object_id = OBJECT_ID(N'flow.Invites'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    /// <summary>db/006 (the AI builder and Insights): the index it makes last, by name.</summary>
    public const string InsightsProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunSteps_Failed' AND object_id = OBJECT_ID(N'flow.RunSteps'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    private volatile bool insightsReady;
    private DateTime insightsCheckedAt = DateTime.MinValue;

    /// <summary>
    /// Has db/006 run? Until it has, the AI builder is "Not set up yet", Insights reads only the runs still kept and hides
    /// its time-saved settings, and pruning keeps no daily totals.
    /// </summary>
    public async Task<bool> InsightsReadyAsync()
    {
        if (insightsReady) return true;
        if (DateTime.UtcNow - insightsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        insightsReady = await conn.ExecuteScalarAsync<bool>(InsightsProbeSql);
        insightsCheckedAt = DateTime.UtcNow;
        return insightsReady;
    }

    /// <summary>db/007 (secrets, approvals, webhook samples, Stripe, HubSpot and Xero): the index it makes last, by name.</summary>
    public const string StepsProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Approvals_Inbox' AND object_id = OBJECT_ID(N'flow.Approvals'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    private volatile bool stepsReady;
    private DateTime stepsCheckedAt = DateTime.MinValue;

    /// <summary>
    /// Has db/007 run? Until it has, secrets, approvals, webhook samples and the Stripe, HubSpot and Xero connections
    /// hide themselves (503 <c>not_ready</c>), and an Approval step fails with why.
    /// </summary>
    public async Task<bool> StepsReadyAsync()
    {
        if (stepsReady) return true;
        if (DateTime.UtcNow - stepsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        stepsReady = await conn.ExecuteScalarAsync<bool>(StepsProbeSql);
        stepsCheckedAt = DateTime.UtcNow;
        return stepsReady;
    }

    /// <summary>db/008 (plans and billing): the index it makes last, by name.</summary>
    public const string PlansProbeSql = @"
SELECT CASE WHEN EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WorkspacePlans_Customer' AND object_id = OBJECT_ID(N'flow.WorkspacePlans'))
            THEN CAST(1 AS BIT) ELSE CAST(0 AS BIT) END;";

    private volatile bool plansReady;
    private DateTime plansCheckedAt = DateTime.MinValue;

    /// <summary>Has db/008 run? Until it has, plans aren't enforced and Plan &amp; billing says it isn't set up yet.</summary>
    public async Task<bool> PlansReadyAsync()
    {
        if (plansReady) return true;
        if (DateTime.UtcNow - plansCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        plansReady = await conn.ExecuteScalarAsync<bool>(PlansProbeSql);
        plansCheckedAt = DateTime.UtcNow;
        return plansReady;
    }

    private volatile bool invitesReady;
    private DateTime invitesCheckedAt = DateTime.MinValue;

    /// <summary>Has db/005 run? Until it has, the workspace look and invitations hide themselves (503 <c>not_ready</c>).</summary>
    public async Task<bool> InvitesReadyAsync()
    {
        if (invitesReady) return true;
        if (DateTime.UtcNow - invitesCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        invitesReady = await conn.ExecuteScalarAsync<bool>(InvitesProbeSql);
        invitesCheckedAt = DateTime.UtcNow;
        return invitesReady;
    }

    private volatile bool connectorsReady;
    private DateTime connectorsCheckedAt = DateTime.MinValue;

    /// <summary>Has db/004 run? Until it has, the outside connectors hide themselves and nothing polls.</summary>
    public async Task<bool> ConnectorsReadyAsync()
    {
        if (connectorsReady) return true;
        if (DateTime.UtcNow - connectorsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        connectorsReady = await conn.ExecuteScalarAsync<bool>(ConnectorsProbeSql);
        connectorsCheckedAt = DateTime.UtcNow;
        return connectorsReady;
    }

    private volatile bool versionsReady;
    private DateTime versionsCheckedAt = DateTime.MinValue;

    /// <summary>Has db/003 run? Until it has, saves keep no versions and the version list hides itself.</summary>
    public async Task<bool> VersionsReadyAsync()
    {
        if (versionsReady) return true;
        if (DateTime.UtcNow - versionsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        versionsReady = await conn.ExecuteScalarAsync<bool>(VersionsProbeSql);
        versionsCheckedAt = DateTime.UtcNow;
        return versionsReady;
    }

    private volatile bool ready;
    private DateTime checkedAt = DateTime.MinValue;
    private volatile bool alertsReady;
    private DateTime alertsCheckedAt = DateTime.MinValue;

    /// <summary>Has db/002 run? Until it has, failure alerts hide themselves (remembered as <see cref="ReadyAsync"/> is).</summary>
    public async Task<bool> AlertsReadyAsync()
    {
        if (alertsReady) return true;
        if (DateTime.UtcNow - alertsCheckedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        alertsReady = await conn.ExecuteScalarAsync<bool>(AlertsProbeSql);
        alertsCheckedAt = DateTime.UtcNow;
        return alertsReady;
    }

    public async Task<bool> ReadyAsync()
    {
        if (ready) return true;
        if (DateTime.UtcNow - checkedAt < NegativeTtl) return false;
        using var conn = factory.Flow();
        ready = await conn.ExecuteScalarAsync<bool>(ProbeSql);
        checkedAt = DateTime.UtcNow;
        return ready;
    }
}

/// <summary>Which of the later migrations the workspace endpoints can count on (<see cref="FlowSchema"/>; a fake in tests).</summary>
public interface IWorkspaceFeatures
{
    /// <summary>db/002: failure alerts.</summary>
    Task<bool> AlertsReadyAsync();
    /// <summary>db/005: the workspace look and invitations.</summary>
    Task<bool> InvitesReadyAsync();
}
