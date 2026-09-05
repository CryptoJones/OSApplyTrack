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

    /// <summary>
    /// Draft the letter. <paramref name="postingText"/> is the job description as
    /// fetched from <see cref="AppFields.Link"/>; pass it whenever it's available.
    /// Without it the model only ever sees the poller's 280-character notes snippet
    /// and a bare URL it cannot open, and writes a letter about a posting it has not
    /// read. It is optional rather than required because drafting predates the fetch
    /// and must keep working for hand-entered leads and unreachable pages.
    /// </summary>
    public async Task<string> DraftAsync(
        AppFields app, Resume resume, EffectiveLlmConfig cfg,
        string signature = "", string postingText = "", CancellationToken ct = default)
    {
        if (resume.IsEmpty)
            throw new AppValidationException("add your résumé in Résumé settings before drafting a cover letter");

        var (system, user) = BuildPrompt(app, resume, postingText);
        var body = (await _llm.CompleteAsync(system, user, cfg, ct)).Trim();

        // Reject empty/implausible output rather than save a broken letter.
        if (body.Length is < 40 or > 6000)
            throw new LlmUnavailableException("the model returned an unusable draft — try again");
        return signature.Trim() is { Length: > 0 } closing
            ? $"{body}\n\n{closing}"
            : body;
    }

    /// <summary>How much fetched posting text reaches the prompt. Descriptions run long
    /// (Workday and LinkedIn pages routinely clear 20k characters of boilerplate) and the
    /// letter only needs the requirements, which sit near the top. Generous enough to
    /// carry them, small enough to leave room on a modest local context window.</summary>
    private const int MaxPostingChars = 8000;

    private static (string System, string User) BuildPrompt(
        AppFields app, Resume resume, string postingText)
    {
        var lane = LaneLead.TryGetValue(app.Lane, out var lead) ? lead : LaneLead["ai"];
        var system =
            $"""
            You are an expert cover-letter writer drafting a tailored, ready-to-send cover
            letter for a job applicant. Write in the first person as the applicant.

            Hard rules:
            - Use ONLY the facts in the CANDIDATE BRIEF. Do NOT invent employers, titles,
              metrics, or any claim not present there. Do NOT assert facts about the company
              beyond what the JOB POSTING states; where no posting text is given, speak to
              the role and domain at a general level rather than guessing at specifics.
            - Where the JOB POSTING is present, tie the applicant's strengths to what it
              actually asks for, in its own vocabulary. Address its stated requirements, not
              a generic version of the role.
            - Confident, concrete, specific voice. No clichés or filler ("I am excited to",
              "team player", "fast-paced environment", "passionate", "I believe").
            - Lead with the applicant's {lane} strengths and connect them to what THIS role
              and company are about.
            - Plain text / light Markdown only: no preamble or commentary, no code fences,
              no headings, no bullet lists, no placeholders like [Company].

            Structure the letter as:
            - a "Dear Hiring Team," greeting,
            - 2-3 short body paragraphs, ~200-280 words total,
            - end after the final body paragraph; do not write a sign-off or signature.
            The application will append the applicant's saved signature exactly.
            Do NOT include a date or a postal address.
            """;

        var user =
            $"""
            COMPANY: {Or(app.Company, "(unspecified)")}
            ROLE: {Or(app.Role, "the open role")}
            LOCATION: {Or(app.Location, "(unspecified)")}
            JOB NOTES: {Or(app.Notes, "(none)")}
            POSTING URL: {Or(app.Link, "(none)")}

            {PostingBlock(postingText)}

            CANDIDATE BRIEF (the only facts you may assert about the applicant):
            {resume.ToBrief()}
            """;

        return (system, user);
    }

    /// <summary>The posting section of the prompt. Says plainly when the text is missing,
    /// so the model treats the absence as a known gap instead of inventing requirements to
    /// fill it.</summary>
    private static string PostingBlock(string postingText)
    {
        var text = postingText.Trim();
        if (text.Length == 0)
            return "JOB POSTING: (not available — the posting text could not be retrieved)";
        if (text.Length > MaxPostingChars)
            text = text[..MaxPostingChars].TrimEnd() + "\n[…posting truncated]";
        return $"""
            JOB POSTING (what this role actually asks for — draw the connections from here):
            {text}
            """;
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;
}
