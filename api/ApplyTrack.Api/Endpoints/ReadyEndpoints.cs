// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The Ready lane worked in bulk (#186): <c>POST /api/ready/actions</c> takes one
/// action and many applications and queues the batch server-side, so the rate limit
/// applies to the request rather than to each application — every requeue on
/// 2026-09-13 was an <c>INSERT INTO submit_requests</c> by hand because the UI had one
/// button per packet and the <c>draft</c> limit trips after ten. <b>prepare</b> queues a
/// rebuild-then-dry-run for each (#183); <b>submit</b> queues the click (a dry run while
/// <i>Dry run only</i> is on), or with <c>all_clean</c> every Ready packet whose latest
/// dry run was clean (#185); <b>pass</b> retires them. Each name that could not be
/// queued comes back with its reason rather than failing the batch.
/// </summary>
public static class ReadyEndpoints
{
    private const int MaxNames = 200;

    public static void MapReadyEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/ready/actions", async (
            JsonElement payload, ApplicationRepo apps, AgentPacketRepo packets, AgentSettingsRepo agentSettings,
            SubmitRequestRepo queue, BrowserAvailability browser, IDbConnection conn,
            Auth.TenantContext tenant, SecretProtector protector) =>
        {
            var action = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String
                ? (a.GetString() ?? "").Trim().ToLowerInvariant() : "";
            if (action is not ("prepare" or "submit" or "pass"))
                throw new AppValidationException("action must be prepare, submit or pass");
            var allClean = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("all_clean", out var ac) && ac.ValueKind == JsonValueKind.True;
            var names = new List<string>();
            if (payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("names", out var ns) && ns.ValueKind == JsonValueKind.Array)
            {
                InputLimits.Count("names", ns.GetArrayLength(), MaxNames);
                foreach (var n in ns.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                        names.Add(Slug.Normalize(n.GetString()!));
            }
            names = names.Distinct(StringComparer.Ordinal).ToList();
            if (names.Count == 0 && !(action == "submit" && allClean))
                throw new AppValidationException("select at least one application");

            var done = new List<string>();
            var skipped = new List<object>();
            void Skip(string name, string reason) => skipped.Add(new { name, reason });

            // One read of the few columns the actions decide on, then set-based writes:
            // a 200-name batch is a handful of statements, not 600 round trips (#351).
            var briefs = await apps.BriefsAsync(names);

            if (action == "pass")
            {
                var moved = (await apps.SetStatusAsync(names, "passed")).ToHashSet(StringComparer.Ordinal);
                foreach (var name in names)
                {
                    if (moved.Contains(name)) done.Add(name);
                    else if (!briefs.ContainsKey(name)) Skip(name, "not found");
                    else Skip(name, "already passed");
                }
                return Results.Ok(new { action, done, skipped });
            }

            if (!await agentSettings.IsAllowedAsync())
                throw new AppForbiddenException(AgentEndpoints.NotAllowed);
            if (!await browser.IsAvailableAsync())
                throw new AppValidationException(SubmitEndpoints.NoBrowser);
            var settings = await agentSettings.GetAsync();

            if (action == "prepare")
            {
                foreach (var name in names)
                {
                    if (!briefs.TryGetValue(name, out var rec)) { Skip(name, "not found"); continue; }
                    if (rec.Link.Length == 0) { Skip(name, "no posting link"); continue; }
                    if (await queue.EnqueueAsync(rec.Name, dryRun: true, prepare: true)) done.Add(rec.Name);
                    else Skip(rec.Name, "already queued");
                }
                return Results.Json(new { action, dry_run = true, done, skipped }, statusCode: StatusCodes.Status202Accepted);
            }

            // submit
            var dryRun = settings.DryRun;
            if (allClean)
            {
                if (dryRun)
                    throw new AppValidationException(
                        "turn off Dry run only in Settings · Agent first — re-running a clean dry run as a dry run proves nothing new");
                done.AddRange(await ReadyPromoter.PromoteCleanAsync(conn, tenant.TenantId, protector, settings.LongTail, dryRun: false, settings.LinkedInEasy));
                return Results.Json(new { action, dry_run = false, done, skipped }, statusCode: StatusCodes.Status202Accepted);
            }
            var gates = await packets.GatesAsync(briefs.Keys);
            foreach (var name in names)
            {
                if (!briefs.TryGetValue(name, out var rec)) { Skip(name, "not found"); continue; }
                if (rec.Link.Length == 0) { Skip(name, "no posting link"); continue; }
                if (!gates.TryGetValue(rec.Name, out var packet)) { Skip(rec.Name, "no packet — prepare it first"); continue; }
                if (!AtsProvider.BrowserCanSubmit(packet.Provider, settings.LongTail, settings.LinkedInEasy))
                {
                    Skip(rec.Name, SubmitEndpoints.CannotDrive(packet.Provider));
                    continue;
                }
                var blocking = packet.Blocking;
                if (!dryRun && blocking > 0)
                {
                    Skip(rec.Name, $"{blocking} required answer(s) still need you");
                    continue;
                }
                if (await queue.EnqueueAsync(rec.Name, dryRun)) done.Add(rec.Name);
                else Skip(rec.Name, "already queued");
            }
            return Results.Json(new { action, dry_run = dryRun, done, skipped }, statusCode: StatusCodes.Status202Accepted);
        }).RequireRateLimiting("draft");
    }
}
