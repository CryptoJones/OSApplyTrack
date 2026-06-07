// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Middleware;
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
builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();

// The JSON contract the SPA depends on: C# PascalCase <-> snake_case JSON
// (ContactEmail <-> contact_email), case-insensitive on the way in. The dictionary
// key policy is deliberately left unset so map keys — status names, lane names,
// source ids — pass through verbatim.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});

var app = builder.Build();

// Bring the schema up to date on startup — the cross-runtime contract both the
// .NET API and the Python poller share.
Migrator.Upgrade(connectionString);

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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAppsEndpoints();
app.MapBlacklistEndpoints();
app.MapCriteriaEndpoints();
app.MapAuthEndpoints();

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the app.
public partial class Program;
