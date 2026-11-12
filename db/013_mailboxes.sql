/* =========================================================
   VantraFlow — 013_mailboxes.sql
   Send from your own mailbox: SMTP mailboxes connected to a
   workspace, and the workspace's default sender for Send
   email steps.

     flow.Mailboxes        a workspace's SMTP mailboxes: the
                           server (host, port, STARTTLS or
                           SSL/TLS), the username, the password
                           (AES-256-GCM sealed by the API, bound
                           to the row; wiped when the mailbox is
                           removed) and the From address and
                           name. A removed mailbox keeps its row
                           (RemovedAt set) so a step that still
                           names it can say which one it was.
     flow.Workspaces       one new column, DefaultSender: who
                           Send email steps set to "Workspace
                           default" send from. NULL is Vantra
                           Flow's own mailbox, as before;
                           otherwise "microsoft", "google" or
                           "smtp:<mailbox id>".

   Order: deploy the API first, then run this. Until it has
   run, SMTP mailboxes and the default sender hide themselves
   (503 not_ready) and every step sends as it did. The probe
   (MailboxesRepository.ProbeSql) looks for the column
   DefaultSender, added last, by name in sys.columns.

   Azure-ready: no USE, no GO. Run it as one batch connected to
   `vantraflow`, after db/001. The table, the column and the
   index go through EXEC() so the batch compiles before they
   exist. Idempotent: safe to re-run.
   ========================================================= */

SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET ARITHABORT ON;
SET NUMERIC_ROUNDABORT OFF;

-- Alters flow.Workspaces (db/001): stop before changing anything if it isn't there.
IF OBJECT_ID(N'flow.Workspaces', N'U') IS NULL
    THROW 51013, N'flow.Workspaces is missing: run db/001_flow.sql on this database first, then run this again. Nothing was changed.', 1;

IF OBJECT_ID(N'flow.Mailboxes', N'U') IS NULL
    EXEC(N'CREATE TABLE flow.Mailboxes
    (
        Id             NVARCHAR(40)     NOT NULL,
        WorkspaceId    UNIQUEIDENTIFIER NOT NULL,
        Host           NVARCHAR(253)    NOT NULL,
        Port           INT              NOT NULL,
        Security       NVARCHAR(10)     NOT NULL,
        Username       NVARCHAR(256)    NOT NULL,
        PasswordCipher VARBINARY(2048)  NULL,
        FromEmail      NVARCHAR(256)    NOT NULL,
        FromName       NVARCHAR(100)    NULL,
        Status         NVARCHAR(12)     NOT NULL CONSTRAINT DF_Mailboxes_Status DEFAULT N''active'',
        LastError      NVARCHAR(400)    NULL,
        CreatedBy      NVARCHAR(64)     NOT NULL,
        CreatedByName  NVARCHAR(160)    NULL,
        CreatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Mailboxes_CreatedAt DEFAULT SYSUTCDATETIME(),
        UpdatedAt      DATETIME2(3)     NOT NULL CONSTRAINT DF_Mailboxes_UpdatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt     DATETIME2(3)     NULL,
        RemovedAt      DATETIME2(3)     NULL,
        CONSTRAINT PK_Mailboxes PRIMARY KEY (Id),
        CONSTRAINT CK_Mailboxes_Port CHECK (Port IN (25, 465, 587, 2525)),
        CONSTRAINT CK_Mailboxes_Security CHECK (Security IN (N''starttls'', N''ssl'')),
        CONSTRAINT CK_Mailboxes_Status CHECK (Status IN (N''active'', N''error''))
    );');

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_Mailboxes_Workspace' AND object_id = OBJECT_ID(N'flow.Mailboxes'))
    EXEC(N'CREATE INDEX IX_Mailboxes_Workspace ON flow.Mailboxes (WorkspaceId, RemovedAt) INCLUDE (FromEmail, Status);');

-- Last: the probe's column.
IF COL_LENGTH(N'flow.Workspaces', N'DefaultSender') IS NULL
    EXEC(N'ALTER TABLE flow.Workspaces ADD DefaultSender NVARCHAR(60) NULL;');
