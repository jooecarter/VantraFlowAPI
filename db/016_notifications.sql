/* =========================================================
   VantraFlow — 016_notifications.sql
   Notifications: the bell, the Inbox page and Settings →
   Notifications.

     flow.Notifications  one row per note to one person in one
                         workspace: its kind (approval,
                         flow_failed, flow_paused, connection,
                         report_sent, members), the parts it's
                         written in (a lead, the bold title, the
                         rest), a link in the studio, how many
                         times it happened (a flow's failures in
                         an hour are one note, "failed 3 times"),
                         when, and when it was read. Kept 90 days.
     flow.UserProfiles   per person (VantraId): the notification
                         switches (in the app, by email, for each
                         kind, as JSON) and their job title.

   Order: deploy the API first, then run this. Until it has
   run, the bell shows the last day's failed runs as it always
   has, Settings → Notifications says it arrives with the next
   server update, and every email goes as before. The probe
   looks for the index made last, IX_Notifications_Recipient,
   by name.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001 and db/011. Tables and indexes go
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

-- Builds on db/001 and db/011 (a flow's owner): stop before changing anything if either isn't there.
IF OBJECT_ID(N'flow.Flows', N'U') IS NULL
    THROW 51016, N'flow.Flows is missing: run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;
IF COL_LENGTH(N'flow.Flows', N'OwnerId') IS NULL
    THROW 51016, N'flow.Flows.OwnerId is missing: run db/011_team.sql on this database first, then run this again. Nothing was changed.', 1;

IF OBJECT_ID(N'flow.Notifications', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Notifications
    (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        WorkspaceId UNIQUEIDENTIFIER NOT NULL,
        Recipient   NVARCHAR(64)     NOT NULL,
        Kind        NVARCHAR(24)     NOT NULL,
        RefKey      NVARCHAR(100)    NULL,
        Lead        NVARCHAR(60)     NULL,
        Title       NVARCHAR(200)    NOT NULL,
        Body        NVARCHAR(400)    NULL,
        Link        NVARCHAR(400)    NULL,
        Times       INT              NOT NULL CONSTRAINT DF_Notifications_Times DEFAULT 1,
        GroupedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Notifications_GroupedAt DEFAULT SYSUTCDATETIME(),
        CreatedAt   DATETIME2(3)     NOT NULL CONSTRAINT DF_Notifications_CreatedAt DEFAULT SYSUTCDATETIME(),
        ReadAt      DATETIME2(3)     NULL,
        CONSTRAINT PK_Notifications PRIMARY KEY (Id),
        CONSTRAINT CK_Notifications_Times CHECK (Times >= 1)
    );');

IF OBJECT_ID(N'flow.UserProfiles', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.UserProfiles
    (
        VantraId          NVARCHAR(64)   NOT NULL,
        JobTitle          NVARCHAR(100)  NULL,
        NotificationPrefs NVARCHAR(2000) NULL,
        UpdatedAt         DATETIME2(3)   NOT NULL CONSTRAINT DF_UserProfiles_UpdatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_UserProfiles PRIMARY KEY (VantraId),
        CONSTRAINT CK_UserProfiles_Prefs CHECK (NotificationPrefs IS NULL OR ISJSON(NotificationPrefs) = 1)
    );');

-- Housekeeping: notes older than 90 days.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Notifications_Created' AND object_id = OBJECT_ID(N'flow.Notifications'))
    EXEC(N'CREATE INDEX IX_Notifications_Created ON flow.Notifications (CreatedAt);');

-- A person's notes in a workspace, newest first. Last: the probe looks for it.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Notifications_Recipient' AND object_id = OBJECT_ID(N'flow.Notifications'))
    EXEC(N'CREATE INDEX IX_Notifications_Recipient ON flow.Notifications (Recipient, WorkspaceId, CreatedAt DESC) INCLUDE (ReadAt, Kind, RefKey, GroupedAt);');
