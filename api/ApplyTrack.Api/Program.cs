// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

// Connection string is config-driven so the one build runs either way:
// local dev reads appsettings.json; a container overrides it with the
// ConnectionStrings__Postgres environment variable (set by docker-compose).
var connectionString = builder.Configuration.GetConnectionString("Postgres")
    ?? throw new InvalidOperationException(
        "No 'Postgres' connection string configured (set ConnectionStrings:Postgres "
        + "or the ConnectionStrings__Postgres environment variable).");

// Bring the schema up to date on startup; Step 1 adds the application tables.
Migrator.Upgrade(connectionString);

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
