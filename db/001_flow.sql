/* =========================================================
   VantraFlow — 001_flow.sql
   Vantra Flow's own database: the automation studio that
   links the Vantra apps (Orbit and Projects first).

     flow.Workspaces        a Flow workspace: its name, the
                            time zone schedules run in, and an
                            optional monthly run quota
     flow.WorkspaceMembers  who is in it: owner | admin | member
                            (only owners and admins connect apps)
     flow.Connections       a workspace's link to one Orbit or
                            Projects workspace: the remote key's
                            id (the origin marker on Flow's
                            writes) and its token, AES-256-GCM
                            sealed by the API (never plain)
     flow.Subscriptions     the webhook Flow registered in an app
                            (one per Orbit table, one per Projects
                            workspace): the remote id and its
                            signing secret, sealed
     flow.Flows             a flow: its definition (JSON), on or
                            off, the trigger it listens to, and a
                            webhook trigger's unguessable id and
                            optional sealed HMAC secret
     flow.Events            the queue: app events, schedule slots,
                            webhook calls, tests and replays.
                            Claimed under a lease with UPDLOCK +
                            READPAST, retried with backoff, left
                            'dead' with its error after the last
                            try. Done rows are deleted.
     flow.Runs              one run of a flow: a snapshot of the
                            definition it runs, the trigger data,
                            each step's output so far, where it
                            is (Position), and when it may go on
                            (NotBefore: a delay or a retry)
     flow.RunSteps          the run log: each step's input,
                            output, error, attempt and duration
     flow.FiredKeys         what has fired, once: each schedule
                            slot and each app event (the insert
                            is the claim, so two instances never
                            run one twice)
     flow.Usage             runs and emails per workspace per
                            month and per day (quotas, rate limit)

   No FKs: rows are deleted by the API in dependency order.

   Order: deploy the API first, then run this. Until it has
   run every endpoint but sign-in answers 503 not_ready and the
   worker waits, checking once a minute.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`. What depends on an earlier step (the schema,
   the constraints, the indexes) goes through EXEC() so the
   batch compiles before the tables exist; IX_Runs_Due is made
   last: the probe (FlowSchema.ReadyAsync) only says ready once
   it is there. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF SCHEMA_ID(N'flow') IS NULL
    EXEC(N'CREATE SCHEMA flow AUTHORIZATION dbo;');

IF OBJECT_ID(N'flow.Workspaces', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Workspaces
    (
        Id              UNIQUEIDENTIFIER NOT NULL,
        Name            NVARCHAR(120)    NOT NULL,
        TimeZone        NVARCHAR(64)     NOT NULL CONSTRAINT DF_Workspaces_TimeZone DEFAULT N''UTC'',
        OwnerVantraId   NVARCHAR(64)     NOT NULL,
        MonthlyRunQuota INT              NULL,
        CreatedAt       DATETIME2(3)     NOT NULL CONSTRAINT DF_Workspaces_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt       DATETIME2(3)     NOT NULL CONSTRAINT DF_Workspaces_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Workspaces PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.WorkspaceMembers', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.WorkspaceMembers
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        VantraId    NVARCHAR(64)     NOT NULL,
        Role        NVARCHAR(8)      NOT NULL,
        Email       NVARCHAR(256)    NULL,
        Name        NVARCHAR(160)    NULL,
        AddedBy     NVARCHAR(64)     NULL,
        AddedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_WorkspaceMembers_AddedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WorkspaceMembers PRIMARY KEY (WorkspaceId, VantraId),
        CONSTRAINT CK_WorkspaceMembers_Role CHECK (Role IN (N''owner'', N''admin'', N''member''))
    );');

IF OBJECT_ID(N'flow.Connections', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Connections
    (
        Id                  UNIQUEIDENTIFIER NOT NULL,
        WorkspaceId         UNIQUEIDENTIFIER NOT NULL,
        App                 NVARCHAR(16)     NOT NULL,
        RemoteWorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        RemoteWorkspaceName NVARCHAR(200)    NOT NULL,
        KeyId               NVARCHAR(64)     NOT NULL,
        TokenCipher         VARBINARY(400)   NOT NULL,
        Status              NVARCHAR(12)     NOT NULL CONSTRAINT DF_Connections_Status DEFAULT N''active'',
        LastError           NVARCHAR(400)    NULL,
        ConnectedBy         NVARCHAR(64)     NOT NULL,
        ConnectedByName     NVARCHAR(160)    NULL,
        ConnectedAt         DATETIME2(3)     NOT NULL CONSTRAINT DF_Connections_ConnectedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt          DATETIME2(3)     NULL,
        RevokedAt           DATETIME2(3)     NULL,
        CONSTRAINT PK_Connections PRIMARY KEY (Id),
        CONSTRAINT CK_Connections_App CHECK (App IN (N''orbit'', N''projects'')),
        CONSTRAINT CK_Connections_Status CHECK (Status IN (N''active'', N''error'', N''revoked''))
    );');

IF OBJECT_ID(N'flow.Subscriptions', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Subscriptions
    (
        Id           NVARCHAR(40)     NOT NULL,
        WorkspaceId  UNIQUEIDENTIFIER NOT NULL,
        ConnectionId UNIQUEIDENTIFIER NOT NULL,
        App          NVARCHAR(16)     NOT NULL,
        Scope        NVARCHAR(64)     NOT NULL,
        RemoteId     NVARCHAR(64)     NOT NULL,
        SecretCipher VARBINARY(400)   NOT NULL,
        Events       NVARCHAR(200)    NOT NULL,
        CreatedAt    DATETIME2(3)     NOT NULL CONSTRAINT DF_Subscriptions_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastEventAt  DATETIME2(3)     NULL,
        CONSTRAINT PK_Subscriptions PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.Flows', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Flows
    (
        Id               NVARCHAR(40)     NOT NULL,
        WorkspaceId      UNIQUEIDENTIFIER NOT NULL,
        Name             NVARCHAR(160)    NOT NULL,
        Description      NVARCHAR(400)    NULL,
        Enabled          BIT              NOT NULL CONSTRAINT DF_Flows_Enabled DEFAULT 0,
        Definition       NVARCHAR(MAX)    NOT NULL,
        Version          INT              NOT NULL CONSTRAINT DF_Flows_Version DEFAULT 1,
        TriggerApp       NVARCHAR(16)     NOT NULL,
        TriggerEvent     NVARCHAR(40)     NOT NULL,
        TriggerScope     NVARCHAR(64)     NULL,
        HookId           NVARCHAR(40)     NULL,
        HookSecretCipher VARBINARY(400)   NULL,
        TemplateId       NVARCHAR(40)     NULL,
        PausedReason     NVARCHAR(200)    NULL,
        CreatedBy        NVARCHAR(64)     NOT NULL,
        CreatedAt        DATETIME2(3)     NOT NULL CONSTRAINT DF_Flows_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedBy        NVARCHAR(64)     NOT NULL,
        UpdatedAt        DATETIME2(3)     NOT NULL CONSTRAINT DF_Flows_UpdatedAt DEFAULT SYSUTCDATETIME(),
        LastRunAt        DATETIME2(3)     NULL,
        LastRunStatus    NVARCHAR(12)     NULL,
        CONSTRAINT PK_Flows PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.Events', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Events
    (
        Seq         BIGINT IDENTITY(1, 1) NOT NULL,
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Source      NVARCHAR(16)     NOT NULL,
        Kind        NVARCHAR(40)     NOT NULL,
        Scope       NVARCHAR(64)     NULL,
        FlowId      NVARCHAR(40)     NULL,
        Payload     NVARCHAR(MAX)    NOT NULL,
        FlowCaused  BIT              NOT NULL CONSTRAINT DF_Events_FlowCaused DEFAULT 0,
        Test        BIT              NOT NULL CONSTRAINT DF_Events_Test DEFAULT 0,
        ReplayOf    NVARCHAR(40)     NULL,
        At          DATETIME2(3)     NOT NULL,
        NotBefore   DATETIME2(3)     NOT NULL,
        Attempts    INT              NOT NULL CONSTRAINT DF_Events_Attempts DEFAULT 0,
        LeaseUntil  DATETIME2(3)     NULL,
        LeaseBy     NVARCHAR(64)     NULL,
        Status      NVARCHAR(8)      NOT NULL CONSTRAINT DF_Events_Status DEFAULT N''queued'',
        LastError   NVARCHAR(1000)   NULL,
        CONSTRAINT PK_Events PRIMARY KEY (Seq),
        CONSTRAINT CK_Events_Source CHECK (Source IN (N''orbit'', N''projects'', N''schedule'', N''webhook'', N''manual'')),
        CONSTRAINT CK_Events_Status CHECK (Status IN (N''queued'', N''dead''))
    );');

IF OBJECT_ID(N'flow.Runs', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Runs
    (
        Seq         BIGINT IDENTITY(1, 1) NOT NULL,
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        FlowName    NVARCHAR(160)    NOT NULL,
        FlowVersion INT              NOT NULL,
        Definition  NVARCHAR(MAX)    NOT NULL,
        TriggerData NVARCHAR(MAX)    NOT NULL,
        Context     NVARCHAR(MAX)    NULL,
        Position    NVARCHAR(200)    NULL,
        Attempt     INT              NOT NULL CONSTRAINT DF_Runs_Attempt DEFAULT 0,
        Status      NVARCHAR(12)     NOT NULL,
        Test        BIT              NOT NULL CONSTRAINT DF_Runs_Test DEFAULT 0,
        ReplayOf    NVARCHAR(40)     NULL,
        EventId     NVARCHAR(40)     NULL,
        StepCount   INT              NOT NULL CONSTRAINT DF_Runs_StepCount DEFAULT 0,
        Error       NVARCHAR(1000)   NULL,
        StartedAt   DATETIME2(3)     NOT NULL,
        FinishedAt  DATETIME2(3)     NULL,
        DurationMs  INT              NULL,
        NotBefore   DATETIME2(3)     NOT NULL,
        LeaseUntil  DATETIME2(3)     NULL,
        LeaseBy     NVARCHAR(64)     NULL,
        CONSTRAINT PK_Runs PRIMARY KEY (Seq),
        CONSTRAINT CK_Runs_Status CHECK (Status IN (N''queued'', N''running'', N''waiting'', N''succeeded'', N''failed'', N''skipped'', N''cancelled''))
    );');

IF OBJECT_ID(N'flow.RunSteps', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.RunSteps
    (
        RunId      NVARCHAR(40)     NOT NULL,
        Seq        INT              NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        StepId     NVARCHAR(40)     NOT NULL,
        Type       NVARCHAR(16)     NOT NULL,
        App        NVARCHAR(16)     NULL,
        StepAction NVARCHAR(40)     NULL,
        Name       NVARCHAR(160)    NOT NULL,
        Status     NVARCHAR(12)     NOT NULL,
        Attempt    INT              NOT NULL,
        Input      NVARCHAR(MAX)    NULL,
        Output     NVARCHAR(MAX)    NULL,
        Error      NVARCHAR(1000)   NULL,
        StartedAt  DATETIME2(3)     NOT NULL,
        DurationMs INT              NOT NULL,
        CONSTRAINT PK_RunSteps PRIMARY KEY (RunId, Seq),
        CONSTRAINT CK_RunSteps_Status CHECK (Status IN (N''succeeded'', N''failed'', N''retrying'', N''waiting'', N''skipped''))
    );');

IF OBJECT_ID(N'flow.FiredKeys', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FiredKeys
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        FiredKey    NVARCHAR(200)    NOT NULL,
        At          DATETIME2(3)     NOT NULL CONSTRAINT DF_FiredKeys_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FiredKeys PRIMARY KEY (WorkspaceId, FlowId, FiredKey)
    );');

IF OBJECT_ID(N'flow.Usage', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Usage
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Period      NVARCHAR(10)     NOT NULL,
        Runs        INT              NOT NULL CONSTRAINT DF_Usage_Runs DEFAULT 0,
        Emails      INT              NOT NULL CONSTRAINT DF_Usage_Emails DEFAULT 0,
        CONSTRAINT PK_Usage PRIMARY KEY (WorkspaceId, Period)
    );');

-- A person's workspaces.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WorkspaceMembers_VantraId' AND object_id = OBJECT_ID(N'flow.WorkspaceMembers'))
    EXEC(N'CREATE INDEX IX_WorkspaceMembers_VantraId ON flow.WorkspaceMembers (VantraId) INCLUDE (Role);');

-- One live connection per app per workspace.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Connections_Live' AND object_id = OBJECT_ID(N'flow.Connections'))
    EXEC(N'CREATE UNIQUE INDEX UX_Connections_Live ON flow.Connections (WorkspaceId, App) WHERE RevokedAt IS NULL;');

-- A connection's subscriptions, one per scope.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Subscriptions_Scope' AND object_id = OBJECT_ID(N'flow.Subscriptions'))
    EXEC(N'CREATE UNIQUE INDEX UX_Subscriptions_Scope ON flow.Subscriptions (ConnectionId, Scope);');

-- A workspace's flows, and the flows an event can start.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Flows_Workspace' AND object_id = OBJECT_ID(N'flow.Flows'))
    EXEC(N'CREATE INDEX IX_Flows_Workspace ON flow.Flows (WorkspaceId, TriggerApp, TriggerEvent) INCLUDE (Enabled, TriggerScope);');

-- A webhook trigger's address.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Flows_HookId' AND object_id = OBJECT_ID(N'flow.Flows'))
    EXEC(N'CREATE UNIQUE INDEX UX_Flows_HookId ON flow.Flows (HookId) WHERE HookId IS NOT NULL;');

-- The schedule tick: every enabled schedule flow.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Flows_Schedule' AND object_id = OBJECT_ID(N'flow.Flows'))
    EXEC(N'CREATE INDEX IX_Flows_Schedule ON flow.Flows (TriggerApp) INCLUDE (WorkspaceId, Definition) WHERE Enabled = 1 AND TriggerApp = N''schedule'';');

-- The event claim: due events, oldest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Events_Due' AND object_id = OBJECT_ID(N'flow.Events'))
    EXEC(N'CREATE INDEX IX_Events_Due ON flow.Events (Status, NotBefore, Seq) INCLUDE (WorkspaceId, LeaseUntil) WHERE Status = N''queued'';');

-- One event per id in a workspace.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Events_Id' AND object_id = OBJECT_ID(N'flow.Events'))
    EXEC(N'CREATE UNIQUE INDEX UX_Events_Id ON flow.Events (WorkspaceId, Id);');

-- A run by its id; a workspace's and a flow's runs, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Runs_Id' AND object_id = OBJECT_ID(N'flow.Runs'))
    EXEC(N'CREATE UNIQUE INDEX UX_Runs_Id ON flow.Runs (Id);');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Workspace' AND object_id = OBJECT_ID(N'flow.Runs'))
    EXEC(N'CREATE INDEX IX_Runs_Workspace ON flow.Runs (WorkspaceId, StartedAt DESC) INCLUDE (FlowId, Status, Test);');
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Flow' AND object_id = OBJECT_ID(N'flow.Runs'))
    EXEC(N'CREATE INDEX IX_Runs_Flow ON flow.Runs (FlowId, StartedAt DESC) INCLUDE (Status);');

-- The prune of fired keys.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FiredKeys_At' AND object_id = OBJECT_ID(N'flow.FiredKeys'))
    EXEC(N'CREATE INDEX IX_FiredKeys_At ON flow.FiredKeys (At);');

-- The run claim: queued and waiting runs that are due. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Due' AND object_id = OBJECT_ID(N'flow.Runs'))
    EXEC(N'CREATE INDEX IX_Runs_Due ON flow.Runs (NotBefore, Seq) INCLUDE (WorkspaceId, Status, LeaseUntil) WHERE Status IN (N''queued'', N''running'', N''waiting'');');
