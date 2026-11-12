/* =========================================================
   VantraFlow — 010_reliability.sql
   Error handling and reliability: auto-pause and the daily
   failure digest. (Retries, timeouts, error paths and "Retry
   from here" live in the flows' definitions and the runs'
   data, and need nothing here.)

     flow.Flows       gains PauseAfterFailures: switch the flow
                      off after this many failed runs in a row
                      (null: never, the default), with the
                      reason on it and an email to the owners
     flow.Workspaces  gains FailureDigest: a daily email to
                      owners and admins of the flows that failed
                      in the last 24 hours. Off for the
                      workspaces there today, on for every new
                      one (its default becomes 1 once the column
                      is added).
                      DigestSentOn: the workspace's local day
                      the last digest went for (the claim that
                      sends it once a day)

   Order: deploy the API first, then run this. Until it has
   run, the digest and auto-pause settings are hidden and their
   endpoints answer 503 not_ready. The probe
   (FlowSchema.ReliabilityReadyAsync) looks for the column added
   last, DigestSentOn, by name in sys.columns.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001. Columns and constraints go
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

-- Needs db/001 (the workspaces and flows): stop before changing anything if it hasn't run.
IF OBJECT_ID(N'flow.Workspaces', N'U') IS NULL OR OBJECT_ID(N'flow.Flows', N'U') IS NULL
    THROW 51010, N'Run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF COL_LENGTH(N'flow.Flows', N'PauseAfterFailures') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD PauseAfterFailures TINYINT NULL;');

-- The digest: off (0) for every workspace there now, then on (1) by default for new ones.
IF COL_LENGTH(N'flow.Workspaces', N'FailureDigest') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD FailureDigest BIT NOT NULL CONSTRAINT DF_Workspaces_FailureDigest_Off DEFAULT 0 WITH VALUES;');

IF EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_Workspaces_FailureDigest_Off' AND parent_object_id = OBJECT_ID(N'flow.Workspaces'))
    EXEC(N'ALTER TABLE flow.Workspaces DROP CONSTRAINT DF_Workspaces_FailureDigest_Off;');

IF NOT EXISTS (SELECT 1 FROM sys.default_constraints WHERE name = N'DF_Workspaces_FailureDigest' AND parent_object_id = OBJECT_ID(N'flow.Workspaces'))
    EXEC(N'ALTER TABLE flow.Workspaces ADD CONSTRAINT DF_Workspaces_FailureDigest DEFAULT 1 FOR FailureDigest;');

IF COL_LENGTH(N'flow.Workspaces', N'DigestSentOn') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD DigestSentOn DATE NULL;');
