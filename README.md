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
db/005_workspace_invites.sql workspaces: their colour and logo, and invitations (tokens kept hashed)
db/006_ai_insights.sql    the AI builder's monthly count, time-saved figures, daily run totals, Insights' indexes
db/010_reliability.sql    error handling: auto-pause per flow, the daily failure digest (on for new workspaces)
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
  visit makes one for you, and you can make more (`Workspaces:MaxOwned`, 20 each). Each workspace links to one Orbit
  workspace and one Projects workspace through its connections. Only owners and admins connect apps. Every
  workspace endpoint checks the caller's role on the server (`Services/Workspaces/WorkspaceRules.cs`, as Orbit's):
  - owners and admins rename it, set its time zone, colour and logo (a PNG, JPEG or WebP data URL, 100,000
    characters at most), invite people, change roles and remove people;
  - only an owner makes someone an owner, changes or removes an owner, hands the workspace over (they become an
    owner, you an admin) or deletes it; there is always at least one owner, so the last one can't leave;
  - inviting an address that has a Vantra account adds them at once and emails them a note. Anyone else gets an
    invitation (db/005): a link to `{App:BaseUrl}/invite/{token}`, emailed through the email service, good for 14
    days. The token is 32 random bytes; only its SHA-256 is stored, it travels in request bodies (never a URL) and
    is never logged, so a resend makes a new link. Opening the link signed out keeps it until the person signs in
    or signs up, then accepts it as that account (its email must be the invited address). Invitations can be
    resent (one a minute) or revoked. 100 invitations and adds a day per workspace, 30 a minute per person;
  - **deleting** a workspace (its name typed to confirm) switches its flows off, disconnects its Orbit and
    Projects connections in those apps (Flow's webhooks removed, its key revoked) and revokes its Slack and Google
    grants at the provider (best effort: an app that can't be reached is reported, not waited for), then deletes
    its runs, run logs and queued events, and, in one transaction, its flows and versions, connections, tokens,
    poll cursors, invitations, usage, members and the workspace. A delete that fails part way can be run again.
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
- **Plans and billing** (db/008). Flow is sold and billed on its own, per workspace (not per seat), by the month's
  runs: Free (100 runs, 3 active flows, core apps), Starter (2,000, unlimited flows), Pro (10,000, premium apps and
  steps, AI builder credits) and Business (50,000, priority support). The numbers are `Billing:*` settings; the
  amounts are Stripe prices (`Stripe:Prices:{key}`), and the studio's display copy is one file there.
  - **Premium** apps and steps — Slack, Microsoft 365, Google, HTTP requests, webhooks, sub-flows, paths and
    approvals (`Billing:PremiumApps`, `Billing:PremiumSteps`, plus a catalog entry's own `Premium` flag) — can be
    added on any plan but switched on only on Pro and Business.
  - **Enforced on the server.** At the month's quota new runs are logged as skipped; owners get an email at 80% and
    at 100%, once each a month. Switching a flow on checks the active-flow limit and premium steps (402 with `code`
    `plan_flows` or `plan_premium` and `upgradeTo`). A flow that's on can't be saved with premium steps its plan
    lacks. Replay counts as a run.
  - **Trial.** 14 days of Pro for a new workspace, starting when billing goes live: every workspace that existed
    before db/008 (and any made before Stripe is set up) stays on Pro until then, and its 14 days start the
    moment `Stripe:SecretKey` is set. Owners hear three days before a trial ends, with how many flows Free wouldn't
    cover.
  - **Downgrades** never delete anything. Every five minutes the worker switches off what a workspace's plan no
    longer covers — premium flows, then the least recently run over the active-flow limit — with the reason on each
    flow and one email to its owners. They stay off until switched on again (after an upgrade).
  - **Stripe**, as Vantra Projects: a customer per workspace; Checkout for one price (a running trial carries over);
    the billing portal under Flow's own configuration (switch between Flow's prices, cancel at period end, invoices
    and the card); and `POST /api/billing/webhook`, signature-verified, each event handled once, the subscription
    re-read from Stripe. It handles `checkout.session.completed`, `customer.subscription.created`, `.updated` and
    `.deleted`, and `invoice.payment_failed` (an email to owners, once per invoice). Everything Flow makes in Stripe
    is stamped `vantra_app=flow`; anything else on a shared Stripe account (Projects') is ignored. Owners and admins
    manage billing; everyone in the workspace sees the plan. Without `Stripe:SecretKey` the plan still shows, as
    "Not set up yet", and checkout answers 503 `billing_off`.
- **Versions.** Every save keeps a copy (the newest 100 per flow): `GET …/flows/{id}/versions`, one by number, and
  restore, which saves it again as the newest. Runs record the version they ran.

- **AI flow builder** (`Controllers/AiController.cs`, `Services/Ai`). People describe an automation in plain words
  (`POST api/w/{ws}/ai/draft { prompt, definition? }`) and get a draft to review: a name, a short explanation, the
  definition and the apps it needs. It's never saved or switched on there. The call is OpenAI chat completions, as
  Orbit's AI, with Structured Outputs (a strict JSON schema) and a 45-second timeout. The instructions summarise the
  catalog straight from `FlowCatalog` (so new triggers and actions are offered with no change), the step types, and the
  workspace's connected apps, Orbit tables and Projects projects. No key, token or secret is sent: with "Ask AI" in the
  builder the current flow goes too, its headers and secret-looking values replaced and put back from the flow after.
  The answer is held to the catalog (unknown apps, triggers, actions and step types dropped, ids made unique, size
  limits kept) and checked as a save is; what the request didn't say is left empty for the builder's "to fix" list.
  Each draft costs one of the plan's AI credits for the month (Pro 200, Business 1,000, Free and Starter none: 402
  `plan_ai` with the tier to upgrade to), held under `Ai:MonthlyCap` when that's set (before plans are in force, it's
  `Ai:MonthlyCap` or 200); a call that fails is refunded. Drafting is limited to 6 a minute per person and 20 a minute
  per workspace. Off ("Not set up yet") until
  `OpenAI:ApiKey` and `OpenAI:Model` are both set.
- **Insights** (`GET api/w/{ws}/insights?days=7|30|90&flowId=`): runs per day (succeeded and failed), the success rate
  against the period before, median and 95th-percentile duration, the busiest flows and those that fail most, the
  steps that fail most with their commonest errors, the time saved (steps run in succeeded runs × minutes per step, 2 by
  default, or a flow's own minutes per run; owners and admins set them, `PUT …/insights/time-saved`) and this month's
  runs against the plan's quota, read through `IRunLimits` alone. Added up in SQL. Once db/006 has run, the hourly prune adds
  the runs it deletes to their flow's day (`flow.RunDays`) in the same transaction, so the 90 days hold up after the
  runs are gone; durations and failing steps cover the runs still kept (`Flow:KeepRunsDays`).

Until db/001 has run, every endpoint except sign-in and `/api/health` answers 503 `not_ready`, and the worker
waits. Until db/002 has run, failure alerts are hidden, their endpoints answer 503 `not_ready`, and none are sent.
Until db/003 has run, saves keep no versions and the version endpoints answer 503 `not_ready`. Until db/004 has run,
the outside apps are hidden, their endpoints answer 503 `not_ready`, and nothing polls. Until db/005 has run, the
workspace colour, logo and invitations are hidden and their endpoints answer 503 `not_ready`; adding someone who
already has a Vantra account, renaming, leaving, handing over and deleting all work. Until db/006 has run, the AI builder answers
503 `not_ready`, Insights reads only the runs still kept and its time-saved settings answer 503 `not_ready`, and the
prune keeps no daily totals. Until db/008 has run, plans
aren't enforced (the server's run quota applies as before), Plan & billing reads as not set up, checkout answers 503
`not_ready`, and the Stripe webhook answers 503 so Stripe keeps its events and retries.

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
| `Stripe:SecretKey` | Stripe's secret key: billing goes live (and trials start) when it's set |
| `Stripe:WebhookSecret` | the signing secret of the webhook endpoint `{App:ApiBaseUrl}/api/billing/webhook` |
| `Stripe:Prices:starter_month`, `…:starter_year`, `…:pro_month`, `…:pro_year`, `…:business_month`, `…:business_year` | a Stripe price id each, with GBP, EUR, AUD and CAD currency options |
| `Billing:TrialDays`, `Billing:Tiers:{tier}:Runs`, `…:ActiveFlows`, `…:Premium`, `…:AiCredits`, `…:PrioritySupport`, `Billing:PremiumApps`, `Billing:PremiumSteps` | optional; the plans' numbers (see appsettings.json) |
| `Workspaces:MaxOwned` | optional; how many workspaces one person may own (default 20) |
| `OpenAI:ApiKey`, `OpenAI:Model` | the AI flow builder (both needed; a model with Structured Outputs) |
| `Ai:MonthlyCap` | optional; a ceiling on AI drafts per workspace per month, over the plan's credits (0 turns drafting off) |

Deploys run through `.github/workflows/main_vantraflow.yml` to the `vantraflow` App Service. It uses the
`AZUREAPPSERVICE_CLIENTID`, `AZUREAPPSERVICE_TENANTID` and `AZUREAPPSERVICE_SUBSCRIPTIONID` secrets. Run
`db/001_flow.sql`, then `db/002_failure_alerts.sql`, then `db/003_flow_versions.sql`, then `db/004_oauth_connectors.sql`, then `db/005_workspace_invites.sql`, then `db/006_ai_insights.sql`, then `db/007_steps_apps.sql`, then `db/008_plans.sql`, then `db/010_reliability.sql`, by hand against the `vantraflow` database, connected to it, each
as one batch.
