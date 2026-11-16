/* =========================================================
   VantraFlow — 014_triggers.sql
   Three new kinds of trigger: a form, a date (before or after
   a date, and a task becoming overdue) and a button on a
   record in Orbit or Projects.

     flow.FlowForms        a form trigger's public address and
                           how it's found: the workspace, the
                           flow, its slug (unique per workspace),
                           an unguessable token (the link), who
                           can fill it in (team or public),
                           whether it's published (its flow is
                           on) and whether team members see it
                           under Requests in Orbit and Projects.
                           The questions themselves live in the
                           flow's definition.
     flow.FormSubmissions  one row per answer sent: the form,
                           the submission key the page sends (a
                           retry starts nothing twice), an HMAC
                           prefix of the address (never the
                           address), who (team forms) and the
                           run it started. The per-form limit
                           counts these.
     flow.DateFirings      the date triggers' ledger, keyed by
                           (flow, record, date value, offset):
                           each date fires once, and a changed
                           date is a new key, so its run moves.
     flow.DateCursors      where each date flow's scan is up to,
                           under a lease (UPDLOCK + READPAST),
                           so several instances share the work.

   Order: deploy the API first, then run this. Until it has
   run, the new triggers are hidden in the studio, switching
   such a flow on is refused, forms answer 503 not_ready and
   the date scan waits; existing flows are unaffected. The
   probe (TriggersRepository.ProbeSql) looks for the index
   made last, IX_DateCursors_Due, by name in sys.indexes.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001. Tables and indexes go through
   EXEC() so the batch compiles before they exist. Idempotent:
   safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- Builds on db/001: stop before changing anything if it isn't there.
IF OBJECT_ID(N'flow.Flows', N'U') IS NULL
    THROW 51014, N'flow.Flows is missing: run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF OBJECT_ID(N'flow.FlowForms', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FlowForms
    (
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        Slug        NVARCHAR(60)     NOT NULL,
        Token       NVARCHAR(24)     NOT NULL,
        Title       NVARCHAR(160)    NOT NULL,
        Access      NVARCHAR(10)     NOT NULL CONSTRAINT DF_FlowForms_Access DEFAULT N''team'',
        Published   BIT              NOT NULL CONSTRAINT DF_FlowForms_Published DEFAULT 0,
        ShowInApps  BIT              NOT NULL CONSTRAINT DF_FlowForms_ShowInApps DEFAULT 0,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_FlowForms_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_FlowForms_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FlowForms PRIMARY KEY (Id),
        CONSTRAINT CK_FlowForms_Access CHECK (Access IN (N''team'', N''public''))
    );');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_FlowForms_Token' AND object_id = OBJECT_ID(N'flow.FlowForms'))
    EXEC(N'CREATE UNIQUE INDEX UX_FlowForms_Token ON flow.FlowForms (Token);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_FlowForms_Slug' AND object_id = OBJECT_ID(N'flow.FlowForms'))
    EXEC(N'CREATE UNIQUE INDEX UX_FlowForms_Slug ON flow.FlowForms (WorkspaceId, Slug);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_FlowForms_Flow' AND object_id = OBJECT_ID(N'flow.FlowForms'))
    EXEC(N'CREATE UNIQUE INDEX UX_FlowForms_Flow ON flow.FlowForms (FlowId);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FlowForms_Apps' AND object_id = OBJECT_ID(N'flow.FlowForms'))
    EXEC(N'CREATE INDEX IX_FlowForms_Apps ON flow.FlowForms (WorkspaceId, Published, ShowInApps) INCLUDE (Access, Title, Slug, Token);');

IF OBJECT_ID(N'flow.FormSubmissions', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FormSubmissions
    (
        Id            NVARCHAR(40)     NOT NULL,
        FormId        NVARCHAR(40)     NOT NULL,
        WorkspaceId   UNIQUEIDENTIFIER NOT NULL,
        FlowId        NVARCHAR(40)     NOT NULL,
        SubmissionKey NVARCHAR(64)     NOT NULL,
        IpHash        NVARCHAR(16)     NULL,
        VantraId      NVARCHAR(64)     NULL,
        RunId         NVARCHAR(40)     NULL,
        SubmittedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_FormSubmissions_At DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FormSubmissions PRIMARY KEY (Id)
    );');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'UX_FormSubmissions_Key' AND object_id = OBJECT_ID(N'flow.FormSubmissions'))
    EXEC(N'CREATE UNIQUE INDEX UX_FormSubmissions_Key ON flow.FormSubmissions (FormId, SubmissionKey);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FormSubmissions_Form' AND object_id = OBJECT_ID(N'flow.FormSubmissions'))
    EXEC(N'CREATE INDEX IX_FormSubmissions_Form ON flow.FormSubmissions (FormId, SubmittedAt) INCLUDE (IpHash);');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_FormSubmissions_At' AND object_id = OBJECT_ID(N'flow.FormSubmissions'))
    EXEC(N'CREATE INDEX IX_FormSubmissions_At ON flow.FormSubmissions (SubmittedAt);');

IF OBJECT_ID(N'flow.DateFirings', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.DateFirings
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        RecordId    NVARCHAR(80)     NOT NULL,
        DateValue   NVARCHAR(40)     NOT NULL,
        OffsetKey   NVARCHAR(40)     NOT NULL,
        FireAt      DATETIME2(3)     NOT NULL,
        FiredAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_DateFirings_FiredAt DEFAULT SYSUTCDATETIME(),
        RunId       NVARCHAR(40)     NULL,
        CONSTRAINT PK_DateFirings PRIMARY KEY (FlowId, RecordId, DateValue, OffsetKey)
    );');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DateFirings_FiredAt' AND object_id = OBJECT_ID(N'flow.DateFirings'))
    EXEC(N'CREATE INDEX IX_DateFirings_FiredAt ON flow.DateFirings (FiredAt);');

IF OBJECT_ID(N'flow.DateCursors', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.DateCursors
    (
        FlowId       NVARCHAR(40)     NOT NULL,
        WorkspaceId  UNIQUEIDENTIFIER NOT NULL,
        StartedAt    DATETIME2(3)     NOT NULL CONSTRAINT DF_DateCursors_StartedAt DEFAULT SYSUTCDATETIME(),
        ScannedUntil DATETIME2(3)     NOT NULL,
        NextScanAt   DATETIME2(3)     NOT NULL,
        Failures     INT              NOT NULL CONSTRAINT DF_DateCursors_Failures DEFAULT 0,
        LastError    NVARCHAR(400)    NULL,
        LeaseUntil   DATETIME2(3)     NULL,
        LeaseBy      NVARCHAR(64)     NULL,
        CONSTRAINT PK_DateCursors PRIMARY KEY (FlowId)
    );');

-- Last: the probe's index.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_DateCursors_Due' AND object_id = OBJECT_ID(N'flow.DateCursors'))
    EXEC(N'CREATE INDEX IX_DateCursors_Due ON flow.DateCursors (NextScanAt) INCLUDE (LeaseUntil);');
