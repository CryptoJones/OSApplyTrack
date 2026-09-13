// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent;
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
    public const string NoBrowser =
        "no browser is available on this instance — no agent worker with one has checked in; use Copy answers and open the posting";

    public static void MapSubmitEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/apps/{name}/submit", async (
            string name, JsonElement? payload, BrowserAvailability browser,
            ApplicationRepo apps, AgentPacketRepo packets, AgentSettingsRepo settings,
            SubmitRequestRepo queue) =>
        {
            if (!await settings.IsAllowedAsync())
                throw new AppForbiddenException(AgentEndpoints.NotAllowed);
            // A browser here, or an agent worker with one that has checked in lately: the
            // shipped shape wires the browser to the agent container only, so this process's
            // own configuration says nothing about whether a click has somewhere to land.
            if (!await browser.IsAvailableAsync())
                throw new AppValidationException(NoBrowser);
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            if (string.IsNullOrWhiteSpace(rec.Fields.Link))
                throw new AppValidationException("this application has no posting link to submit to");
            var packet = await packets.GetAsync(rec.Name)
                ?? throw new AppValidationException("prepare the packet first");

            var agent = await settings.GetAsync();
            if (!AtsProvider.BrowserCanSubmit(packet.Provider, agent.LongTail))
                throw new AppValidationException(packet.Provider == AtsProvider.Workday
                    ? "Workday needs an account with the employer — apply via Copy answers and open the posting"
                    : "this ATS isn't one the agent knows — turn on the long tail in Settings · Agent, or apply via Copy answers and open the posting");

            // Dry run unless the caller says otherwise AND the tenant has turned dry-run off.
            var wantsReal = payload is { ValueKind: JsonValueKind.Object } p
                && p.TryGetProperty("dry_run", out var d) && d.ValueKind == JsonValueKind.False;
            var dryRun = !wantsReal || agent.DryRun;
            var blocking = packet.BlockingReview().Count();
            if (!dryRun && blocking > 0)
                throw new AppValidationException(
                    $"{blocking} required answer(s) still need you before this can be submitted");

            var queued = await queue.EnqueueAsync(rec.Name, dryRun);
            return Results.Json(new { queued, dry_run = dryRun, pending = true },
                statusCode: queued ? StatusCodes.Status202Accepted : StatusCodes.Status200OK);
        }).RequireRateLimiting("draft");

        // The security code the board emailed the candidate, for the run parked on it.
        app.MapPost("/api/apps/{name}/security-code", async (string name, JsonElement payload, SubmitRequestRepo queue, AgentSettingsRepo settings) =>
        {
            if (!await settings.IsAllowedAsync())
                throw new AppForbiddenException(AgentEndpoints.NotAllowed);
            var code = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                ? (c.GetString() ?? "").Trim() : "";
            if (code.Length is < 4 or > 16 || !code.All(char.IsLetterOrDigit))
                throw new AppValidationException("the code is 4–16 letters and digits, as the email shows it");
            if (!await queue.SetSecurityCodeAsync(name, code))
                throw new AppConflictException("no run is waiting for a code on this application — run Submit again and paste the code when the next email arrives");
            return Results.Json(new { accepted = true }, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting("draft");

        app.MapGet("/api/apps/{name}/submit", async (string name, SubmitRequestRepo queue) =>
        {
            var r = await queue.GetAsync(name);
            return Results.Ok(r is null
                ? new { pending = false, dry_run = (bool?)null, requested_at = (DateTimeOffset?)null, claimed_at = (DateTimeOffset?)null, done_at = (DateTimeOffset?)null }
                : new { pending = r.Pending, dry_run = (bool?)r.DryRun, requested_at = (DateTimeOffset?)r.RequestedAt, claimed_at = r.ClaimedAt, done_at = r.DoneAt });
        });

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
