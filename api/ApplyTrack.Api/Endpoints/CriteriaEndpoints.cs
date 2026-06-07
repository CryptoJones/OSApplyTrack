// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The <c>/api/criteria</c> routes — GET returns the tenant's discovery criteria
/// (or defaults when none is saved), PUT normalizes the posted JSON (junk dropped,
/// score clamped) and returns the stored form. Both serialize to the exact
/// snake_case shape the SPA round-trips and the Python poller reads.
/// </summary>
public static class CriteriaEndpoints
{
    public static void MapCriteriaEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/criteria", async ([FromServices] NpgsqlDataSource db) =>
        {
            await using var conn = await db.OpenConnectionAsync();
            var repo = new CriteriaRepo(conn, Tenant.BootstrapId);
            return Results.Ok(await repo.GetAsync());
        });

        app.MapPut("/api/criteria", async (JsonElement payload, [FromServices] NpgsqlDataSource db) =>
        {
            var criteria = Criteria.FromJson(payload);
            await using var conn = await db.OpenConnectionAsync();
            var repo = new CriteriaRepo(conn, Tenant.BootstrapId);
            await repo.UpsertAsync(criteria);
            return Results.Ok(criteria);
        });
    }
}
