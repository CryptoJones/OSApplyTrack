// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using Dapper;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Un-sticks Ready. <see cref="ReadyPromoter"/> moves a packet on only when its newest
/// evidence is a clean dry run, so a packet whose last run <i>failed</i> was never looked
/// at again: the queue row was stamped done, nothing re-queued it, and it sat in Ready
/// until a person noticed — 33 of them on 2026-09-19, with an empty queue (#274). Two
/// states heal themselves here, on every pass:
/// <list type="bullet">
/// <item>a Ready application with <b>no packet at all</b> — nothing judges it (the pass
/// reads leads) and nothing promotes it (there is no evidence), so a prepare is queued;</item>
/// <item>a Ready application whose last run died on something <b>transient</b> — a browser
/// crash, a timeout, an Apply button that did not answer — which gets another dry run.
/// That run also retires a posting that has since closed, the way every run does.</item>
/// </list>
/// Everything else stays put on purpose. A captcha, a sign-in wall, a required field
/// nobody could map and a provider the browser may not drive are the person's, and a
/// submit whose confirmation was not recognised is <b>never</b> retried: the board may
/// have taken it, and a second application is worse than a parked one. Retries are
/// bounded — <see cref="MaxFailures"/> per <see cref="Window"/>, <see cref="RetryAfter"/>
/// apart — so a form that is simply broken stops costing runs.
/// </summary>
public static partial class ReadyReconciler
{
    /// <summary>Failed runs an application may spend inside <see cref="Window"/> before it is left for the person.</summary>
    public const int MaxFailures = 3;
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);
    /// <summary>A failed run is not retried sooner than this: a board that is down stays down for a while.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(6);

    public sealed record Result(List<string> Prepared, List<string> Retried)
    {
        public int Count => Prepared.Count + Retried.Count;
    }

    private sealed record Candidate(string Name, string Link, string Source, bool HasPacket, int Failures, int PrepareErrors);

    public static async Task<Result> ReconcileAsync(IDbConnection conn, long tenantId, SecretProtector protector, bool longTail)
    {
        var result = new Result([], []);
        // Ready, with a link to drive, and nothing already in flight.
        var candidates = (await conn.QueryAsync<Candidate>(
            """
            SELECT a.name, a.link, a.source,
                   EXISTS (SELECT 1 FROM agent_packets p
                           WHERE p.tenant_id = a.tenant_id AND p.application_name = a.name) AS haspacket,
                   (SELECT count(*)::int FROM agent_evidence e
                    WHERE e.tenant_id = a.tenant_id AND e.application_name = a.name
                      AND e.kind = 'failed' AND e.created_at > now() - @window) AS failures,
                   (SELECT count(*)::int FROM agent_events v
                    WHERE v.tenant_id = a.tenant_id AND v.application_name = a.name
                      AND v.kind = 'error' AND v.created_at > now() - @window) AS prepareerrors
            FROM applications a
            WHERE a.tenant_id = @t AND a.status = 'ready' AND a.link <> ''
              AND NOT EXISTS (SELECT 1 FROM submit_requests r
                              WHERE r.tenant_id = a.tenant_id AND r.application_name = a.name
                                AND r.done_at IS NULL)
            ORDER BY a.name
            """,
            new { t = tenantId, window = Window })).ToList();
        if (candidates.Count == 0)
            return result;

        var latest = await new AgentEvidenceRepo(conn, tenantId, protector).LatestPerApplicationAsync(candidates.Select(c => c.Name));
        var queue = new SubmitRequestRepo(conn, tenantId);
        var events = new AgentEventRepo(conn, tenantId);
        foreach (var c in candidates)
        {
            if (!AtsProvider.BrowserCanSubmit(AtsProvider.Detect(c.Link, c.Source), longTail))
                continue;
            if (!c.HasPacket)
            {
                // A build that keeps failing is recorded as an error each time; stop at the cap.
                if (c.PrepareErrors >= MaxFailures)
                    continue;
                if (await queue.EnqueueAsync(c.Name, dryRun: true, prepare: true))
                {
                    result.Prepared.Add(c.Name);
                    await events.RecordAsync(AgentEventRepo.Kinds.Requeued, c.Name,
                        new { reason = "ready with no packet — prepare queued" });
                }
                continue;
            }
            if (!latest.TryGetValue(c.Name, out var last) || !IsTransientFailure(last.Kind, last.Detail))
                continue;
            if (c.Failures >= MaxFailures || DateTimeOffset.UtcNow - last.CreatedAt < RetryAfter)
                continue;
            if (await queue.EnqueueAsync(c.Name, dryRun: true))
            {
                result.Retried.Add(c.Name);
                await events.RecordAsync(AgentEventRepo.Kinds.Requeued, c.Name,
                    new { reason = "last run failed on a transient error — dry run queued", attempt = c.Failures + 1 });
            }
        }
        return result;
    }

    /// <summary>
    /// Whether a failed run is worth another try. New evidence says so itself
    /// (<c>transient</c>, written where the failure is caught); evidence from before that
    /// is read by its wording — an exception's type name leading the reason, or one of the
    /// submitter's own timed-out waits. A captcha never is, and neither is a real run or a
    /// submit that was clicked: either may have gone through.
    /// </summary>
    public static bool IsTransientFailure(string kind, JsonElement detail)
    {
        if (kind != AgentEvidenceRepo.Kinds.Failed || detail.ValueKind != JsonValueKind.Object)
            return false;
        if (detail.TryGetProperty("captcha", out var captcha) && captcha.ValueKind == JsonValueKind.True)
            return false;
        // A real run that died may have died after the click. Only a dry run is safe to repeat.
        if (detail.TryGetProperty("dry_run", out var dry) && dry.ValueKind == JsonValueKind.False)
            return false;
        if (detail.TryGetProperty("transient", out var flag) && flag.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return flag.ValueKind == JsonValueKind.True;
        var why = Text(detail, "reason") + " " + Text(detail, "error");
        if (why.Contains("Submit was clicked", StringComparison.Ordinal))
            return false;
        return ExceptionLed().IsMatch(why.TrimStart())
            || why.Contains("did not respond within", StringComparison.Ordinal)
            || why.Contains("no form appeared within", StringComparison.Ordinal);
    }

    private static string Text(JsonElement detail, string name) =>
        detail.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // "TimeoutException: Timeout 20000ms exceeded." — the shape RunSubmitAsync gives a crash.
    [GeneratedRegex(@"^[A-Za-z0-9_.]*Exception: ")]
    private static partial Regex ExceptionLed();
}
