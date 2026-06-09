// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using Microsoft.AspNetCore.Mvc;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The <c>/api/apps</c> + <c>/api/stats</c> routes, written against the SPA's
/// existing contract (same URLs, JSON shapes, and <c>?expected_version=</c> 409
/// flow as the Python FastAPI app). The <see cref="ApplicationRepo"/> arrives from DI
/// already scoped to the current tenant. <c>draft</c> generates a cover letter via the
/// configured LLM (the result surfaces as <c>material</c> on the app-detail GET);
/// <c>check-link</c> is still out of v1 and answers 501.
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

        app.MapGet("/api/apps/{name}", async (string name, ApplicationRepo repo, CoverLetterRepo letters) =>
        {
            var rec = await repo.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            return Results.Ok(new
            {
                filename = rec.Name,
                raw = MarkdownCodec.Render(rec.Fields),
                fields = rec.Fields,
                version = rec.Version.ToString(),
                material = await letters.GetBodyAsync(rec.Name) ?? "",
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

        // Draft a tailored cover letter for the application through the configured
        // (instance-default or per-tenant) LLM, then persist it — overwrite-by-app, so
        // re-drafting replaces. The body also surfaces as `material` on the next
        // GET /api/apps/{name}. Rate-limited: each call is an expensive upstream request.
        app.MapPost("/api/apps/{name}/draft", async (
            string name,
            ApplicationRepo apps, ResumeRepo resumes, LlmSettingsRepo llm,
            LlmOptions instance, CoverLetterDrafter drafter, CoverLetterRepo letters,
            CancellationToken ct) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var resume = await resumes.GetAsync();
            var cfg = EffectiveLlmConfig.Resolve(instance, await llm.GetOverrideAsync());
            var body = await drafter.DraftAsync(rec.Fields, resume, cfg, ct);
            await letters.UpsertAsync(rec.Name, body, cfg.Model);
            return Results.Ok(new { ok = true, material = body });
        }).RequireRateLimiting("draft");

        // -- Not in v1 -----------------------------------------------------------
        // The link probe is still out of scope here; answer 501 with a {detail} body
        // the SPA can surface as a toast.
        app.MapGet("/api/apps/{name}/check-link",
            (string name) => NotImplemented("link checking is not available yet"));
    }

    private static IResult NotImplemented(string detail) =>
        Results.Json(new { detail }, statusCode: StatusCodes.Status501NotImplemented);
}
