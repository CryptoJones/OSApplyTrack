// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The agent's settings, its audit trail, and an on-demand evaluation of one lead.
/// <c>/api/agent-settings</c> round-trips <see cref="AgentSettings"/>;
/// <c>/api/agent-events</c> lists what the agent did, newest first; and
/// <c>POST /api/apps/{name}/verdict</c> runs the same evaluation the worker would,
/// right now, for the lead the human is looking at.
/// </summary>
public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/agent-settings", async (AgentSettingsRepo repo, AgentOptions options, BrowserOptions browser) =>
            Results.Ok(View(await repo.GetAsync(), options, browser)));

        app.MapPut("/api/agent-settings", async (
            JsonElement payload, AgentSettingsRepo repo, AgentOptions options, BrowserOptions browser) =>
        {
            var settings = AgentSettings.FromJson(payload);
            await repo.UpsertAsync(settings);
            return Results.Ok(View(await repo.GetAsync(), options, browser));
        });

        app.MapGet("/api/agent-events", async (AgentEventRepo repo, int? limit) =>
            Results.Ok(await repo.ListRecentAsync(limit ?? 50)));

        app.MapPost("/api/apps/{name}/verdict", async (
            string name, ApplicationRepo apps, AgentSettingsRepo agentSettings,
            AgentEventRepo events, ResumeRepo resumes, CriteriaRepo criteria,
            LlmSettingsRepo llm, LlmOptions instance, LeadEvaluator evaluator,
            CancellationToken ct) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var cfg = EffectiveLlmConfig.Resolve(instance, await llm.GetOverrideAsync());
            if (!cfg.IsConfigured)
                throw new AppValidationException(
                    "no LLM endpoint is configured — set one in Settings · AI (or the instance default)");

            var verdict = await evaluator.EvaluateAsync(
                rec, await resumes.GetAsync(), await criteria.GetAsync(),
                await agentSettings.GetAsync(), cfg, events, ct)
                ?? throw new LlmUnavailableException("the model did not reach a verdict — see the agent log");
            return Results.Ok(new { ok = true, verdict });
        }).RequireRateLimiting("draft");
    }

    private static object View(AgentSettings s, AgentOptions options, BrowserOptions browser) => new
    {
        enabled = s.Enabled,
        dry_run = s.DryRun,
        min_fit_score = s.MinFitScore,
        max_per_run = s.MaxPerRun,
        max_per_day = s.MaxPerDay,
        work_authorization = s.WorkAuthorization,
        needs_sponsorship = s.NeedsSponsorship,
        clearance_ok = s.ClearanceOk,
        salary_expectation = s.SalaryExpectation,
        phone = s.Phone,
        country = s.Country,
        long_tail = s.LongTail,
        // Whether this instance runs a worker at all — so the UI can say "saved, but
        // nothing will happen until the operator starts the agent container".
        worker_running = options.Enabled,
        // Whether this instance has a browser container to fill and submit forms with.
        browser_available = browser.IsConfigured,
    };
}
