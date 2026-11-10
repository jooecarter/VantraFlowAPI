/* =========================================================
   VantraFlow — 002_failure_alerts.sql
   Failure alerts: "Email me when this flow fails", and a
   workspace default for the flows that don't say.

     flow.Workspaces.AlertOnFailure
                            the default: email a flow's
                            creator when it fails (off)
     flow.Flows.AlertOnFailure
                            the flow's own choice: 1 on, 0 off,
                            NULL the workspace's default
     flow.Flows.AlertTo     who asked (a VantraId); NULL: the
                            flow's creator
     flow.Flows.AlertedAt   when the last alert went: at most
                            one email per flow per hour, the
                            failures since then in one digest
                            (the claim is an UPDATE on this,
                            so two instances never both send)

   Order: deploy the API first, then run this. Until it has
   run, the studio hides the alert settings, their endpoints
   answer 503 not_ready, and no alerts are sent. The probe
   (FlowSchema.AlertsReadyAsync) looks for AlertedAt, added
   last, by name in sys.columns.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001. Each column goes through
   EXEC() so the batch compiles whatever has run before.
   Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

IF COL_LENGTH(N'flow.Workspaces', N'AlertOnFailure') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD AlertOnFailure BIT NOT NULL CONSTRAINT DF_Workspaces_AlertOnFailure DEFAULT 0;');

IF COL_LENGTH(N'flow.Flows', N'AlertOnFailure') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD AlertOnFailure BIT NULL;');

IF COL_LENGTH(N'flow.Flows', N'AlertTo') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD AlertTo NVARCHAR(64) NULL;');

-- Last: the probe looks for it.
IF COL_LENGTH(N'flow.Flows', N'AlertedAt') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD AlertedAt DATETIME2(3) NULL;');
