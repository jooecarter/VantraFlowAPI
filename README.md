# VantraFlowAPI

The API behind **Vantra Flow**, the Vantra suite's automation studio. Flow connects Vantra Orbit, Vantra Projects
and the web: when something happens in one app, the steps that follow run in another. The studio (SPA) is
[vantra-flow](../vantra-flow).

ASP.NET Core (.NET 10), Dapper, Azure SQL. It is laid out like VantraOrbitAPI:

```
src/VantraFlow.Api        the API
tests/VantraFlow.Api.Tests xunit (no database needed)
db/001_flow.sql           the schema: one idempotent, Azure-ready batch
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

Until db/001 has run, every endpoint except sign-in and `/api/health` answers 503 `not_ready`, and the worker
waits.

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
| `Flow:MonthlyRuns`, `Flow:EmailsPerDay`, … | limits (see appsettings.json) |

Deploys run through `.github/workflows/main_vantraflow.yml` to the `vantraflow` App Service. It uses the
`AZUREAPPSERVICE_CLIENTID`, `AZUREAPPSERVICE_TENANTID` and `AZUREAPPSERVICE_SUBSCRIPTIONID` secrets. Run
`db/001_flow.sql` by hand against the `vantraflow` database, connected to it, as one batch.
