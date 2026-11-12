using System.Data;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Dapper;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using VantraFlow.Api.Controllers;
using VantraFlow.Api.Data;
using VantraFlow.Api.Services;
using VantraFlow.Api.Services.Auth;
using VantraFlow.Api.Services.Connectors;
using VantraFlow.Api.Services.Engine;
using VantraFlow.Api.Services.OAuth;
using VantraFlow.Api.Services.Workspaces;

var builder = WebApplication.CreateBuilder(args);

// Local-only overrides (connection strings, JWT key): gitignored, and loaded only in Development.
if (builder.Environment.IsDevelopment())
    builder.Configuration.AddJsonFile("appsettings.Development.local.json", optional: true, reloadOnChange: true);

builder.Services.AddControllers().AddJsonOptions(o =>
    o.JsonSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull);
builder.Services.AddOpenApi();

// Every Flow timestamp column is datetime2(3), as Orbit's.
SqlMapper.AddTypeMap(typeof(DateTime), DbType.DateTime2);
SqlMapper.AddTypeMap(typeof(DateTime?), DbType.DateTime2);

// ── Databases ────────────────────────────────────────────────────────────
// The shared Vantra identity DB (UserDetails, for sign-in) and Flow's own. Orbit and Projects are reached only
// through their APIs, over a workspace's connections.
var vantraConn = builder.Configuration.GetConnectionString("Vantra");
var flowConn = builder.Configuration.GetConnectionString("VantraFlow");
if (string.IsNullOrWhiteSpace(vantraConn) || string.IsNullOrWhiteSpace(flowConn))
    throw new InvalidOperationException(
        "ConnectionStrings:Vantra and ConnectionStrings:VantraFlow must be set " +
        "(appsettings.Development.local.json locally, App Service config in production).");
builder.Services.AddSingleton(new SqlConnectionFactory(vantraConn, flowConn));
builder.Services.AddSingleton<FlowSchema>();
// Shared Vantra workspaces, stage 1: the suite-wide record for a workspace, read from the identity DB (vantra.*,
// VantraProjectsAPI db/shared/001). Read only; answers nothing until those tables exist and hold a link.
builder.Services.AddSingleton<ISharedWorkspaceStore, SqlSharedWorkspaceStore>();
builder.Services.AddSingleton<SharedWorkspaces>();
// Stage 2: members, app access and invitations from the shared record (db/shared/002), Flow's own list mirrored.
builder.Services.AddSingleton<VantraFlow.Api.Shared.ISharedStore, VantraFlow.Api.Shared.SqlSharedStore>();
builder.Services.AddSingleton<VantraFlow.Api.Shared.SharedMembers>();
builder.Services.AddSingleton<VantraFlow.Api.Shared.ISharedMirror, VantraFlow.Api.Shared.FlowSharedMirror>();
builder.Services.AddSingleton<VantraFlow.Api.Shared.SharedSync>();
builder.Services.AddScoped<VantraFlow.Api.Shared.ISharedHost, VantraFlow.Api.Shared.FlowSharedHost>();
builder.Services.AddScoped<VantraFlow.Api.Shared.SharedTeam>();
// Shared Vantra workspaces, stage 3: one workspace everywhere (create, link on first sight, switch apps on and off).
builder.Services.AddSingleton<VantraFlow.Api.Shared.ISharedDirectory, VantraFlow.Api.Shared.SqlSharedDirectory>();
builder.Services.AddSingleton<VantraFlow.Api.Shared.SharedSpaces>();
builder.Services.AddScoped<VantraFlow.Api.Shared.FlowSpaces>();
builder.Services.AddScoped<FlowReadyFilter>();
builder.Services.AddScoped<UsersRepository>();
builder.Services.AddScoped<WorkspacesRepository>();
builder.Services.AddScoped<IWorkspaceStore>(sp => sp.GetRequiredService<WorkspacesRepository>());
builder.Services.AddSingleton<IWorkspaceFeatures>(sp => sp.GetRequiredService<FlowSchema>());
builder.Services.AddScoped<IUserDirectory>(sp => sp.GetRequiredService<UsersRepository>());
builder.Services.AddScoped<FlowsRepository>();
builder.Services.AddScoped<ConnectionsRepository>();
builder.Services.AddScoped<EngineRepository>();
builder.Services.AddScoped<AlertsRepository>();
builder.Services.AddScoped<IWorkspaceAlerts>(sp => sp.GetRequiredService<AlertsRepository>());
// Workspaces as a whole: invitations (db/005), their emails, and what deleting one undoes outside Flow.
builder.Services.AddScoped<IInviteStore, InvitesRepository>();
builder.Services.AddScoped<IWorkspaceMail, WorkspaceMail>();
builder.Services.AddScoped<IWorkspaceTeardown, WorkspaceTeardown>();
builder.Services.AddScoped<VersionsRepository>();
builder.Services.AddScoped<FailureAlerts>();

// Plans and billing (db/008): Flow's tiers and limits (Billing:*), Stripe (Stripe:*), and the plan in force.
builder.Services.AddSingleton(sp => VantraFlow.Api.Services.Billing.PlanCatalog.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<VantraFlow.Api.Services.Billing.StripeBilling>();
builder.Services.AddScoped<PlansRepository>();
builder.Services.AddScoped<VantraFlow.Api.Services.Billing.Plans>();

// ── Identity ─────────────────────────────────────────────────────────────
builder.Services.AddSingleton<JwtTokenService>();
// Two-step sign-in: Orbit's and Projects', asked server to server (Services/Auth/TwoFactorSignIn.cs).
builder.Services.AddSingleton<TwoFactorChallengeTokens>();
builder.Services.AddSingleton<ITwoFactorApps, AppTwoFactorApps>();
builder.Services.AddScoped<ITwoFactorGuards, TwoFactorGuards>();
builder.Services.AddScoped<TwoFactorSignIn>();
builder.Services.AddSingleton<GoogleTokenVerifier>();
builder.Services.AddSingleton<MicrosoftTokenVerifier>();
builder.Services.AddHttpClient(); // Google token exchange, Microsoft Graph

// ── Connectors and the engine ───────────────────────────────────────────
builder.Services.AddSingleton<FlowSecrets>();
builder.Services.AddSingleton(sp => FlowLimits.From(sp.GetRequiredService<IConfiguration>()));
builder.Services.AddSingleton<OrbitApi>();
builder.Services.AddSingleton<ProjectsApi>();
builder.Services.AddSingleton<AppConnectors>();
builder.Services.AddScoped<Connections>();
builder.Services.AddSingleton<EmailService>();
builder.Services.AddScoped<IStepActions, StepActions>();
builder.Services.AddScoped<SqlEngineStore>();
builder.Services.AddSingleton<FlowSignal>();
builder.Services.AddHostedService<FlowWorker>();
// Outside apps (Slack, Microsoft 365, Google): OAuth 2.0 per workspace, their actions and polling triggers (db/004).
builder.Services.AddScoped<OAuthRepository>();
builder.Services.AddSingleton<SlackProvider>();
builder.Services.AddSingleton<MicrosoftProvider>();
builder.Services.AddSingleton<GoogleProvider>();
builder.Services.AddSingleton<OAuthProviders>();
builder.Services.AddScoped<OAuthConnections>();
builder.Services.AddScoped<OAuthActions>();
builder.Services.AddScoped<IPollSource, OAuthPollSource>();
builder.Services.AddScoped<PollTriggers>();
// The providers' APIs: fixed public hosts, no redirects, no cookies.
builder.Services.AddHttpClient(OAuthProvider.ClientName, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(25);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VantraFlow/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// The apps' APIs: no redirects, no cookies.
builder.Services.AddHttpClient(AppApi.ClientName, c =>
    {
        c.Timeout = TimeSpan.FromSeconds(30);
        c.DefaultRequestHeaders.UserAgent.ParseAdd("VantraFlow/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false });
// The HTTP request step: every connection goes through NetworkGuard (public addresses only, checked at connect
// time so DNS rebinding can't slip past the up-front check), no proxy, no redirects, no cookies.
builder.Services.AddHttpClient(StepActions.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ConnectCallback = NetworkGuard.ConnectAsync,
    });

// ── Rate limits ──────────────────────────────────────────────────────────
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Sign-in: 20 a minute per address.
    o.AddPolicy(AuthController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
    // Inviting and adding people: 30 a minute per person (each workspace also has its daily cap).
    o.AddPolicy(MembersController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.User.FindFirst("sub")?.Value ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // Opening and accepting invitation links: 30 a minute per address.
    o.AddPolicy(InviteAcceptController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
    // Webhook triggers and app events: 300 a minute per address and path.
    o.AddPolicy(IncomingController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
        (ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown") + ctx.Request.Path,
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 300, Window = TimeSpan.FromMinutes(1) }));
});

// ── Auth ─────────────────────────────────────────────────────────────────
// The suite's signing key and issuer, so one Vantra account works across the apps; Flow's own audience.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be set and at least 32 characters (appsettings.Development.local.json locally, App Service config in production).");
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false; // keep raw claim names (sub, email, ...)
        options.TokenValidationParameters = FlowTokenValidation.Session(builder.Configuration);
    });
builder.Services.AddAuthorization();

// ── CORS ─────────────────────────────────────────────────────────────────
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? ["http://localhost:5195"];
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseDeveloperExceptionPage();
    app.MapOpenApi();
}
else
{
    // A JSON 500 through the normal pipeline, so CORS headers still apply; the message is our own, never an exception's.
    app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
    {
        var feature = ctx.Features.Get<IExceptionHandlerPathFeature>();
        ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("UnhandledException")
            .LogError(feature?.Error, "Unhandled exception for {Method} {Path}", ctx.Request.Method, feature?.Path);
        ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await ctx.Response.WriteAsJsonAsync(new { error = "Something went wrong on our side." });
    }));
}

app.UseRouting();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();
