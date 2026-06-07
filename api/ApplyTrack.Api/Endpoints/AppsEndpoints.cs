// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The <c>/api/apps</c> + <c>/api/stats</c> routes, written against the SPA's
/// existing contract (same URLs, JSON shapes, and <c>?expected_version=</c> 409
/// flow as the Python FastAPI app). The <see cref="ApplicationRepo"/> arrives from DI
/// already scoped to the current tenant; <c>poll</c>, <c>check-link</c>, and
/// <c>draft</c> are not in v1 and answer 501 so the SPA shows a clean "not available"
/// toast instead of a crash.
/// </summary>
public static class AppsEndpoints
{
    /// <summary>Body of <c>PUT /api/apps/{name}/raw</c> — the full Markdown document.</summary>
    public sealed record RawUpdate(string Content);

    public static void MapAppsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/apps", async (ApplicationRepo repo) =>
            Results.Ok(await repo.ListAsync()));

        app.MapGet("/api/stats", async (ApplicationRepo repo) =>
        {
            var (status, lane) = await repo.StatsAsync();
            return Results.Ok(new { status, lane });
        });

        app.MapGet("/api/apps/{name}", async (string name, ApplicationRepo repo) =>
        {
            var rec = await repo.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            return Results.Ok(new
            {
                filename = rec.Name,
                raw = MarkdownCodec.Render(rec.Fields),
                fields = rec.Fields,
                version = rec.Version.ToString(),
                material = "",
            });
        });

        app.MapPost("/api/apps", async (AppFields payload, ApplicationRepo repo) =>
        {
            var filename = await repo.CreateAsync(payload);
            return Results.Json(new { filename }, statusCode: StatusCodes.Status201Created);
        });

        app.MapPut("/api/apps/{name}", async (
            string name, AppFields payload,
            [FromQuery(Name = "expected_version")] string? expectedVersion,
            ApplicationRepo repo) =>
        {
            var filename = await repo.UpdateStructuredAsync(name, payload, expectedVersion);
            return Results.Ok(new { filename });
        });

        app.MapPut("/api/apps/{name}/raw", async (
            string name, RawUpdate payload,
            [FromQuery(Name = "expected_version")] string? expectedVersion,
            ApplicationRepo repo) =>
        {
            var filename = await repo.UpdateRawAsync(name, payload.Content, expectedVersion);
            return Results.Ok(new { filename });
        });

        app.MapDelete("/api/apps/{name}", async (string name, ApplicationRepo repo) =>
        {
            await repo.DeleteAsync(name);
            return Results.NoContent();
        });

        // On-demand poll. The discovery poller is the decoupled Python worker, so
        // .NET can't run it inline; enqueue a request the worker drains out of band
        // (`applytrack poll --drain`) and answer {count:0} now. The SPA's live
        // refresh surfaces the new leads once the worker stages them.
        app.MapPost("/api/poll", async (PollRequestRepo polls) =>
        {
            await polls.EnqueueAsync();
            return Results.Ok(new { count = 0 });
        }).RequireRateLimiting("poll");

        // -- Not in v1 -----------------------------------------------------------
        // The link probe and LLM cover-letter engine are out of scope here; answer
        // 501 with a {detail} body the SPA can surface as a toast.
        app.MapGet("/api/apps/{name}/check-link",
            (string name) => NotImplemented("link checking is not available yet"));
        app.MapPost("/api/apps/{name}/draft",
            (string name) => NotImplemented("cover-letter drafting is not available in this version"));
    }

    private static IResult NotImplemented(string detail) =>
        Results.Json(new { detail }, statusCode: StatusCodes.Status501NotImplemented);
}
