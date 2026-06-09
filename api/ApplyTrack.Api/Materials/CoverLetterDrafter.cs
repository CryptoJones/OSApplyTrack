// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Materials;

/// <summary>
/// Drafts a tailored cover letter for one application — the multi-tenant, server-side
/// heir to the original <c>materials.py</c>. The prompt's anti-hallucination brief is
/// the tenant's own structured <see cref="Resume"/> (not hardcoded facts), and the
/// output is plain text/Markdown (the LaTeX/PDF path is a later module).
/// </summary>
public sealed class CoverLetterDrafter
{
    private readonly ILlmClient _llm;

    public CoverLetterDrafter(ILlmClient llm) => _llm = llm;

    // Which strength the letter leads with — mirrors the original lane switch.
    private static readonly Dictionary<string, string> LaneLead = new()
    {
        ["dotnet"] = ".NET / backend-engineering",
        ["devrel"] = "developer-relations / developer-enablement",
        ["ai"] = "AI-agent / applied-AI",
    };

    public async Task<string> DraftAsync(
        AppFields app, Resume resume, EffectiveLlmConfig cfg, CancellationToken ct = default)
    {
        if (resume.IsEmpty)
            throw new AppValidationException("add your résumé in Résumé settings before drafting a cover letter");

        var (system, user) = BuildPrompt(app, resume);
        var body = (await _llm.CompleteAsync(system, user, cfg, ct)).Trim();

        // Reject empty/implausible output rather than save a broken letter.
        if (body.Length is < 40 or > 6000)
            throw new LlmUnavailableException("the model returned an unusable draft — try again");
        return body;
    }

    private static (string System, string User) BuildPrompt(AppFields app, Resume resume)
    {
        var lane = LaneLead.TryGetValue(app.Lane, out var lead) ? lead : LaneLead["ai"];
        var name = resume.FullName.Length > 0 ? resume.FullName : "the candidate";

        var system =
            $"""
            You are an expert cover-letter writer drafting a tailored, ready-to-send cover
            letter for a job applicant. Write in the first person as the applicant.

            Hard rules:
            - Use ONLY the facts in the CANDIDATE BRIEF. Do NOT invent employers, titles,
              metrics, or any claim not present there. Do NOT assert facts about the company
              beyond what is given; you may speak to the role and domain at a general level.
            - Confident, concrete, specific voice. No clichés or filler ("I am excited to",
              "team player", "fast-paced environment", "passionate", "I believe").
            - Lead with the applicant's {lane} strengths and connect them to what THIS role
              and company are about.
            - Plain text / light Markdown only: no preamble or commentary, no code fences,
              no headings, no bullet lists, no placeholders like [Company].

            Structure the letter as:
            - a "Dear Hiring Team," greeting,
            - 2-3 short body paragraphs, ~200-280 words total,
            - a brief sign-off ending with "{name}".
            Do NOT include a date or a postal address.
            """;

        var user =
            $"""
            COMPANY: {Or(app.Company, "(unspecified)")}
            ROLE: {Or(app.Role, "the open role")}
            LOCATION: {Or(app.Location, "(unspecified)")}
            JOB NOTES: {Or(app.Notes, "(none)")}
            POSTING: {Or(app.Link, "(none)")}

            CANDIDATE BRIEF (the only facts you may assert about the applicant):
            {resume.ToBrief()}
            """;

        return (system, user);
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}
