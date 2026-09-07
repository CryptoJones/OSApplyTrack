// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Reflection;
using System.Text.Json;
using System.Threading.RateLimiting;
using ApplyTrack.Api;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Middleware;
using ApplyTrack.Api.Notifications;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Scrape;
using Microsoft.AspNetCore.HttpOverrides;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// Connection string is config-driven so the one build runs either way:
// local dev reads appsettings.json; a container overrides it with the
// ConnectionStrings__Postgres environment variable (set by docker-compose).
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "No 'Postgres' connection string configured (set ConnectionStrings:Postgres "
        + "or the ConnectionStrings__Postgres environment variable).");

// One pooled data source for the app's lifetime. Registered via a factory (not the
// pre-built-instance overload) so the DI container OWNS it and disposes it — and its
// connection pool — on shutdown. The instance overload would leak the pool, which the
// long-lived prod app tolerates but the test suite (many short-lived hosts) does not.
// Default a 30s command timeout unless the connection string already pins one.
var postgres = new NpgsqlConnectionStringBuilder(connectionString);
if (!postgres.ContainsKey("Command Timeout"))
{
    postgres.CommandTimeout = 30;
}
connectionString = postgres.ConnectionString;
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

// Per-request plumbing for the tenancy choke-point. A scoped connection (DI disposes
// it at request end; Dapper opens/closes it around each command) underpins the repos.
// The TenantContext is stamped by TenantMiddleware from the session cookie, and the
// per-tenant repos are built pre-scoped to it — endpoints never see a tenant_id or a
// raw connection.
builder.Services.AddScoped<IDbConnection>(sp =>
    new ResilientDbConnection(
        sp.GetRequiredService<NpgsqlDataSource>().CreateConnection(),
        sp.GetRequiredService<ILogger<ResilientDbConnection>>()));
builder.Services.AddScoped<TenantContext>();
builder.Services.AddScoped<UserRepo>();
builder.Services.AddScoped<SessionRepo>();
builder.Services.AddScoped<MagicTokenRepo>();
builder.Services.AddScoped(sp => new ApplicationRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new CriteriaRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new BlacklistRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new PollRequestRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
// Magic-link delivery. With an SMTP host configured (Email:Host), mail goes out
// over SMTP via MailKit — point it at any submission relay (local, your provider,
// or a hosted service). With no host set, links are logged to the console so auth
// works with zero email config. Deliverability to Gmail/Outlook needs a relay whose
// IP has PTR + SPF/DKIM/DMARC — direct-to-MX from a residential IP gets rejected.
var emailOptions = builder.Configuration.GetSection("Email").Get<EmailOptions>() ?? new EmailOptions();
builder.Services.AddSingleton(emailOptions);
if (emailOptions.IsConfigured)
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

// Materials engine (cover-letter drafting). The LLM endpoint is an OpenAI-compatible
// server chosen entirely by config — a free local model (Ollama/vLLM) or any hosted
// provider — so résumé data can stay on-prem and per-draft cost is the operator's
// dial. Instance defaults come from the `Llm` config section; a tenant may override
// them, and a tenant's own API key is encrypted at rest with the operator's master key.
var llmOptions = builder.Configuration.GetSection("Llm").Get<LlmOptions>() ?? new LlmOptions();
builder.Services.AddSingleton(llmOptions);
builder.Services.AddSingleton(new SecretProtector(
    builder.Configuration["Secrets:Key"] ?? builder.Configuration["APPLYTRACK_SECRETS_KEY"]));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<ILlmClient, OpenAiCompatibleLlmClient>();
builder.Services.AddSingleton<CoverLetterDrafter>();
builder.Services.AddScoped(sp => new ResumeRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new LlmSettingsRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId,
    sp.GetRequiredService<SecretProtector>(),
    sp.GetRequiredService<ILogger<LlmSettingsRepo>>()));
builder.Services.AddScoped(sp => new CoverLetterRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));

// The editor's Autofill button: server-side fetch of a job-posting URL (SSRF-guarded;
// its own pinned HttpClient, so not from the factory) + the JobPosting/OG parser.
builder.Services.AddSingleton<JobPageFetcher>();

// Agentic auto-apply. The per-tenant settings and audit trail are always served (the
// SPA's Settings · Agent tab and the on-demand verdict endpoint use them); the
// unattended worker itself is registered only when Agent__Enabled=true — the
// intended shape is a second container from this image with that set and no port.
var agentOptions = builder.Configuration.GetSection("Agent").Get<AgentOptions>() ?? new AgentOptions();
builder.Services.AddSingleton(agentOptions);
builder.Services.AddSingleton<StructuredCompleter>();
builder.Services.AddSingleton<FitJudge>();
builder.Services.AddSingleton<LeadEvaluator>();
builder.Services.AddScoped(sp => new AgentSettingsRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new AgentEventRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new AgentPacketRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
// Step 3: the packet. Greenhouse's public Job Board API is the one ATS form we can
// read without a browser or an employer key; host pinned here, no tenant URL.
builder.Services.AddHttpClient(GreenhouseBoard.ClientName, c =>
{
    c.BaseAddress = new Uri("https://boards-api.greenhouse.io/");
    c.Timeout = TimeSpan.FromSeconds(15);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("OSApplyTrack/1.17 (+https://github.com/CryptoJones/OSApplyTrack)");
});
builder.Services.AddSingleton<GreenhouseBoard>();
builder.Services.AddSingleton<AnswerDrafter>();
builder.Services.AddSingleton<PacketBuilder>();

// The "moo": per-tenant Telegram notification when a packet is ready to submit. The
// host is pinned in code (no tenant-controlled URL, so no SSRF guard). RemoveAllLoggers:
// the factory's default handler logs the request URI, which carries the bot token.
builder.Services.AddHttpClient(TelegramNotifier.ClientName, c =>
{
    c.BaseAddress = new Uri("https://api.telegram.org/");
    c.Timeout = TimeSpan.FromSeconds(15);
}).RemoveAllLoggers();
builder.Services.AddSingleton<INotifier, TelegramNotifier>();
builder.Services.AddSingleton<PacketReadyNotifier>();
builder.Services.AddScoped(sp => new NotificationSettingsRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId,
    sp.GetRequiredService<SecretProtector>(),
    sp.GetRequiredService<ILogger<NotificationSettingsRepo>>()));

// Step 4: the browser. Its own container (see docker/browser), reached over ws://;
// unset means no browser and the copy-and-open path. The submitter itself is
// harmless to register everywhere — it refuses to run unless configured.
var browserOptions = builder.Configuration.GetSection("Browser").Get<BrowserOptions>() ?? new BrowserOptions();
builder.Services.AddSingleton(browserOptions);
builder.Services.AddSingleton<BrowserSubmitter>();
builder.Services.AddSingleton<FormDiscoverer>();
builder.Services.AddScoped(sp => new SubmitRequestRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));
builder.Services.AddScoped(sp => new AgentEvidenceRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));

if (agentOptions.Enabled)
{
    var agentConnectionString = connectionString;
    builder.Services.AddSingleton(sp => new AgentWorker(
        agentConnectionString, agentOptions, sp.GetRequiredService<LlmOptions>(),
        sp.GetRequiredService<SecretProtector>(), sp.GetRequiredService<LeadEvaluator>(),
        sp.GetRequiredService<PacketBuilder>(), sp.GetRequiredService<PacketReadyNotifier>(),
        browserOptions, sp.GetRequiredService<BrowserSubmitter>(),
        sp.GetRequiredService<ILoggerFactory>()));
    builder.Services.AddHostedService(sp => sp.GetRequiredService<AgentWorker>());
}

// The JSON contract the SPA depends on: C# PascalCase <-> snake_case JSON
// (ContactEmail <-> contact_email), case-insensitive on the way in. The dictionary
// key policy is deliberately left unset so map keys — status names, lane names,
// source ids — pass through verbatim.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

// Per-IP throttles on the two abuse-prone unauthenticated/expensive routes:
// magic-link requests (email spam / user enumeration probing) and the on-demand
// poll (each one fans out to external job boards). Other routes are unmetered.
// UseForwardedHeaders middleware already populates RemoteIpAddress from
// X-Forwarded-For, so we read that instead of parsing the raw header.
static string ClientPartition(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
    options.AddPolicy("poll", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
    // Cover-letter drafting fans out to the LLM (cost + latency) — throttle harder.
    options.AddPolicy("draft", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(5) }));
    // A test notification is an outbound message to the tenant's own chat — small budget.
    options.AddPolicy("notify", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(5) }));
    // Each scrape is an outbound fetch of an arbitrary site — same budget as poll.
    options.AddPolicy("scrape", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Bring the schema up to date on startup — the cross-runtime contract both the
// .NET API and the Python poller share.
// MigrationTimeoutSeconds (default 60) — increase for slower databases / many
// migrations; the same timeout applies to every script the run executes.
var migrationTimeout = TimeSpan.FromSeconds(TimeoutConfiguration.PositiveTimeoutSeconds(
    builder.Configuration["MigrationTimeoutSeconds"], defaultValue: 60));
// Migrations:Mode=wait — for a container whose DB role cannot migrate (the agent
// worker under its least-privilege role): wait for the owner container to, instead.
if (string.Equals(builder.Configuration["Migrations:Mode"], "wait", StringComparison.OrdinalIgnoreCase))
    Migrator.WaitUntilCurrent(connectionString, TimeSpan.FromMinutes(5));
else
    Migrator.Upgrade(connectionString, migrationTimeout);

// Honor forwarded client/protocol data only from a known reverse proxy. ASP.NET
// Core's loopback defaults cover same-host Caddy/nginx/`tailscale serve`; container
// gateways and remote proxies must be named under ForwardedHeaders:KnownProxies or
// KnownNetworks. An untrusted direct caller's headers are ignored, so they cannot
// forge Request.IsHttps or rotate the per-IP rate-limit partition.
app.UseForwardedHeaders(ForwardedHeadersConfiguration.Create(builder.Configuration));

// Enforce request-body size limits on the two endpoints that accept large uploads,
// checked on Content-Length before the body is read, so it works under TestServer too.
app.UseWhen(ctx => ctx.Request.Method == HttpMethods.Post && ctx.Request.Path == "/api/scrape",
    branch => branch.Use(async (ctx, next) =>
    {
        if (ctx.Request.ContentLength > 64L * 1024)
        {
            ctx.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
            return;
        }
        await next();
    }));
app.UseWhen(ctx => ctx.Request.Method == HttpMethods.Post && ctx.Request.Path == "/api/account/import",
    branch => branch.Use(async (ctx, next) =>
    {
        if (ctx.Request.ContentLength > 10L * 1024 * 1024)
        {
            ctx.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
            return;
        }
        await next();
    }));
app.UseWhen(ctx => ctx.Request.Method == HttpMethods.Post && ctx.Request.Path == "/api/resume/upload",
    branch => branch.Use(async (ctx, next) =>
    {
        if (ctx.Request.ContentLength > ResumePdfImporter.MaxPdfBytes + 16L * 1024)
        {
            ctx.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
            return;
        }
        await next();
    }));

// Stamp CSP + the other hardening headers on every response. After UseForwardedHeaders
// so Request.IsHttps is accurate (HSTS only when actually behind HTTPS).
app.UseMiddleware<SecurityHeadersMiddleware>();

// Catch the domain exceptions first so every downstream handler can throw them and
// get the FastAPI-compatible {"detail": "..."} body + status the SPA expects.
app.UseMiddleware<ApiExceptionMiddleware>();

// Bound ordinary JSON mutations before Minimal API model binding. Account import
// and résumé PDF upload keep their separate, larger purpose-built limits.
app.UseMiddleware<JsonBodyLimitMiddleware>();

// Serve the vanilla-JS SPA verbatim from wwwroot (index.html as the default doc).
// Static files short-circuit before the tenancy middleware, so the shell loads
// without a session and the SPA's own login gate handles the 401s on /api.
app.UseDefaultFiles();
app.UseStaticFiles();

// The tenancy choke-point: resolve the session -> tenant, enforce auth on /api.
app.UseMiddleware<TenantMiddleware>();

// Enforce the per-route rate-limit policies declared above (RequireRateLimiting).
app.UseRateLimiter();

// The running build, read from the assembly rather than a hard-coded constant so it can
// only ever report what is actually deployed. InformationalVersion carries a "+<commit>"
// suffix on CI builds; the header shows the bare version, so trim it once here.
var buildVersion = Assembly.GetExecutingAssembly()
    .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
buildVersion = buildVersion.Split('+')[0];
if (buildVersion.Length == 0) buildVersion = "unknown";

// Liveness: cheap and static, so a transient DB blip never trips it into a restart loop.
// It also carries the build version. This is the one endpoint the SPA can read before
// login, which is what lets the header show a version on the sign-in screen too.
app.MapGet("/health", () => Results.Ok(new { status = "ok", version = buildVersion }));
// Readiness: actually touch Postgres so an orchestrator can gate traffic on a working
// DB. Deliberately a separate path from liveness — a DB hiccup should drain traffic,
// not kill the pod. 503 (not an exception) on failure so it reads as "not ready" cleanly.
app.MapGet("/health/ready", async (NpgsqlDataSource db, CancellationToken ct) =>
{
    try
    {
        await using var conn = await db.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready", database = "connected" });
    }
    catch
    {
        return Results.Json(
            new { status = "unavailable", database = "disconnected" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapAppsEndpoints();
app.MapBlacklistEndpoints();
app.MapCriteriaEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapMaterialsEndpoints();
app.MapScrapeEndpoints();
app.MapAgentEndpoints();
app.MapPacketEndpoints();
app.MapNotificationsEndpoints();
app.MapSubmitEndpoints();

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the app.
public partial class Program;
