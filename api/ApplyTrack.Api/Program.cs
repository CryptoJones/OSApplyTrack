// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
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

// One pooled data source for the app's lifetime; repos open a connection per request.
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));

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
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapAppsEndpoints();
app.MapBlacklistEndpoints();
app.MapCriteriaEndpoints();

app.Run();

// Exposed so the test project's WebApplicationFactory<Program> can boot the app.
public partial class Program;
