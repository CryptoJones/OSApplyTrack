// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Scrape;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Evaluates one lead end to end — fetch the posting, judge it, write the audit row —
/// and is shared by the unattended <see cref="AgentWorker"/> and the on-demand
/// <c>POST /api/apps/{name}/verdict</c> endpoint, so a human-triggered evaluation and
/// a worker pass record exactly the same thing.
/// </summary>
public sealed class LeadEvaluator
{
    private readonly FitJudge _judge;
    private readonly JobPageFetcher _fetcher;
    private readonly ILogger<LeadEvaluator> _log;

    public LeadEvaluator(FitJudge judge, JobPageFetcher fetcher, ILogger<LeadEvaluator> log)
    {
        _judge = judge;
        _fetcher = fetcher;
        _log = log;
    }

    /// <summary>
    /// Judge one lead and write the verdict — or the failure — to the audit trail.
    /// </summary>
    public async Task<Verdict?> EvaluateAsync(
        AppRecord rec, Resume resume, Criteria criteria, AgentSettings settings,
        EffectiveLlmConfig cfg, AgentEventRepo events, CancellationToken ct)
    {
        var posting = await ReadPostingAsync(rec.Fields.Link, ct);
        try
        {
            var verdict = await _judge.JudgeAsync(rec.Fields, posting, resume, criteria, settings, cfg, ct);
            await events.RecordAsync(AgentEventRepo.Kinds.Verdict, rec.Name, new
            {
                verdict.Decision, verdict.Confidence, verdict.Rationale, verdict.Concerns,
                verdict.Disqualifiers, verdict.ModelConsulted,
                Model = cfg.Model, PostingChars = posting.Length,
                rec.Fields.Company, rec.Fields.Role, Score = rec.Fields.Score,
            });
            _log.LogInformation("{Name}: {Decision} ({Confidence}%)", rec.Name, verdict.Decision, verdict.Confidence);
            return verdict;
        }
        catch (Exception ex) when (ex is LlmUnavailableException or AppValidationException)
        {
            // The model could not produce a verdict: record it and move on. This must
            // never fall through to "proceed".
            _log.LogWarning("{Name}: no verdict: {Reason}", rec.Name, ex.Message);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = ex.Message, rec.Fields.Company, rec.Fields.Role });
            return null;
        }
    }

    /// <summary>Best-effort posting fetch, the same contract as the draft endpoint's.</summary>
    public async Task<string> ReadPostingAsync(string link, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(link))
            return "";
        try
        {
            var (html, finalUrl) = await _fetcher.FetchAsync(link, ct);
            return JobPostingParser.Parse(html, finalUrl).Description?.Trim() ?? "";
        }
        catch (Exception ex) when (ex is ScrapeUnavailableException or AppValidationException)
        {
            _log.LogInformation("no posting text for {Link}: {Reason}", link, ex.Message);
            return "";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "unexpected failure fetching {Link}", link);
            return "";
        }
    }

}
