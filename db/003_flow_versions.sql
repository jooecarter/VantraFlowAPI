/* =========================================================
   VantraFlow — 003_flow_versions.sql
   Versions: every save of a flow keeps a copy, so an earlier
   one can be looked at and restored.

     flow.FlowVersions      one row per save: the flow's name
                            and definition as saved, its
                            version number (flow.Flows.Version
                            after the save, the number runs
                            record in flow.Runs.FlowVersion),
                            who saved it and when, how many
                            steps it had, and a note ("Restored
                            version 3"). The newest 100 per
                            flow are kept.

   Order: deploy the API first, then run this. Until it has
   run, saves keep no versions, the studio hides the version
   list and its endpoints answer 503 not_ready. The probe
   (FlowSchema.VersionsReadyAsync) looks for the table by name
   (OBJECT_ID).

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001 and db/002. The table goes
   through EXEC() so the batch compiles whatever has run
   before. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF OBJECT_ID(N'flow.FlowVersions', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.FlowVersions
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        Version     INT              NOT NULL,
        Name        NVARCHAR(160)    NOT NULL,
        Definition  NVARCHAR(MAX)    NOT NULL,
        StepCount   INT              NOT NULL CONSTRAINT DF_FlowVersions_StepCount DEFAULT 0,
        Note        NVARCHAR(200)    NULL,
        SavedBy     NVARCHAR(64)     NOT NULL,
        SavedByName NVARCHAR(160)    NULL,
        SavedAt     DATETIME2(3)     NOT NULL CONSTRAINT DF_FlowVersions_SavedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_FlowVersions PRIMARY KEY (WorkspaceId, FlowId, Version)
    );');

