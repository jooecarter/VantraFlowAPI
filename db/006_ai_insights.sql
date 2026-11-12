/* =========================================================
   VantraFlow — 006_ai_insights.sql
   The AI flow builder and Insights.

     flow.Usage             gains AiDrafts: the flows the AI
                            drafted in the month (the monthly
                            cap, Ai:MonthlyCap), on the month's
                            row
     flow.Workspaces        gains MinutesPerStep: the minutes a
                            step saves, for "time saved" (null:
                            the default, 2)
     flow.Flows             gains MinutesPerRun: the minutes a
                            run of this flow saves (null: its
                            steps times the workspace's figure)
     flow.RunDays           each flow's runs per day, kept when
                            the runs themselves are pruned
                            (Flow:KeepRunsDays), so Insights
                            reaches back 90 days: succeeded,
                            failed and other runs, and the
                            steps the succeeded ones ran
     IX_Runs_Insights       a workspace's runs by start, with
                            what Insights adds up
     IX_RunSteps_Failed     a workspace's failed steps by start

   Order: deploy the API first, then run this. Until it has
   run, the AI builder is "Not set up yet", Insights reads the
   runs still kept and its time-saved settings are hidden, and
   pruning keeps no daily totals. The probe
   (FlowSchema.InsightsReadyAsync) looks for
   IX_RunSteps_Failed, made last, by name in sys.indexes.

   Azure-ready: no USE, no GO — run it as one batch connected
   to `vantraflow`, after db/001 to db/005. Columns, tables
   and indexes go through EXEC() so the batch compiles before
   they exist. Idempotent — safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- The AI builder's monthly count.
IF COL_LENGTH(N'flow.Usage', N'AiDrafts') IS NULL
    EXEC(N'ALTER TABLE flow.Usage ADD AiDrafts INT NOT NULL CONSTRAINT DF_Usage_AiDrafts DEFAULT 0;');

-- Time saved: the workspace's minutes per step, and a flow's own minutes per run.
IF COL_LENGTH(N'flow.Workspaces', N'MinutesPerStep') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD MinutesPerStep DECIMAL(6, 2) NULL;');
IF COL_LENGTH(N'flow.Flows', N'MinutesPerRun') IS NULL
    EXEC(N'ALTER TABLE flow.Flows ADD MinutesPerRun DECIMAL(7, 2) NULL;');

-- Each flow's runs per day, kept past the runs' own pruning.
IF OBJECT_ID(N'flow.RunDays', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.RunDays
    (
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        [Day]       DATE             NOT NULL,
        FlowId      NVARCHAR(40)     NOT NULL,
        FlowName    NVARCHAR(160)    NOT NULL,
        Succeeded   INT              NOT NULL CONSTRAINT DF_RunDays_Succeeded DEFAULT 0,
        Failed      INT              NOT NULL CONSTRAINT DF_RunDays_Failed DEFAULT 0,
        Other       INT              NOT NULL CONSTRAINT DF_RunDays_Other DEFAULT 0,
        Steps       INT              NOT NULL CONSTRAINT DF_RunDays_Steps DEFAULT 0,
        CONSTRAINT PK_RunDays PRIMARY KEY (WorkspaceId, [Day], FlowId)
    );');

-- Insights: a workspace's runs in a range, with what it adds up.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Runs_Insights' AND object_id = OBJECT_ID(N'flow.Runs'))
    EXEC(N'CREATE INDEX IX_Runs_Insights ON flow.Runs (WorkspaceId, StartedAt) INCLUDE (FlowId, Status, Test, DurationMs, StepCount);');

-- Insights: the steps that failed. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_RunSteps_Failed' AND object_id = OBJECT_ID(N'flow.RunSteps'))
    EXEC(N'CREATE INDEX IX_RunSteps_Failed ON flow.RunSteps (WorkspaceId, StartedAt) INCLUDE (RunId, StepId, Type, App, StepAction, Name, Error) WHERE Status = N''failed'';');
