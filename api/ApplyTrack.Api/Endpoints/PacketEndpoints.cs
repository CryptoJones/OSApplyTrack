// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Notifications;
using Microsoft.AspNetCore.Mvc;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The prepared packet behind an application: read it, edit its answers with the
/// same <c>?expected_version=</c> 409 contract applications use, prepare one on
/// demand, or discard it.
/// </summary>
public static class PacketEndpoints
{
    public static void MapPacketEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/apps/{name}/packet", async (string name, AgentPacketRepo packets) =>
            Results.Ok(await packets.GetAsync(name)
                ?? throw new AppNotFoundException($"no packet for '{name}'")));

        app.MapPut("/api/apps/{name}/packet", async (
            string name, JsonElement payload,
            [FromQuery(Name = "expected_version")] string? expectedVersion,
            AgentPacketRepo packets) =>
        {
            var answers = new Dictionary<string, string>();
            if (payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("answers", out var a) && a.ValueKind == JsonValueKind.Object)
            {
                InputLimits.Count("answers", a.EnumerateObject().Count(), 200);
                foreach (var p in a.EnumerateObject())
                    answers[p.Name] = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() ?? "" : "";
            }
            return Results.Ok(await packets.UpdateAnswersAsync(name, answers, expectedVersion));
        });

        // Prepare now: judge the lead (reusing a recorded verdict unless ?force=true),
        // and on `proceed` build the packet and park the application in `ready` — the
        // same path the worker takes, at a human's request.
        app.MapPost("/api/apps/{name}/packet/prepare", async (
            string name, bool? force,
            ApplicationRepo apps, AgentSettingsRepo agentSettings, AgentEventRepo events,
            ResumeRepo resumes, CriteriaRepo criteria, LlmSettingsRepo llm, LlmOptions instance,
            CoverLetterRepo letters, AgentPacketRepo packets, NotificationSettingsRepo notifications,
            UserRepo users, Auth.TenantContext tenant,
            LeadEvaluator evaluator, PacketBuilder builder, PacketReadyNotifier notifier,
            BrowserOptions browser, SubmitRequestRepo queue,
            CancellationToken ct) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var cfg = EffectiveLlmConfig.Resolve(instance, await llm.GetOverrideAsync());
            if (!cfg.IsConfigured)
                throw new AppValidationException(
                    "no LLM endpoint is configured — set one in Settings · AI (or the instance default)");

            var resume = await resumes.GetAsync();
            var crit = await criteria.GetAsync();
            var settings = await agentSettings.GetAsync();

            Verdict verdict;
            var recorded = force == true ? null : await events.LatestVerdictAsync(rec.Name);
            if (recorded is not null && recorded.Detail.TryGetProperty("decision", out var d))
            {
                verdict = new Verdict(
                    d.GetString() ?? Verdict.Skip,
                    recorded.Detail.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
                    recorded.Detail.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "",
                    [], [], recorded.Detail.TryGetProperty("model_consulted", out var m) && m.ValueKind == JsonValueKind.True);
            }
            else
            {
                verdict = await evaluator.EvaluateAsync(rec, resume, crit, settings, cfg, events, ct)
                    ?? throw new LlmUnavailableException("the model did not reach a verdict — see the agent log");
            }
            if (!verdict.IsProceed)
                throw new AppValidationException(
                    "the agent's verdict is skip — " + verdict.Rationale + " (re-run with force to override)");

            var (_, _, _, lettersEnabled) = await llm.GetViewAsync();
            var email = (await users.GetAsync(tenant.TenantId))?.Email ?? "";
            var inputs = new PacketInputs(
                resume, settings, email, await llm.GetCoverLetterSignatureAsync(), lettersEnabled, cfg);
            var packet = await builder.BuildAsync(rec, verdict, inputs,
                new PacketScope(apps, letters, packets, events), ct);
            // With a browser, the moo waits for the dry-run fill; without one, this is it.
            if (browser.IsConfigured && rec.Fields.Link.Length > 0)
                await queue.EnqueueAsync(rec.Name, dryRun: true);
            else
                await notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, ct);
            return Results.Ok(new { ok = true, packet });
        }).RequireRateLimiting("draft");

        app.MapDelete("/api/apps/{name}/packet", async (string name, AgentPacketRepo packets) =>
            await packets.DeleteAsync(name)
                ? Results.NoContent()
                : throw new AppNotFoundException($"no packet for '{name}'"));
    }
}
