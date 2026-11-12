/* =========================================================
   VantraFlow — 008_plans.sql
   Plans and billing: each workspace's plan, the notices it
   has had, and the Stripe events already handled.

     flow.WorkspacePlans    one row per workspace. Tier is the
                            paid tier (starter, pro, business)
                            while a Stripe subscription is
                            live, else free. TrialEndsAt is
                            Flow's own 14-day Pro trial; null
                            means not started: it starts when
                            billing goes live (Stripe set up),
                            so nobody's trial runs out before
                            they can pay. Status, Interval,
                            CurrentPeriodEnd and
                            CancelAtPeriodEnd copy the Stripe
                            subscription. Nothing about a card
                            is stored.
     flow.PlanNotices       the emails sent once a period: 80%
                            and 100% of the month's runs, a
                            failed payment, flows switched off.
     flow.BillingEvents     the Stripe webhook events handled,
                            by id, so a retried delivery is
                            handled once.

   Every workspace that exists when this runs gets a plan row
   with its trial not started (Pro until billing goes live,
   then 14 days of Pro). Nothing is switched off by running
   this.

   Order: deploy the API first, then run this. Until it has
   run, plans aren't enforced (the server's run quota applies
   as before) and Plan & billing shows "Not set up yet". The
   probe (FlowSchema.PlansReadyAsync) looks for
   IX_WorkspacePlans_Customer, made last, by name in
   sys.indexes.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001 to db/005. Tables, the seed
   and indexes go through EXEC() so the batch compiles before
   they exist. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- Each workspace's plan.
IF OBJECT_ID(N'flow.WorkspacePlans', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.WorkspacePlans
    (
        WorkspaceId          UNIQUEIDENTIFIER NOT NULL,
        Tier                 NVARCHAR(12)     NOT NULL CONSTRAINT DF_WorkspacePlans_Tier DEFAULT N''free'',
        Status               NVARCHAR(24)     NULL,
        Interval             NVARCHAR(8)      NULL,
        TrialEndsAt          DATETIME2(3)     NULL,
        CurrentPeriodEnd     DATETIME2(3)     NULL,
        CancelAtPeriodEnd    BIT              NOT NULL CONSTRAINT DF_WorkspacePlans_Cancel DEFAULT 0,
        StripeCustomerId     NVARCHAR(80)     NULL,
        StripeSubscriptionId NVARCHAR(80)     NULL,
        Currency             NVARCHAR(3)      NULL,
        TrialNoticeAt        DATETIME2(3)     NULL,
        CreatedAt            DATETIME2(3)     NOT NULL CONSTRAINT DF_WorkspacePlans_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt            DATETIME2(3)     NOT NULL CONSTRAINT DF_WorkspacePlans_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WorkspacePlans PRIMARY KEY (WorkspaceId),
        CONSTRAINT CK_WorkspacePlans_Tier CHECK (Tier IN (N''free'', N''starter'', N''pro'', N''business'')),
        CONSTRAINT CK_WorkspacePlans_Interval CHECK (Interval IS NULL OR Interval IN (N''month'', N''year''))
    );');

-- The once-a-period emails.
IF OBJECT_ID(N'flow.PlanNotices', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.PlanNotices
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Kind        NVARCHAR(24)     NOT NULL,
        Period      NVARCHAR(80)     NOT NULL,
        SentAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_PlanNotices_SentAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PlanNotices PRIMARY KEY (WorkspaceId, Kind, Period)
    );');

-- Stripe events handled.
IF OBJECT_ID(N'flow.BillingEvents', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.BillingEvents
    (
        Id         NVARCHAR(80)  NOT NULL,
        Type       NVARCHAR(80)  NOT NULL,
        ReceivedAt DATETIME2(3)  NOT NULL CONSTRAINT DF_BillingEvents_ReceivedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_BillingEvents PRIMARY KEY (Id)
    );');

-- Every workspace here now: a plan with its trial not started.
EXEC(N'INSERT INTO flow.WorkspacePlans (WorkspaceId)
    SELECT w.Id FROM flow.Workspaces w
    WHERE NOT EXISTS (SELECT 1 FROM flow.WorkspacePlans p WHERE p.WorkspaceId = w.Id);');

-- A subscription's workspace, from the webhook.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WorkspacePlans_Subscription' AND object_id = OBJECT_ID(N'flow.WorkspacePlans'))
    EXEC(N'CREATE UNIQUE INDEX UX_WorkspacePlans_Subscription ON flow.WorkspacePlans (StripeSubscriptionId) WHERE StripeSubscriptionId IS NOT NULL;');

-- A customer's workspace. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WorkspacePlans_Customer' AND object_id = OBJECT_ID(N'flow.WorkspacePlans'))
    EXEC(N'CREATE INDEX IX_WorkspacePlans_Customer ON flow.WorkspacePlans (StripeCustomerId) WHERE StripeCustomerId IS NOT NULL;');
