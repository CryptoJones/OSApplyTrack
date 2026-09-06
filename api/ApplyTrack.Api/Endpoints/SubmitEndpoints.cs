// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The human's Submit click, and what the browser saw. <c>POST /api/apps/{name}/submit</c>
/// queues a browser run for the worker — a dry run (fill, screenshot, do not click)
/// or the real thing — and never touches an employer from the request thread.
/// <c>GET …/evidence</c> lists the screenshots and confirmations that came back.
/// </summary>
public static class SubmitEndpoints
{
    public static void MapSubmitEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/apps/{name}/submit", async (
            string name, JsonElement? payload, BrowserOptions browser,
            ApplicationRepo apps, AgentPacketRepo packets, AgentSettingsRepo settings,
            SubmitRequestRepo queue) =>
        {
            if (!browser.IsConfigured)
                throw new AppValidationException(
                    "browser submission isn't configured on this instance — use Copy answers and open the posting");
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            if (string.IsNullOrWhiteSpace(rec.Fields.Link))
                throw new AppValidationException("this application has no posting link to submit to");
            var packet = await packets.GetAsync(rec.Name)
                ?? throw new AppValidationException("prepare the packet first");

            // Dry run unless the caller says otherwise AND the tenant has turned dry-run off.
            var agent = await settings.GetAsync();
            var wantsReal = payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("dry_run", out var d) && d.ValueKind == JsonValueKind.False;
            var dryRun = !wantsReal || agent.DryRun;
            if (!dryRun && packet.NeedsReview.Count > 0)
                throw new AppValidationException(
                    $"{packet.NeedsReview.Count} answer(s) still need you before this can be submitted");

            var queued = await queue.EnqueueAsync(rec.Name, dryRun);
            return Results.Json(new { queued, dry_run = dryRun, pending = true },
                statusCode: queued ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).RequireRateLimiting("draft");

        app.MapGet("/api/apps/{name}/submit", async (string name, SubmitRequestRepo queue) =>
            Results.Ok(await queue.GetAsync(name) is { } r
                ? new { pending = r.Pending, dry_run = r.DryRun, requested_at = r.RequestedAt, claimed_at = r.ClaimedAt, done_at = r.DoneAt }
                : null));

        app.MapGet("/api/apps/{name}/evidence", async (string name, AgentEvidenceRepo evidence) =>
            Results.Ok(await evidence.ListAsync(name)));

        app.MapGet("/api/apps/{name}/evidence/{id:long}/screenshot.png", async (
            string name, long id, AgentEvidenceRepo evidence) =>
        {
            var bytes = await evidence.ScreenshotAsync(name, id)
                ?? throw new AppNotFoundException("no screenshot");
            return Results.File(bytes, "image/png");
        });
    }
}
