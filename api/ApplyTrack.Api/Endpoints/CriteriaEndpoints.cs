// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Data;

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
        app.MapGet("/api/criteria", async (CriteriaRepo repo) =>
            Results.Ok(await repo.GetAsync()));

        app.MapPut("/api/criteria", async (JsonElement payload, CriteriaRepo repo) =>
        {
            var criteria = Criteria.FromJson(payload);
            await repo.UpsertAsync(criteria);
            return Results.Ok(criteria);
        });
    }
}
