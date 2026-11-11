/* =========================================================
   VantraFlow — 005_workspace_invites.sql
   Workspaces as a whole: their look, and invitations.

     flow.Workspaces        gains Color (the tile's #rrggbb;
                            null: Flow's green) and Logo (a
                            PNG, JPEG or WebP data URL of at
                            most 100,000 characters; null: the
                            initials on the tile)
     flow.Invites           an invitation to a workspace, one
                            row per (workspace, lower-cased
                            email): the role it carries (admin
                            or member), pending (not emailed)
                            or sent, accepted or revoked, who
                            sent it, and when it expires (14
                            days from when it was issued or
                            last resent). The link's token is
                            never stored: only its SHA-256
                            (TokenHash), so the table can't be
                            used to join. A resend makes a new
                            token; the old link stops working.

   Order: deploy the API first, then run this. Until it has
   run, the workspace look and invitations are hidden, their
   endpoints answer 503 not_ready, and adding someone who
   already has a Vantra account keeps working as before. The
   probe (FlowSchema.InvitesReadyAsync) looks for
   UX_Invites_Token, made last, by name in sys.indexes.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001 to db/004. Columns, tables,
   constraints and indexes go through EXEC() so the batch
   compiles before they exist. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- The workspace's look.
IF COL_LENGTH(N'flow.Workspaces', N'Color') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD Color NVARCHAR(7) NULL;');
IF COL_LENGTH(N'flow.Workspaces', N'Logo') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD Logo NVARCHAR(MAX) NULL;');
IF OBJECT_ID(N'flow.CK_Workspaces_Color', N'C') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces WITH CHECK ADD CONSTRAINT CK_Workspaces_Color
        CHECK (Color IS NULL OR Color LIKE N''#[0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f][0-9a-f]'');');
IF OBJECT_ID(N'flow.CK_Workspaces_Logo', N'C') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces WITH CHECK ADD CONSTRAINT CK_Workspaces_Logo
        CHECK (Logo IS NULL OR (LEN(Logo) <= 100000 AND Logo LIKE N''data:image/%''));');

-- Invitations.
IF OBJECT_ID(N'flow.Invites', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Invites
    (
        Id            UNIQUEIDENTIFIER NOT NULL,
        WorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        Email         NVARCHAR(320)    NOT NULL,
        Role          NVARCHAR(8)      NOT NULL,
        TokenHash     BINARY(32)       NOT NULL,
        Status        NVARCHAR(10)     NOT NULL CONSTRAINT DF_Invites_Status DEFAULT N''pending'',
        InvitedBy     NVARCHAR(64)     NOT NULL,
        InvitedByName NVARCHAR(160)    NULL,
        CreatedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_Invites_CreatedAt DEFAULT SYSUTCDATETIME(),
        SentAt        DATETIME2(3)     NULL,
        ExpiresAt     DATETIME2(3)     NOT NULL,
        AcceptedBy    NVARCHAR(64)     NULL,
        AcceptedAt    DATETIME2(3)     NULL,
        RevokedAt     DATETIME2(3)     NULL,
        CONSTRAINT PK_Invites PRIMARY KEY (Id),
        CONSTRAINT CK_Invites_Role CHECK (Role IN (N''admin'', N''member'')),
        CONSTRAINT CK_Invites_Status CHECK (Status IN (N''pending'', N''sent'', N''accepted'', N''revoked''))
    );');

-- One invitation per address per workspace.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Invites_Email' AND object_id = OBJECT_ID(N'flow.Invites'))
    EXEC(N'CREATE UNIQUE INDEX UX_Invites_Email ON flow.Invites (WorkspaceId, Email) INCLUDE (Status, ExpiresAt);');

-- A link's invitation, by its token's hash. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Invites_Token' AND object_id = OBJECT_ID(N'flow.Invites'))
    EXEC(N'CREATE UNIQUE INDEX UX_Invites_Token ON flow.Invites (TokenHash);');
