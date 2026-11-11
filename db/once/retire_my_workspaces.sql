/* =========================================================
   Vantra Flow (vantraflow) — db/once/retire_my_workspaces.sql
   Shared Vantra workspaces, stage 3: retire the two empty
   automatic "My workspace"s Flow made before every workspace
   became a shared one. Run ONCE, by hand, on vantraflow.

   GUARDED. It touches only these two ids:
     B25498CA-A9BB-4AD3-BD8C-559C639B82E8
     2CEB40CB-FB3F-4BFD-96AA-89AC2B3248A7
   and deletes one only while it still has 0 flows and 0
   connections (apps, and outside apps such as Slack or
   Google). One that has either is kept, untouched. Nothing
   else is changed. Its rows (members, usage, any invitation)
   go with it, as deleting a workspace in Flow does.

   One transaction: all or nothing. The last result set says
   what happened to each: deleted, kept (not empty) or
   already gone. Safe to run again.

   Azure-ready: no USE, no GO, one batch.
   ========================================================= */

SET NOCOUNT ON;
SET XACT_ABORT ON;

IF OBJECT_ID(N'flow.Workspaces', N'U') IS NULL OR OBJECT_ID(N'flow.Flows', N'U') IS NULL OR OBJECT_ID(N'flow.Connections', N'U') IS NULL
    THROW 51001, N'Connect to the vantraflow database (flow.Workspaces is missing here). Nothing was changed.', 1;

DECLARE @Retire TABLE (Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY);
INSERT INTO @Retire (Id) VALUES
    (CAST(N'B25498CA-A9BB-4AD3-BD8C-559C639B82E8' AS UNIQUEIDENTIFIER)),
    (CAST(N'2CEB40CB-FB3F-4BFD-96AA-89AC2B3248A7' AS UNIQUEIDENTIFIER));

DECLARE @Report TABLE (Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY, Name NVARCHAR(120) NULL, Flows INT NOT NULL, Connections INT NOT NULL, Outcome NVARCHAR(40) NOT NULL);
DECLARE @Go TABLE (Id UNIQUEIDENTIFIER NOT NULL PRIMARY KEY);

BEGIN TRAN;

INSERT INTO @Report (Id, Name, Flows, Connections, Outcome)
SELECT r.Id, w.Name,
       (SELECT COUNT(*) FROM flow.Flows f WITH (UPDLOCK, HOLDLOCK) WHERE f.WorkspaceId = r.Id),
       (SELECT COUNT(*) FROM flow.Connections c WITH (UPDLOCK, HOLDLOCK) WHERE c.WorkspaceId = r.Id),
       CASE WHEN w.Id IS NULL THEN N'already gone' ELSE N'kept (not empty)' END
FROM @Retire r
LEFT JOIN flow.Workspaces w WITH (UPDLOCK, HOLDLOCK) ON w.Id = r.Id;

IF OBJECT_ID(N'flow.OAuthConnections', N'U') IS NOT NULL
    UPDATE rep SET Connections = rep.Connections + (SELECT COUNT(*) FROM flow.OAuthConnections o WITH (UPDLOCK, HOLDLOCK) WHERE o.WorkspaceId = rep.Id)
    FROM @Report rep;

UPDATE @Report SET Outcome = N'deleted' WHERE Name IS NOT NULL AND Flows = 0 AND Connections = 0;
INSERT INTO @Go (Id) SELECT Id FROM @Report WHERE Outcome = N'deleted';

IF OBJECT_ID(N'flow.FlowVersions', N'U') IS NOT NULL DELETE FROM flow.FlowVersions WHERE WorkspaceId IN (SELECT Id FROM @Go);
IF OBJECT_ID(N'flow.PollCursors', N'U') IS NOT NULL DELETE FROM flow.PollCursors WHERE WorkspaceId IN (SELECT Id FROM @Go);
IF OBJECT_ID(N'flow.OAuthPending', N'U') IS NOT NULL DELETE FROM flow.OAuthPending WHERE WorkspaceId IN (SELECT Id FROM @Go);
IF OBJECT_ID(N'flow.Invites', N'U') IS NOT NULL DELETE FROM flow.Invites WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE s FROM flow.RunSteps s WHERE s.RunId IN (SELECT r.Id FROM flow.Runs r WHERE r.WorkspaceId IN (SELECT Id FROM @Go));
DELETE FROM flow.Runs WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.Events WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.FiredKeys WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.Subscriptions WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.[Usage] WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.WorkspaceMembers WHERE WorkspaceId IN (SELECT Id FROM @Go);
DELETE FROM flow.Workspaces WHERE Id IN (SELECT Id FROM @Go);

COMMIT;

SELECT Id, Name, Flows, Connections, Outcome FROM @Report ORDER BY Id;
