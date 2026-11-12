/* =========================================================
   VantraFlow — 007_steps_apps.sql
   New steps and apps: workspace secrets, approvals, webhook
   samples, and Stripe, HubSpot and Xero connections.

     flow.Secrets       a workspace's named secrets for the HTTP
                        request step (a bearer token, a password,
                        an API key): AES-256-GCM sealed by the
                        API, bound to the row, never shown again.
                        Hint keeps the last four characters.
     flow.Approvals     an Approval step's request: who may
                        decide, until when, and who decided what
                        and when. One per run and step.
     flow.HookSamples   the newest payload a webhook trigger
                        received (also while the flow is off), so
                        the builder can map its fields.
     flow.OAuthConnections  its Provider may now also be stripe,
                        hubspot or xero (CK_OAuthConnections_Provider
                        is replaced by …_Provider2).
     flow.Events        its Source may now also be stripe, hubspot
                        or xero (CK_Events_Source2 is replaced by
                        CK_Events_Source3).

   Order: deploy the API first, then run this. Until it has
   run, the new steps and apps hide themselves and their
   endpoints answer 503 not_ready. The probe
   (FlowSchema.StepsReadyAsync) looks for IX_Approvals_Inbox,
   made last, by name in sys.indexes.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001 to db/005. Tables, indexes and
   constraints go through EXEC() so the batch compiles before
   they exist. Idempotent: safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'flow.Secrets', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Secrets
    (
        Id            NVARCHAR(40)     NOT NULL,
        WorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        Name          NVARCHAR(80)     NOT NULL,
        Cipher        VARBINARY(MAX)   NOT NULL,
        Hint          NVARCHAR(8)      NULL,
        CreatedBy     NVARCHAR(64)     NOT NULL,
        CreatedByName NVARCHAR(160)    NULL,
        CreatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Secrets_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Secrets_UpdatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt    DATETIME2(3)     NULL,
        CONSTRAINT PK_Secrets PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.Approvals', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Approvals
    (
        Id            NVARCHAR(40)     NOT NULL,
        WorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        FlowId        NVARCHAR(40)     NOT NULL,
        FlowName      NVARCHAR(160)    NOT NULL,
        RunId         NVARCHAR(40)     NOT NULL,
        StepId        NVARCHAR(24)     NOT NULL,
        Title         NVARCHAR(200)    NOT NULL,
        Details       NVARCHAR(4000)   NULL,
        Approvers     NVARCHAR(MAX)    NOT NULL,
        Status        NVARCHAR(12)     NOT NULL CONSTRAINT DF_Approvals_Status DEFAULT N''pending'',
        DecidedBy     NVARCHAR(64)     NULL,
        DecidedByName NVARCHAR(160)    NULL,
        DecidedAt     DATETIME2(3)     NULL,
        DecidedVia    NVARCHAR(8)      NULL,
        Comment       NVARCHAR(1000)   NULL,
        RequestedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Approvals_RequestedAt DEFAULT SYSUTCDATETIME(),
        ExpiresAt     DATETIME2(3)     NOT NULL,
        CONSTRAINT PK_Approvals PRIMARY KEY (Id),
        CONSTRAINT CK_Approvals_Status CHECK (Status IN (N''pending'', N''approved'', N''rejected'', N''expired'', N''cancelled''))
    );');

IF OBJECT_ID(N'flow.HookSamples', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.HookSamples
    (
        FlowId      NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Payload     NVARCHAR(MAX)    NOT NULL,
        ReceivedAt  DATETIME2(3)     NOT NULL,
        CONSTRAINT PK_HookSamples PRIMARY KEY (FlowId)
    );');

-- Stripe, HubSpot and Xero connections: the provider check, replaced.
IF OBJECT_ID(N'flow.CK_OAuthConnections_Provider', N'C') IS NOT NULL
    EXEC(N'ALTER TABLE flow.OAuthConnections DROP CONSTRAINT CK_OAuthConnections_Provider;');
IF OBJECT_ID(N'flow.CK_OAuthConnections_Provider2', N'C') IS NULL
    EXEC(N'ALTER TABLE flow.OAuthConnections WITH CHECK ADD CONSTRAINT CK_OAuthConnections_Provider2
        CHECK (Provider IN (N''slack'', N''microsoft'', N''google'', N''stripe'', N''hubspot'', N''xero''));');

-- Their polling triggers' events: the source check, replaced.
IF OBJECT_ID(N'flow.CK_Events_Source2', N'C') IS NOT NULL
    EXEC(N'ALTER TABLE flow.Events DROP CONSTRAINT CK_Events_Source2;');
IF OBJECT_ID(N'flow.CK_Events_Source3', N'C') IS NULL
    EXEC(N'ALTER TABLE flow.Events WITH CHECK ADD CONSTRAINT CK_Events_Source3
        CHECK (Source IN (N''orbit'', N''projects'', N''schedule'', N''webhook'', N''manual'', N''slack'', N''microsoft'', N''google'', N''stripe'', N''hubspot'', N''xero''));');

-- A workspace's secrets by name (one each).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Secrets_Name' AND object_id = OBJECT_ID(N'flow.Secrets'))
    EXEC(N'CREATE UNIQUE INDEX UX_Secrets_Name ON flow.Secrets (WorkspaceId, Name);');

-- One request per run and step.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Approvals_RunStep' AND object_id = OBJECT_ID(N'flow.Approvals'))
    EXEC(N'CREATE UNIQUE INDEX UX_Approvals_RunStep ON flow.Approvals (RunId, StepId);');

-- The inbox: a workspace's requests, newest first. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Approvals_Inbox' AND object_id = OBJECT_ID(N'flow.Approvals'))
    EXEC(N'CREATE INDEX IX_Approvals_Inbox ON flow.Approvals (WorkspaceId, Status, RequestedAt DESC) INCLUDE (ExpiresAt);');
