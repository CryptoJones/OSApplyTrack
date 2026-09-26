// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Promotes Ready packets whose latest evidence is a clean dry run to a real submission.
/// A dry run used to promote itself only if the tenant's dry-run switch was already off
/// the moment it finished; every packet filled while the switch was on stayed in Ready
/// for good — 23 of them on 2026-09-13, with an empty queue (#185). This is the one
/// rule for all three callers: the settings save that flips dry-run off, the worker's
/// judging pass (for a flip the api never saw), and the Ready lane's
/// <b>Submit all clean</b>. The bar is exactly the worker's own promotion bar: a
/// <c>dry_run</c> row with nothing unmapped and no error, no required answer still
/// waiting on the person, a provider the browser may drive, and a link to drive it at.
/// </summary>
public static class ReadyPromoter
{
    public static async Task<List<string>> PromoteCleanAsync(
        IDbConnection conn, long tenantId, SecretProtector protector, bool longTail, bool dryRun, bool linkedInEasy = false)
    {
        var evidence = new AgentEvidenceRepo(conn, tenantId, protector);
        var packets = new AgentPacketRepo(conn, tenantId, protector);
        var apps = new ApplicationRepo(conn, tenantId);
        var queue = new SubmitRequestRepo(conn, tenantId);
        var promoted = new List<string>();
        var clean = (await evidence.LatestPerReadyApplicationAsync())
            .Where(e => AgentEvidenceRepo.IsCleanDryRun(e.Kind, e.Detail))
            .Select(e => e.Name)
            .ToList();
        if (clean.Count == 0)
            return promoted;
        // Every agent pass runs this: one read each for the packets and the links, not two
        // per Ready application (#351).
        var gates = await packets.GatesAsync(clean);
        var links = await apps.BriefsAsync(clean);
        foreach (var name in clean)
        {
            if (!gates.TryGetValue(name, out var packet) || packet.Blocking > 0)
                continue;
            if (!AtsProvider.BrowserCanSubmit(packet.Provider, longTail, linkedInEasy))
                continue;
            if (!links.TryGetValue(name, out var rec) || rec.Link.Length == 0)
                continue;
            if (await queue.EnqueueAsync(name, dryRun))
                promoted.Add(name);
        }
        return promoted;
    }
}
