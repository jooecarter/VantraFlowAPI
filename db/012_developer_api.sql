/* =========================================================
   VantraFlow — 012_developer_api.sql
   The public developer API (/api/v1) and outgoing webhooks.

     flow.ApiKeys            a workspace's API keys. Only a
                             SHA-256 hash of each key is kept,
                             with its first characters in the
                             clear (Prefix) to find it by; the
                             key itself is shown once. Scopes:
                             space-separated (flows:read,
                             flows:write, runs:read, runs:write,
                             webhooks:manage).
     flow.ApiIdempotency     the answer to a run-now request sent
                             with an Idempotency-Key, kept for a
                             day, so a retry starts nothing new.
     flow.WebhookEndpoints   a workspace's outgoing webhooks: the
                             URL, the events it gets, its signing
                             secret (AES-256-GCM sealed by the API,
                             bound to the row) and, after repeated
                             failures, why it was switched off.
     flow.WebhookDeliveries  each event sent to an endpoint: its
                             payload, the attempts so far, the
                             next one, and the last answer (status
                             code and a short snippet). Kept for 30
                             days.

   Order: deploy the API first, then run this. Until it has
   run, Settings → Developers says it arrives with the next
   server update, /api/v1 answers 503 not_ready and nothing
   is sent. The probe (FlowSchema.DeveloperReadyAsync) looks
   for IX_WebhookDeliveries_Endpoint, made last, by name in
   sys.indexes.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001. Tables and indexes go through
   EXEC() so the batch compiles before they exist. Idempotent:
   safe to re-run. It alters no earlier table.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- Needs db/001 (workspaces, flows and runs): stop before changing anything if it hasn't run.
IF OBJECT_ID(N'flow.Workspaces', N'U') IS NULL OR OBJECT_ID(N'flow.Flows', N'U') IS NULL OR OBJECT_ID(N'flow.Runs', N'U') IS NULL
    THROW 51012, N'Run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF OBJECT_ID(N'flow.ApiKeys', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.ApiKeys
    (
        Id            NVARCHAR(40)     NOT NULL,
        WorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        Name          NVARCHAR(80)     NOT NULL,
        Prefix        NVARCHAR(16)     NOT NULL,
        KeyHash       BINARY(32)       NOT NULL,
        Scopes        NVARCHAR(200)    NOT NULL,
        ExpiresAt     DATETIME2(3)     NULL,
        CreatedBy     NVARCHAR(64)     NOT NULL,
        CreatedByName NVARCHAR(160)    NULL,
        CreatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_ApiKeys_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt    DATETIME2(3)     NULL,
        RevokedAt     DATETIME2(3)     NULL,
        RevokedBy     NVARCHAR(64)     NULL,
        CONSTRAINT PK_ApiKeys PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.ApiIdempotency', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.ApiIdempotency
    (
        KeyId       NVARCHAR(40)     NOT NULL,
        IdemKey     NVARCHAR(255)    NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        RequestHash BINARY(32)       NOT NULL,
        Status      INT              NOT NULL CONSTRAINT DF_ApiIdempotency_Status DEFAULT 0,
        Response    NVARCHAR(MAX)    NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_ApiIdempotency_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ApiIdempotency PRIMARY KEY (KeyId, IdemKey)
    );');

IF OBJECT_ID(N'flow.WebhookEndpoints', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.WebhookEndpoints
    (
        Id               NVARCHAR(40)     NOT NULL,
        WorkspaceId      UNIQUEIDENTIFIER NOT NULL,
        Url              NVARCHAR(2000)   NOT NULL,
        Description      NVARCHAR(200)    NULL,
        Events           NVARCHAR(400)    NOT NULL,
        SecretCipher     VARBINARY(400)   NOT NULL,
        SecretHint       NVARCHAR(8)      NULL,
        Enabled          BIT              NOT NULL CONSTRAINT DF_WebhookEndpoints_Enabled DEFAULT 1,
        DisabledReason   NVARCHAR(400)    NULL,
        DisabledAt       DATETIME2(3)     NULL,
        FailuresInARow   INT              NOT NULL CONSTRAINT DF_WebhookEndpoints_Failures DEFAULT 0,
        CreatedBy        NVARCHAR(64)     NOT NULL,
        CreatedByName    NVARCHAR(160)    NULL,
        CreatedAt        DATETIME2(3)     NOT NULL CONSTRAINT DF_WebhookEndpoints_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt        DATETIME2(3)     NOT NULL CONSTRAINT DF_WebhookEndpoints_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_WebhookEndpoints PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.WebhookDeliveries', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.WebhookDeliveries
    (
        Seq            BIGINT IDENTITY(1, 1) NOT NULL,
        Id             NVARCHAR(40)     NOT NULL,
        WorkspaceId    UNIQUEIDENTIFIER NOT NULL,
        EndpointId     NVARCHAR(40)     NOT NULL,
        EventId        NVARCHAR(40)     NOT NULL,
        EventType      NVARCHAR(40)     NOT NULL,
        Payload        NVARCHAR(MAX)    NOT NULL,
        Status         NVARCHAR(12)     NOT NULL CONSTRAINT DF_WebhookDeliveries_Status DEFAULT N''pending'',
        Test           BIT              NOT NULL CONSTRAINT DF_WebhookDeliveries_Test DEFAULT 0,
        Attempts       INT              NOT NULL CONSTRAINT DF_WebhookDeliveries_Attempts DEFAULT 0,
        NextAttemptAt  DATETIME2(3)     NULL,
        LeaseUntil     DATETIME2(3)     NULL,
        LeaseBy        NVARCHAR(64)     NULL,
        ResponseStatus INT              NULL,
        ResponseBody   NVARCHAR(1000)   NULL,
        Error          NVARCHAR(400)    NULL,
        DurationMs     INT              NULL,
        CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_WebhookDeliveries_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastAttemptAt  DATETIME2(3)     NULL,
        DeliveredAt    DATETIME2(3)     NULL,
        CONSTRAINT PK_WebhookDeliveries PRIMARY KEY (Seq),
        CONSTRAINT CK_WebhookDeliveries_Status CHECK (Status IN (N''pending'', N''succeeded'', N''failed''))
    );');

-- A presented key is found by its prefix (then its hash is compared in constant time).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_ApiKeys_Prefix' AND object_id = OBJECT_ID(N'flow.ApiKeys'))
    EXEC(N'CREATE UNIQUE INDEX UX_ApiKeys_Prefix ON flow.ApiKeys (Prefix);');

-- A workspace's keys, newest first.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiKeys_Workspace' AND object_id = OBJECT_ID(N'flow.ApiKeys'))
    EXEC(N'CREATE INDEX IX_ApiKeys_Workspace ON flow.ApiKeys (WorkspaceId, CreatedAt DESC);');

-- Idempotency answers past their day are pruned.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_ApiIdempotency_CreatedAt' AND object_id = OBJECT_ID(N'flow.ApiIdempotency'))
    EXEC(N'CREATE INDEX IX_ApiIdempotency_CreatedAt ON flow.ApiIdempotency (CreatedAt);');

-- A workspace's endpoints (and, on each event, the ones switched on).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WebhookEndpoints_Workspace' AND object_id = OBJECT_ID(N'flow.WebhookEndpoints'))
    EXEC(N'CREATE INDEX IX_WebhookEndpoints_Workspace ON flow.WebhookEndpoints (WorkspaceId) INCLUDE (Enabled, Events);');

-- One delivery per endpoint and event.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WebhookDeliveries_Event' AND object_id = OBJECT_ID(N'flow.WebhookDeliveries'))
    EXEC(N'CREATE UNIQUE INDEX UX_WebhookDeliveries_Event ON flow.WebhookDeliveries (EndpointId, EventId);');

-- A delivery by its id (the log's Resend).
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_WebhookDeliveries_Id' AND object_id = OBJECT_ID(N'flow.WebhookDeliveries'))
    EXEC(N'CREATE UNIQUE INDEX UX_WebhookDeliveries_Id ON flow.WebhookDeliveries (Id);');

-- The worker's queue: deliveries due an attempt.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WebhookDeliveries_Due' AND object_id = OBJECT_ID(N'flow.WebhookDeliveries'))
    EXEC(N'CREATE INDEX IX_WebhookDeliveries_Due ON flow.WebhookDeliveries (NextAttemptAt, Seq) INCLUDE (LeaseUntil) WHERE Status = N''pending'';');

-- Old deliveries are pruned.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WebhookDeliveries_CreatedAt' AND object_id = OBJECT_ID(N'flow.WebhookDeliveries'))
    EXEC(N'CREATE INDEX IX_WebhookDeliveries_CreatedAt ON flow.WebhookDeliveries (CreatedAt);');

-- An endpoint's delivery log, newest first. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_WebhookDeliveries_Endpoint' AND object_id = OBJECT_ID(N'flow.WebhookDeliveries'))
    EXEC(N'CREATE INDEX IX_WebhookDeliveries_Endpoint ON flow.WebhookDeliveries (EndpointId, Seq DESC) INCLUDE (Status, EventType, CreatedAt);');
