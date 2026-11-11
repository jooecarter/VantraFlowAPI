# VantraFlowAPI

The API behind **Vantra Flow**, the Vantra suite's automation studio. Flow connects Vantra Orbit, Vantra Projects
and the web: when something happens in one app, the steps that follow run in another. The studio (SPA) is
[vantra-flow](../vantra-flow).

ASP.NET Core (.NET 10), Dapper, Azure SQL. It is laid out like VantraOrbitAPI:

```
src/VantraFlow.Api        the API
tests/VantraFlow.Api.Tests xunit (no database needed)
db/001_flow.sql           the schema: one idempotent, Azure-ready batch
db/002_failure_alerts.sql failure alerts: the per-flow setting and the workspace default
db/003_flow_versions.sql  versions: a copy of each save, to look at and restore
db/004_oauth_connectors.sql Slack, Microsoft 365 and Google: OAuth connections, waiting consents, poll cursors
tools/sqlcheck            Orbit's ScriptDom checker: every query and migration parsed as T-SQL
scripts/sqlcheck.sh
```

```
dotnet build
dotnet test
bash scripts/sqlcheck.sh
```

## What it does

- **Sign-in.** It uses the shared Vantra identity (`dbo.UserDetails` in the `Vantra` database), the suite's
  `Jwt:Key` and `Jwt:Issuer`, and Flow's own audience, `vantra-flow`. The VantraId is the token's `sub`. Password,
  Google and Microsoft sign-in work as in Orbit's `AuthController`.
- **Two-step sign-in.** Flow honours the two-step sign-in people set up in Orbit or Projects (Flow doesn't set it
  up: Orbit's Settings → Security does). Each app keeps it in its own database, so Flow asks them server to server
  with the same signed assertion it connects with (`POST api/integrations/flow/v1/2fa/status` and `…/2fa/verify`).
  When either has it on, password, Google and Microsoft sign-in answer `{ twoFactorRequired, challenge }`: a
  5-minute token for its own audience, `vantra-flow-2fa`, which is never a session. `POST /api/auth/2fa` takes it
  with the authenticator's code (or an Orbit backup code). Five tries per challenge (`flow.TwoFactorChallenges`,
  and the apps count too), spent once it signs someone in. The session then carries `amr: otp`. Listing and
  connecting an app's workspaces needs that when the person has two-step sign-in on, and the assertion carries it so
  the app checks again. If an app can't answer, sign-in stops (503 `two_factor_unavailable`).
- **Workspaces.** Flow has its own workspaces (`flow.Workspaces`), with owner, admin and member roles. Your first
  visit makes one for you. Each workspace links to one Orbit workspace and one Projects workspace through its
  connections. Only owners and admins connect apps.
- **Connections.** Each connection has a connect step, triggers and actions:
  - **Connect.** Flow signs a two-minute assertion (audience `vantra-flow-to-orbit` or `vantra-flow-to-projects`).
    The app checks that the person owns or administers the workspace they chose there, and hands back an API key
    named "Vantra Flow". Flow seals the key (AES-256-GCM, bound to the connection's row) and never shows it.
  - **Triggers.** Flow registers a signed webhook in the app: one per Orbit table that a flow listens to, and one
    per Projects workspace. Events land at `POST /api/events/{app}/{subscription}`. Flow checks the signature and
    the freshness of the event, de-duplicates it and queues it.
  - **Actions.** Flow calls the app's API with the connection's key. Orbit uses REST API v1 with an
    Idempotency-Key. Projects uses its `api/integrations/flow/v1`.
- **Engine.** It is adapted from Orbit's automation engine (`Services/Automations`). Flow adds:
  - runs across apps;
  - step outputs that later steps use as tokens (`{{steps.s1.id}}`);
  - delays;
  - if / else branches;
  - "Repeat for each": the steps inside run once per item of a list from the trigger or an earlier step
    (`{{loop.item…}}`), at most 100 items (a test: the first 3), stopping or carrying on when an item fails, with
    each item's result in the run log;
  - paths: up to five, each with its own condition, tried top to bottom, and an optional "otherwise" (if / else
    keeps its own shape, so older flows run as they did);
  - "Only continue if": a filter with and / or groups; when it doesn't pass the run stops and succeeds (inside a
    Repeat for each, just that item stops);
  - the formatter: text (trim, case, replace, split, extract with a regular expression under a 250 ms limit),
    numbers (format, round, maths), dates (parse, format, add or subtract, time zone) and lookup tables, each with
    one clear result, `{{steps.id.output}}`;
  - "Set variable": set, add to or append to a run's variable, read later as `{{vars.name}}` (it carries across a
    loop's items, so a loop can count or collect);
  - "Run another flow": starts a flow whose trigger is "Called by another flow" with the inputs you give it
    (`{{trigger.input…}}`), without waiting; flows can call each other at most three deep;
  - retries with backoff;
  - a run log with each step's input, output, error and duration;
  - Replay.

  A run starts from a snapshot of the flow's definition. The run records its position, so a delay or a retry
  picks up exactly where it stopped.
- **Workers.** `FlowWorker` works the event queue and the runs under leases (UPDLOCK + READPAST), so it is safe
  with several instances. It fires schedule slots once a minute, in the workspace's time zone, and prunes old
  data hourly. **The App Service needs "Always On".**
- **Limits.**
  - Each workspace has a monthly run quota and a per-minute rate.
  - Each step has a 30-second timeout.
  - A flow has at most 25 steps.
  - Email has a daily cap per workspace and goes through Orbit's `EmailService` (Microsoft Graph, else SMTP).
  - The HTTP request step goes through Orbit's `NetworkGuard`: public addresses only, checked at connect time.
- **Loop protection.** Flow's writes reach the apps' webhooks marked with its own key: `source.kind = "api"` with
  `keyId` in Orbit, and `via.apiKeyId` in Projects. Flow skips those events unless the flow opts in. A flow that
  starts more than `Flow:FlowRunsPerMinute` runs in a minute is paused.
- **Webhook trigger.** Each webhook flow gets an unguessable address, `/api/hooks/fhk_…`, and can have an
  optional HMAC secret. With a secret, callers send `X-Flow-Signature: sha256=hex(HMAC(secret, "{X-Flow-Timestamp}.{body}"))`
  and the timestamp must be within five minutes.

- **Failure alerts.** "Email me when this flow fails" on a flow, and a workspace default (email a flow's creator)
  for flows that don't choose. At most one email per flow per hour: the first failure at once, the rest of the
  hour's in one digest, with a link to the newest failed run (`App:BaseUrl`). Within the daily email cap.
- **Actions.** Orbit: create, update and find records, and add a comment to a record. Projects: create projects
  and tasks, find tasks, update a task (status, assignee, due date) and add a comment.

- **Outside apps** (db/004): Slack, Microsoft 365 and Google, connected with OAuth 2.0 per Flow workspace (owners
  and admins). One shared callback, `{App:ApiBaseUrl}/api/oauth/{provider}/callback`; the consent's state is sealed and
  spent once, and the studio claims the result as the same person, so a link someone else started can't attach your
  account to their workspace. Tokens are sealed (AES-256-GCM, bound to the row), refreshed before they expire, and
  revoked at the provider on disconnect where it can be (Slack, Google). A refresh the provider refuses ends the
  connection and pauses the flows that need it. Each provider stays hidden ("Not set up yet") until its settings exist.
  - **Slack**: send a channel message, send a direct message; "New message in channel" through the Events API
    (`POST /api/oauth/slack/events`, signed with `Connectors:Slack:SigningSecret`). Messages Flow's own app posted
    only start flows that opted in; thread replies and other bots' posts only when the trigger asks.
  - **Microsoft 365** (Graph, delegated): send an email as you, post to a Teams channel, create a calendar event;
    "New email in Outlook" polls the inbox with a delta link.
  - **Google**: send an email as you (Gmail); add, find and update a row in a Google Sheet; "New email in Gmail"
    (the mailbox's history) and "New row in a sheet" poll.
  - Polling runs on the worker's minute tick, a cursor per flow under a lease, every `Connectors:PollSeconds` (120),
    backing off on failures (doubling to an hour, and at least as long as a 429 asks). A saved or re-enabled flow
    starts from what arrives after.
- **Versions.** Every save keeps a copy (the newest 100 per flow): `GET …/flows/{id}/versions`, one by number, and
  restore, which saves it again as the newest. Runs record the version they ran.

Until db/001 has run, every endpoint except sign-in and `/api/health` answers 503 `not_ready`, and the worker
waits. Until db/002 has run, failure alerts are hidden, their endpoints answer 503 `not_ready`, and none are sent.
Until db/003 has run, saves keep no versions and the version endpoints answer 503 `not_ready`. Until db/004 has run,
the outside apps are hidden, their endpoints answer 503 `not_ready`, and nothing polls.

## Settings

Set these in App Service configuration, never in the repo:

| Setting | |
|---|---|
| `ConnectionStrings:Vantra` | the shared identity database (the same as Orbit's) |
| `ConnectionStrings:VantraFlow` | Flow's own database |
| `Jwt:Key` | the suite's shared signing key (32+ characters) |
| `App:BaseUrl` | the studio's origin, e.g. `https://flow.vantrasoftware.com` |
| `App:ApiBaseUrl` | this API's public origin: Orbit and Projects deliver events here |
| `Cors:AllowedOrigins:0` | the studio's origin |
| `Connectors:SecretKey` | base64 of 32 random bytes; seals connection keys and secrets (recommended) |
| `Connectors:SharedKey` | optional; signs the connect assertion (set the same value as `FlowIntegration:SharedKey` on Orbit and Projects) |
| `Connectors:Orbit:ApiUrl`, `Connectors:Orbit:AppUrl` | defaults to the live Orbit |
| `Connectors:Projects:ApiUrl`, `Connectors:Projects:AppUrl` | defaults to the live Projects |
| `Graph:*` or `Smtp:*` | the email sender (the same as Orbit's) |
| `Google:ClientId`, `Microsoft:ClientId` | social sign-in |
| `Connectors:Slack:ClientId`, `…:ClientSecret`, `…:SigningSecret` | the Slack connector |
| `Connectors:Microsoft:ClientId`, `…:ClientSecret`, `…:Tenant` (optional) | the Microsoft 365 connector |
| `Connectors:Google:ClientId`, `…:ClientSecret` | the Google connector |
| `Connectors:PollSeconds` | optional; how often polling triggers poll (default 120) |
| `Flow:MonthlyRuns`, `Flow:EmailsPerDay`, … | limits (see appsettings.json) |

Deploys run through `.github/workflows/main_vantraflow.yml` to the `vantraflow` App Service. It uses the
`AZUREAPPSERVICE_CLIENTID`, `AZUREAPPSERVICE_TENANTID` and `AZUREAPPSERVICE_SUBSCRIPTIONID` secrets. Run
`db/001_flow.sql`, then `db/002_failure_alerts.sql`, then `db/003_flow_versions.sql`, then `db/004_oauth_connectors.sql`, by hand against the `vantraflow` database, connected to it, each
as one batch.
