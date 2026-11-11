/* =========================================================
   VantraFlow — 004_oauth_connectors.sql
   Outside apps: Slack, Microsoft 365 and Google, connected
   with OAuth 2.0 per Flow workspace.

     flow.OAuthConnections  a workspace's connection to one
                            provider: the account it acts as,
                            the scopes granted, its access and
                            refresh tokens (AES-256-GCM sealed
                            by the API, bound to the row, never
                            plain) and when the access token
                            expires (refreshed before then).
                            Slack's workspace (TeamId) and the
                            app's bot user route events and mark
                            the messages Flow itself posted.
     flow.OAuthPending      a finished consent waiting for the
                            person who started it to claim it
                            (ten minutes; sealed): the callback
                            never connects on its own, so a
                            link someone else started can't
                            attach your account to their
                            workspace.
     flow.PollCursors       where each polling trigger is up to
                            (a Microsoft delta link, a Gmail
                            history id, a sheet's row count),
                            when it polls next, and how many
                            polls in a row failed (back off).
     flow.Events            its Source may now also be slack,
                            microsoft or google (the check is
                            replaced by CK_Events_Source2).

   Order: deploy the API first, then run this. Until it has
   run, the new connectors are hidden, their endpoints answer
   503 not_ready, and nothing polls. The probe
   (FlowSchema.ConnectorsReadyAsync) looks for IX_PollCursors_Due,
   made last, by name in sys.indexes.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001 to db/003. Tables, indexes and
   constraints go through EXEC() so the batch compiles before
   they exist. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'flow.OAuthConnections', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.OAuthConnections
    (
        Id              UNIQUEIDENTIFIER NOT NULL,
        WorkspaceId     UNIQUEIDENTIFIER NOT NULL,
        Provider        NVARCHAR(16)     NOT NULL,
        AccountId       NVARCHAR(200)    NOT NULL,
        AccountName     NVARCHAR(200)    NOT NULL,
        AccountEmail    NVARCHAR(256)    NULL,
        TeamId          NVARCHAR(64)     NULL,
        BotUserId       NVARCHAR(64)     NULL,
        Scopes          NVARCHAR(2000)   NOT NULL,
        AccessCipher    VARBINARY(MAX)   NOT NULL,
        RefreshCipher   VARBINARY(MAX)   NULL,
        ExpiresAt       DATETIME2(3)     NULL,
        Status          NVARCHAR(12)     NOT NULL CONSTRAINT DF_OAuthConnections_Status DEFAULT N''active'',
        LastError       NVARCHAR(400)    NULL,
        ConnectedBy     NVARCHAR(64)     NOT NULL,
        ConnectedByName NVARCHAR(160)    NULL,
        ConnectedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_OAuthConnections_ConnectedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt      DATETIME2(3)     NULL,
        RevokedAt       DATETIME2(3)     NULL,
        CONSTRAINT PK_OAuthConnections PRIMARY KEY (Id),
        CONSTRAINT CK_OAuthConnections_Provider CHECK (Provider IN (N''slack'', N''microsoft'', N''google'')),
        CONSTRAINT CK_OAuthConnections_Status CHECK (Status IN (N''active'', N''error'', N''revoked''))
    );');

IF OBJECT_ID(N'flow.OAuthPending', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.OAuthPending
    (
        Id          NVARCHAR(64)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Provider    NVARCHAR(16)     NOT NULL,
        VantraId    NVARCHAR(64)     NOT NULL,
        Payload     VARBINARY(MAX)   NOT NULL,
        ExpiresAt   DATETIME2(3)     NOT NULL,
        CONSTRAINT PK_OAuthPending PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.PollCursors', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.PollCursors
    (
        FlowId       NVARCHAR(40)     NOT NULL,
        WorkspaceId  UNIQUEIDENTIFIER NOT NULL,
        Provider     NVARCHAR(16)     NOT NULL,
        Fingerprint  NVARCHAR(64)     NOT NULL,
        Bookmark     NVARCHAR(MAX)    NULL,
        NextPollAt   DATETIME2(3)     NOT NULL,
        Failures     INT              NOT NULL CONSTRAINT DF_PollCursors_Failures DEFAULT 0,
        LastError    NVARCHAR(400)    NULL,
        LastPolledAt DATETIME2(3)     NULL,
        CONSTRAINT PK_PollCursors PRIMARY KEY (FlowId)
    );');

-- Events from the new providers: the source check, replaced.
IF OBJECT_ID(N'flow.CK_Events_Source', N'C') IS NOT NULL
    EXEC(N'ALTER TABLE flow.Events DROP CONSTRAINT CK_Events_Source;');
IF OBJECT_ID(N'flow.CK_Events_Source2', N'C') IS NULL
    EXEC(N'ALTER TABLE flow.Events WITH CHECK ADD CONSTRAINT CK_Events_Source2
        CHECK (Source IN (N''orbit'', N''projects'', N''schedule'', N''webhook'', N''manual'', N''slack'', N''microsoft'', N''google''));');

-- One live connection per provider per workspace.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_OAuthConnections_Live' AND object_id = OBJECT_ID(N'flow.OAuthConnections'))
    EXEC(N'CREATE UNIQUE INDEX UX_OAuthConnections_Live ON flow.OAuthConnections (WorkspaceId, Provider) WHERE RevokedAt IS NULL;');

-- Slack's events: the connections to a Slack workspace.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OAuthConnections_Team' AND object_id = OBJECT_ID(N'flow.OAuthConnections'))
    EXEC(N'CREATE INDEX IX_OAuthConnections_Team ON flow.OAuthConnections (TeamId) WHERE TeamId IS NOT NULL AND RevokedAt IS NULL;');

-- The prune of unclaimed consents.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_OAuthPending_ExpiresAt' AND object_id = OBJECT_ID(N'flow.OAuthPending'))
    EXEC(N'CREATE INDEX IX_OAuthPending_ExpiresAt ON flow.OAuthPending (ExpiresAt);');

-- The poll claim: cursors that are due. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_PollCursors_Due' AND object_id = OBJECT_ID(N'flow.PollCursors'))
    EXEC(N'CREATE INDEX IX_PollCursors_Due ON flow.PollCursors (NextPollAt) INCLUDE (WorkspaceId, Provider);');
