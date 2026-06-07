// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The <c>/api/blacklist</c> routes — add/remove a company and, on add, flip its
/// open leads to "passed" (heir to the Python <c>_pass_open_for</c>). The response
/// echoes the raw company text the caller sent, while storage/matching use the
/// normalized key, matching the FastAPI contract the SPA expects.
/// </summary>
public static class BlacklistEndpoints
{
    public sealed record BlacklistAdd(string Company);

    public static void MapBlacklistEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/blacklist", async ([FromServices] NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var repo = new BlacklistRepo(conn, Tenant.BootstrapId);
            return Results.Ok(await repo.ListAsync());
        });

        app.MapPost("/api/blacklist", async (BlacklistAdd payload, [FromServices] NpgsqlDataSource db) =>
        {
            var company = (payload.Company ?? "").Trim();
            if (company.Length == 0)
                throw new AppValidationException("company is required");
            await using var conn = await db.OpenConnectionAsync();
            var bl = new BlacklistRepo(conn, Tenant.BootstrapId);
            var added = await bl.AddAsync(company);
            var passed = await bl.PassOpenLeadsAsync(company);
            return Results.Ok(new { company, added, passed });
        });

        app.MapPost("/api/apps/{name}/blacklist", async (string name, [FromServices] NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var apps = new ApplicationRepo(conn, Tenant.BootstrapId);
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var company = rec.Fields.Company.Trim();
            if (company.Length == 0)
                throw new AppValidationException("application has no company to blacklist");
            var bl = new BlacklistRepo(conn, Tenant.BootstrapId);
            var added = await bl.AddAsync(company);
            var passed = await bl.PassOpenLeadsAsync(company);
            return Results.Ok(new { company, added, passed });
        });

        app.MapDelete("/api/blacklist/{company}", async (string company, [FromServices] NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var repo = new BlacklistRepo(conn, Tenant.BootstrapId);
            var removed = await repo.RemoveAsync(company);
            return Results.Ok(new { company, removed });
        });
    }
}
