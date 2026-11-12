/* =========================================================
   VantraFlow — 011_team.sql
   Team and organisation: folders, tags and owners for flows,
   comments on a flow, and the workspace's activity log.

     flow.Flows           two new columns: FolderId (the
                          folder it is in, null at the top
                          level) and OwnerId (its owner's
                          Vantra id, null meaning whoever
                          created it).
     flow.Folders         a workspace's folders, one level.
     flow.Tags            a workspace's tags and their colours.
     flow.FlowTags        which tags each flow has.
     flow.FlowComments    comments on a flow, with @mentions
                          kept in the text.
     flow.MentionNotices  when each person was last emailed
                          about a mention on a flow, so it is
                          at most one email an hour.
     flow.Activity        who changed what, per workspace.

   Order: deploy the API first, then run this. Until it has
   run, folders, tags, owners, comments and the activity log
   hide themselves and their endpoints answer 503 not_ready.
   The probe (TeamRepository.ProbeSql) looks for
   IX_Activity_Flow, made last, by name in sys.indexes.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001. Tables, columns and indexes go
   through EXEC() so the batch compiles before they exist.
   Idempotent: safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- Needs db/001 (flow.Flows and flow.Workspaces): stop before changing anything if it hasn't run.
IF OBJECT_ID(N'flow.Flows', N'U') IS NULL OR OBJECT_ID(N'flow.Workspaces', N'U') IS NULL
    THROW 51011, N'Run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF COL_LENGTH(N'flow.Flows', N'FolderId') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD FolderId NVARCHAR(40) NULL;');

IF COL_LENGTH(N'flow.Flows', N'OwnerId') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD OwnerId NVARCHAR(64) NULL;');

IF OBJECT_ID(N'flow.Folders', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Folders
    (
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Name        NVARCHAR(80)     NOT NULL,
        CreatedBy   NVARCHAR(64)     NOT NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Folders_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Folders_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Folders PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.Tags', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Tags
    (
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Name        NVARCHAR(40)     NOT NULL,
        Color       NVARCHAR(16)     NOT NULL,
        CreatedBy   NVARCHAR(64)     NOT NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Tags_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_Tags PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.FlowTags', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FlowTags
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        TagId       NVARCHAR(40)     NOT NULL,
        CONSTRAINT PK_FlowTags PRIMARY KEY (WorkspaceId, FlowId, TagId)
    );');

IF OBJECT_ID(N'flow.FlowComments', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FlowComments
    (
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        AuthorId    NVARCHAR(64)     NOT NULL,
        AuthorName  NVARCHAR(160)    NULL,
        Body        NVARCHAR(2000)   NOT NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_FlowComments_CreatedAt DEFAULT SYSUTCDATETIME(),
        EditedAt    DATETIME2(3)     NULL,
        CONSTRAINT PK_FlowComments PRIMARY KEY (Id)
    );');

IF OBJECT_ID(N'flow.MentionNotices', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.MentionNotices
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        VantraId    NVARCHAR(64)     NOT NULL,
        SentAt      DATETIME2(3)     NOT NULL,
        CONSTRAINT PK_MentionNotices PRIMARY KEY (WorkspaceId, FlowId, VantraId)
    );');

IF OBJECT_ID(N'flow.Activity', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Activity
    (
        Id          BIGINT IDENTITY(1, 1) NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        At          DATETIME2(3)     NOT NULL CONSTRAINT DF_Activity_At DEFAULT SYSUTCDATETIME(),
        ActorId     NVARCHAR(64)     NOT NULL,
        ActorName   NVARCHAR(160)    NULL,
        Kind        NVARCHAR(32)     NOT NULL,
        FlowId      NVARCHAR(40)     NULL,
        FlowName    NVARCHAR(160)    NULL,
        Version     INT              NULL,
        Detail      NVARCHAR(400)    NULL,
        CONSTRAINT PK_Activity PRIMARY KEY (Id)
    );');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Folders_Name' AND object_id = OBJECT_ID(N'flow.Folders'))
    EXEC(N'CREATE UNIQUE INDEX UX_Folders_Name ON flow.Folders (WorkspaceId, Name);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_Tags_Name' AND object_id = OBJECT_ID(N'flow.Tags'))
    EXEC(N'CREATE UNIQUE INDEX UX_Tags_Name ON flow.Tags (WorkspaceId, Name);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Flows_Folder' AND object_id = OBJECT_ID(N'flow.Flows'))
    EXEC(N'CREATE INDEX IX_Flows_Folder ON flow.Flows (WorkspaceId, FolderId) WHERE FolderId IS NOT NULL;');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowComments_Flow' AND object_id = OBJECT_ID(N'flow.FlowComments'))
    EXEC(N'CREATE INDEX IX_FlowComments_Flow ON flow.FlowComments (WorkspaceId, FlowId, CreatedAt);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Activity_Workspace' AND object_id = OBJECT_ID(N'flow.Activity'))
    EXEC(N'CREATE INDEX IX_Activity_Workspace ON flow.Activity (WorkspaceId, Id DESC) INCLUDE (Kind, ActorId);');

-- A flow's own history. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Activity_Flow' AND object_id = OBJECT_ID(N'flow.Activity'))
    EXEC(N'CREATE INDEX IX_Activity_Flow ON flow.Activity (WorkspaceId, FlowId, Id DESC) WHERE FlowId IS NOT NULL;');
