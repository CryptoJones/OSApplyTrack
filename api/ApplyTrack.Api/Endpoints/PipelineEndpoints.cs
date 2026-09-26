// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// <c>GET /api/pipeline</c>: the submit queue as the worker will drain it, and what each
/// request will turn into when it does. The Pipeline strip's label opens this in the SPA.
/// Every row is a pending <c>submit_requests</c> row for this tenant, oldest first (the
/// worker's claim order), with the same gates the worker applies in
/// <c>AgentWorker.RunSubmitAsync</c> evaluated ahead of time: the tenant's dry-run switch,
/// the request's own dry-run flag, the packet's blocking review items, and whether the
/// browser may drive the posting's ATS. A second list carries the Ready packets that are
/// not queued but would be promoted by <i>Submit all clean</i> or a dry-run flip.
/// </summary>
public static class PipelineEndpoints
{
    /// <summary>What the worker will do with a queued request when it claims it.</summary>
    public static class Will
    {
        /// <summary>Click Submit for real.</summary>
        public const string Submit = "submit";
        /// <summary>Fill and screenshot, no click.</summary>
        public const string DryRun = "dry_run";
        /// <summary>Rebuild the packet with form discovery, then a dry run.</summary>
        public const string Prepare = "prepare";
        /// <summary>Nothing — the run will be dropped with an error event.</summary>
        public const string Drop = "drop";
    }

    /// <summary>Where a queued request is right now.</summary>
    public static class Phase
    {
        public const string Queued = "queued";
        public const string Running = "running";
        public const string AwaitingCode = "awaiting_code";
        /// <summary>Claimed more than fifteen minutes ago and never finished — the next pass reclaims it.</summary>
        public const string Stale = "stale";
    }

    /// <summary>One queued request, as the SPA lists it.</summary>
    public sealed record QueueRow(
        int Position, string Name, string Company, string Role, string Status, string Link, string Provider,
        DateTimeOffset RequestedAt, DateTimeOffset? ClaimedAt, string Phase, bool CodeReceived,
        string Will, bool ThenSubmit, string Reason, int BlockingReview, string LastEvidence, DateTimeOffset? LastEvidenceAt);

    /// <summary>One Ready packet that is not queued, and what is holding it.</summary>
    public sealed record ReadyRow(
        string Name, string Company, string Role, string Link, string Provider, string LastEvidence,
        bool Clean, bool Promotable, int BlockingReview, string Holding, DateTimeOffset? LastAt = null);

    public static void MapPipelineEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/pipeline", async (
            SubmitRequestRepo queue, AgentPacketRepo packets, AgentEvidenceRepo evidence, AgentSettingsRepo agentSettings,
            ApplicationRepo apps, AgentOptions options, BrowserAvailability browser, IDbConnection conn) =>
        {
            var settings = await agentSettings.GetAsync();
            var allowed = await agentSettings.IsAllowedAsync();
            var pending = await queue.PendingAsync();
            var latest = await evidence.LatestPerApplicationAsync(pending.Select(p => p.ApplicationName));
            var gates = await packets.GatesAsync(pending.Select(p => p.ApplicationName));
            var now = DateTimeOffset.UtcNow;

            var rows = new List<QueueRow>(pending.Count);
            var position = 0;
            foreach (var p in pending)
            {
                position++;
                latest.TryGetValue(p.ApplicationName, out var last);
                var packet = gates.GetValueOrDefault(p.ApplicationName);
                var blocking = packet?.Blocking ?? 0;
                // With the source: it alone tells a LinkedIn Easy Apply posting from a LinkedIn listing (#278).
                var provider = AtsProvider.Detect(p.Link, p.Source);
                var canDrive = p.Link.Length > 0 && AtsProvider.BrowserCanSubmit(provider, settings.LongTail, settings.LinkedInEasy);

                // The claim is fresh for fifteen minutes; after that ClaimNextAsync hands the
                // row out again, so an older claim is a run that died, not one in progress.
                var phase = Phase.Queued;
                if (p.ClaimedAt is { } claimed)
                {
                    phase = now - claimed > TimeSpan.FromMinutes(15) ? Phase.Stale : Phase.Running;
                    if (phase == Phase.Running && last.Kind == AgentEvidenceRepo.Kinds.AwaitingCode && last.CreatedAt >= claimed)
                        phase = Phase.AwaitingCode;
                }

                // The worker's own order of decisions, evaluated ahead of time.
                string will;
                string reason;
                var thenSubmit = false;
                if (p.Prepare)
                {
                    will = Will.Prepare;
                    reason = canDrive
                        ? "rebuild the packet where the browser is, then a dry run"
                        : "rebuild the packet; the browser cannot drive this ATS, so it is yours by Copy answers and open";
                }
                else if (packet is null)
                {
                    will = Will.Drop;
                    reason = "no packet — prepare it first";
                }
                else if (!canDrive)
                {
                    will = Will.Drop;
                    reason = p.Link.Length == 0 ? "no posting link" : SubmitEndpoints.CannotDrive(provider);
                }
                else if (settings.DryRun)
                {
                    will = Will.DryRun;
                    reason = "Dry run only is on in Settings · Agent";
                }
                else if (blocking > 0)
                {
                    will = Will.DryRun;
                    reason = $"{blocking} required answer{(blocking == 1 ? "" : "s")} still need{(blocking == 1 ? "s" : "")} you";
                }
                else if (p.DryRun)
                {
                    will = Will.DryRun;
                    reason = "requested as a dry run; a clean run queues the real click next";
                    thenSubmit = true;
                }
                else
                {
                    will = Will.Submit;
                    reason = "";
                }

                rows.Add(new QueueRow(position, p.ApplicationName,
                    p.Company.Length > 0 ? p.Company : Slug.NameStem(p.ApplicationName), p.Role, p.Status, p.Link, provider,
                    p.RequestedAt, p.ClaimedAt, phase, p.CodeReceived, will, thenSubmit, reason, blocking,
                    last.Kind ?? "", last.Kind is null ? null : last.CreatedAt));
            }

            // Pipeline only surfaces actively queued submit requests (the submit queue).
            // Ready applications are strictly managed under Ready to avoid cross-stage duplication.
            var ready = Array.Empty<ReadyRow>();

            return Results.Ok(new
            {
                allowed,
                enabled = settings.Enabled,
                dry_run = settings.DryRun,
                long_tail = settings.LongTail,
                worker_running = options.Enabled || await AgentWorkerRegistry.WorkerSeenAsync(conn),
                worker_last_seen = await AgentWorkerRegistry.LastSeenAsync(conn),
                browser_available = await browser.IsAvailableAsync(),
                // The global JSON policy snake-cases the record members (position, requested_at, …).
                queue = rows,
                ready,
                summary = new
                {
                    queued = rows.Count,
                    will_submit = rows.Count(r => r.Will == Will.Submit),
                    dry_runs = rows.Count(r => r.Will == Will.DryRun),
                    prepares = rows.Count(r => r.Will == Will.Prepare),
                    drops = rows.Count(r => r.Will == Will.Drop),
                    awaiting_code = rows.Count(r => r.Phase == Phase.AwaitingCode),
                    promotable = 0,
                },
            });
        });
    }
}
