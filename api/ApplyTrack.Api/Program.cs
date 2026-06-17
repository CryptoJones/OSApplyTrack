// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using System.Threading.RateLimiting;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Middleware;
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

// One pooled data source for the app's lifetime.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

// Per-request plumbing for the tenancy choke-point. A scoped connection (DI disposes
// it at request end; Dapper opens/closes it around each command) underpins the repos.
// The TenantContext is stamped by TenantMiddleware from the session cookie, and the
// per-tenant repos are built pre-scoped to it — endpoints never see a tenant_id or a
// raw connection.
builder.Services.AddScoped<IDbConnection>(sp =>
    sp.GetRequiredService<NpgsqlDataSource>().CreateConnection());
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
    sp.GetRequiredService<SecretProtector>()));
builder.Services.AddScoped(sp => new CoverLetterRepo(
    sp.GetRequiredService<IDbConnection>(), sp.GetRequiredService<TenantContext>().TenantId));

// The editor's Autofill button: server-side fetch of a job-posting URL (SSRF-guarded;
// its own pinned HttpClient, so not from the factory) + the JobPosting/OG parser.
builder.Services.AddSingleton<JobPageFetcher>();

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
    // Each scrape is an outbound fetch of an arbitrary site — same budget as poll.
    options.AddPolicy("scrape", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(ctx),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 15, Window = TimeSpan.FromMinutes(1) }));
});

var app = builder.Build();

// Bring the schema up to date on startup — the cross-runtime contract both the
// .NET API and the Python poller share.
Migrator.Upgrade(connectionString);

// Behind a TLS-terminating reverse proxy (Caddy/nginx/`tailscale serve` — the usual
// self-host front), honor X-Forwarded-Proto so Request.IsHttps is true and the session
// cookie keeps its Secure flag. The app is meant to sit behind that proxy, so forwarded
// headers are accepted from any hop — don't expose Kestrel directly to the internet.
// Plain-HTTP local/dev sends no such header, so this is a no-op there.
var forwarded = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor,
};
forwarded.KnownIPNetworks.Clear();
forwarded.KnownProxies.Clear();
app.UseForwardedHeaders(forwarded);

// Stamp CSP + the other hardening headers on every response. After UseForwardedHeaders
// so Request.IsHttps is accurate (HSTS only when actually behind HTTPS).
app.UseMiddleware<SecurityHeadersMiddleware>();

// Catch the domain exceptions first so every downstream handler can throw them and
// get the FastAPI-compatible {"detail": "..."} body + status the SPA expects.
app.UseMiddleware<ApiExceptionMiddleware>();

// Serve the vanilla-JS SPA verbatim from wwwroot (index.html as the default doc).
// Static files short-circuit before the tenancy middleware, so the shell loads
// without a session and the SPA's own login gate handles the 401s on /api.
app.UseDefaultFiles();
app.UseStaticFiles();

// The tenancy choke-point: resolve the session -> tenant, enforce auth on /api.
app.UseMiddleware<TenantMiddleware>();

// Enforce the per-route rate-limit policies declared above (RequireRateLimiting).
app.UseRateLimiter();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapGet("/health/ready", async (NpgsqlDataSource dataSource, CancellationToken ct) =>
{
    try
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1";
        await cmd.ExecuteScalarAsync(ct);
        return Results.Ok(new { status = "ready", database = "connected" });
    }
    catch (Exception)
    {
        return Results.Json(new { status = "unavailable", database = "disconnected" }, statusCode: 503);
    }
});
app.MapAppsEndpoints();
app.MapBlacklistEndpoints();
app.MapCriteriaEndpoints();
app.MapAuthEndpoints();
app.MapAccountEndpoints();
app.MapMaterialsEndpoints();
app.MapScrapeEndpoints();

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the app.
public partial class Program;
