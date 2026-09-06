// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Agent;

/// <summary>The agent's decision about one lead, as stored in the audit trail.</summary>
public sealed record Verdict(
    string Decision, int Confidence, string Rationale, List<string> Concerns,
    List<string> Disqualifiers, bool ModelConsulted)
{
    public const string Proceed = "proceed";
    public const string Skip = "skip";

    public bool IsProceed => Decision == Proceed;
}

/// <summary>
/// Re-reads a posting and forms an independent fit verdict. <c>classify()</c> in the
/// poller is a keyword count, not a judgement — it is what mis-scores VB.NET roles
/// and "the debate rages on" — so a score threshold alone must never trigger anything
/// outward-facing. The judge can <b>veto</b> a keyword-inflated score, and a skip is
/// recorded with its reason. Deterministic disqualifiers are enforced first and cost
/// no tokens; only a posting that clears them reaches the model.
/// </summary>
public sealed class FitJudge
{
    // The model's answer, before it is combined with the code-side checks.
    private sealed record ModelVerdict(string? Decision, int? Confidence, string? Rationale, List<string>? Concerns);

    /// <summary>How much posting text reaches the prompt — same budget as the drafter.</summary>
    private const int MaxPostingChars = 8000;

    private readonly StructuredCompleter _completer;

    public FitJudge(StructuredCompleter completer) => _completer = completer;

    public async Task<Verdict> JudgeAsync(
        AppFields app, string postingText, Resume resume, Criteria criteria,
        AgentSettings settings, EffectiveLlmConfig cfg, CancellationToken ct = default)
    {
        var hard = Disqualifiers.Find(app, postingText, criteria, settings).ToList();
        if (hard.Count > 0)
        {
            return new Verdict(Verdict.Skip, 100,
                "Disqualified before the model was consulted: " + string.Join("; ", hard) + ".",
                [], hard, ModelConsulted: false);
        }

        var (system, user) = BuildPrompt(app, postingText, resume, criteria, settings);
        var m = await _completer.CompleteAsync<ModelVerdict>(system, user, cfg, Validate, ct);
        var decision = m.Decision!.Trim().ToLowerInvariant();
        var concerns = (m.Concerns ?? [])
            .Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).Take(10).ToList();
        return new Verdict(decision, Math.Clamp(m.Confidence ?? 0, 0, 100),
            m.Rationale!.Trim(), concerns, [], ModelConsulted: true);
    }

    private static string? Validate(ModelVerdict v)
    {
        var d = v.Decision?.Trim().ToLowerInvariant();
        if (d is not (Verdict.Proceed or Verdict.Skip))
            return "\"decision\" must be exactly \"proceed\" or \"skip\"";
        if (v.Confidence is null)
            return "\"confidence\" (an integer 0-100) is required";
        if (string.IsNullOrWhiteSpace(v.Rationale))
            return "\"rationale\" must be a non-empty sentence";
        return null;
    }

    private static (string System, string User) BuildPrompt(
        AppFields app, string postingText, Resume resume, Criteria criteria, AgentSettings settings)
    {
        var system =
            """
            You are a careful job-search assistant deciding whether a candidate should
            apply to one specific job posting. Your job is to VETO poor fits, not to be
            encouraging. A keyword scorer already thought this posting looked promising;
            it is frequently wrong, and you are the check on it.

            Decide "proceed" only when ALL of these hold:
            - the role's core requirements are met by facts in the CANDIDATE BRIEF
              (do not assume skills that are not listed);
            - nothing in the posting conflicts with the ELIGIBILITY facts (work
              authorization, sponsorship, clearance, location and remote preferences);
            - the seniority and domain are plausible for the candidate's history.
            Otherwise decide "skip". When the posting text is missing, decide from the
            title, notes and location only, and lower your confidence accordingly.

            Reply with JSON of this exact shape:
            {"decision": "proceed" | "skip",
             "confidence": <integer 0-100>,
             "rationale": "<two or three sentences naming the specific requirements met or missed>",
             "concerns": ["<a short phrase per open question the candidate should check>"]}
            """;

        var eligibility = settings.ToEligibilityBrief();
        if (criteria.RemoteOnly) eligibility += "\nRemote only: yes";
        if (criteria.ExcludeLocations.Count > 0)
            eligibility += "\nExcluded locations: " + string.Join(", ", criteria.ExcludeLocations);

        var user =
            $"""
            COMPANY: {Or(app.Company, "(unspecified)")}
            ROLE: {Or(app.Role, "(unspecified)")}
            LOCATION: {Or(app.Location, "(unspecified)")}
            KEYWORD SCORE: {Or(app.Score, "(none)")}/100
            NOTES: {Or(app.Notes, "(none)")}

            {PostingBlock(postingText)}

            ELIGIBILITY (facts about the candidate; treat as fixed):
            {eligibility}

            CANDIDATE BRIEF (the only facts you may assume about the candidate):
            {resume.ToBrief()}
            """;
        return (system, user);
    }

    private static string PostingBlock(string postingText)
    {
        var text = postingText.Trim();
        if (text.Length == 0)
            return "JOB POSTING: (not available — the posting text could not be retrieved)";
        if (text.Length > MaxPostingChars)
            text = text[..MaxPostingChars].TrimEnd() + "\n[…posting truncated]";
        return "JOB POSTING:\n" + text;
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}
