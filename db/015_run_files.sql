/* =========================================================
   VantraFlow — 015_run_files.sql
   Files a step makes, for later steps to use: Vantra Orbit's
   "Get a report" step's PDF, attached by Send email.

     flow.RunFiles   one row per file a run's step made: the
                     workspace, the run and step, its name,
                     type and size, and its bytes (at most
                     10 MB). A step's output names it by id;
                     only steps of the same run can read it,
                     and it's deleted 7 days on (a waiting run
                     that comes back later says the file has
                     gone).

   Order: deploy the API first, then run this. Until it has
   run, "Make a PDF" and the Attachments picker are hidden in
   the studio and a step that asks for a PDF fails with a
   plain message; nothing else changes. The probe looks for
   the index made last, IX_RunFiles_Expires, by name.

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
    THROW 51015, N'flow.Flows is missing: run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF OBJECT_ID(N'flow.RunFiles', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.RunFiles
    (
        Id          NVARCHAR(40)     NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        RunId       NVARCHAR(40)     NOT NULL,
        StepId      NVARCHAR(64)     NOT NULL,
        Name        NVARCHAR(200)    NOT NULL,
        ContentType NVARCHAR(100)    NOT NULL,
        Size        INT              NOT NULL,
        Bytes       VARBINARY(MAX)   NOT NULL,
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_RunFiles_CreatedAt DEFAULT SYSUTCDATETIME(),
        ExpiresAt   DATETIME2(3)     NOT NULL,
        CONSTRAINT PK_RunFiles PRIMARY KEY (WorkspaceId, Id),
        CONSTRAINT CK_RunFiles_Size CHECK (Size BETWEEN 0 AND 10485760)
    );');

-- Housekeeping: expired files. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunFiles_Expires' AND object_id = OBJECT_ID(N'flow.RunFiles'))
    EXEC(N'CREATE INDEX IX_RunFiles_Expires ON flow.RunFiles (ExpiresAt);');
