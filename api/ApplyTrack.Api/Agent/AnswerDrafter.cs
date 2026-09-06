// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Agent;

/// <summary>Everything the drafter may draw on for one packet.</summary>
public sealed record AnswerContext(
    Resume Resume, AgentSettings Settings, string Email, string CoverLetter, string PostingExcerpt);

/// <summary>
/// Fills an application form's questions. Two passes: <b>deterministic</b> answers —
/// name, email, phone, links, work authorization, sponsorship, clearance, salary —
/// come straight from the résumé and the tenant's standing answers and never reach
/// the model; then the remaining screening questions go to the model in one call,
/// grounded strictly in the résumé brief and the posting. Anything the model cannot
/// answer from those facts is left blank and flagged for the human rather than
/// invented. EEO/demographic questions are never answered at all: always optional,
/// and a wrong guess is worse than a blank.
/// </summary>
public sealed partial class AnswerDrafter
{
    private const int MaxModelQuestions = 25;
    private const int MaxPostingChars = 6000;

    [GeneratedRegex(@"authori[sz]ed|legally|eligib\w* to work|right to work|work permit", RegexOptions.IgnoreCase)]
    private static partial Regex Authorization();
    [GeneratedRegex(@"sponsor", RegexOptions.IgnoreCase)]
    private static partial Regex Sponsorship();
    [GeneratedRegex(@"clearance", RegexOptions.IgnoreCase)]
    private static partial Regex Clearance();
    [GeneratedRegex(@"salary|compensation|pay (?:expectation|range|rate)|desired pay|rate expectation", RegexOptions.IgnoreCase)]
    private static partial Regex Salary();
    [GeneratedRegex(@"linkedin", RegexOptions.IgnoreCase)]
    private static partial Regex LinkedIn();
    [GeneratedRegex(@"github", RegexOptions.IgnoreCase)]
    private static partial Regex GitHub();
    [GeneratedRegex(@"portfolio|website|personal site|url", RegexOptions.IgnoreCase)]
    private static partial Regex Website();
    [GeneratedRegex(@"\bphone\b|mobile", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();
    [GeneratedRegex(@"\be-?mail\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRe();
    [GeneratedRegex(@"first name|given name", RegexOptions.IgnoreCase)]
    private static partial Regex FirstName();
    [GeneratedRegex(@"last name|surname|family name", RegexOptions.IgnoreCase)]
    private static partial Regex LastName();
    [GeneratedRegex(@"^full name$|^name$|your name", RegexOptions.IgnoreCase)]
    private static partial Regex FullName();
    [GeneratedRegex(@"how did you hear|\breferr|\bstart date|available to start|notice period|\bearliest", RegexOptions.IgnoreCase)]
    private static partial Regex HumanOnly();

    private sealed record ModelAnswer(string? Id, string? Answer);
    private sealed record ModelReply(List<ModelAnswer>? Answers);

    private readonly StructuredCompleter _completer;

    public AnswerDrafter(StructuredCompleter completer) => _completer = completer;

    public async Task<(Dictionary<string, string> Answers, List<ReviewItem> NeedsReview)> DraftAsync(
        IReadOnlyList<PacketQuestion> questions, AnswerContext ctx, EffectiveLlmConfig cfg,
        CancellationToken ct = default)
    {
        var answers = new Dictionary<string, string>();
        var review = new List<ReviewItem>();
        var forModel = new List<PacketQuestion>();

        foreach (var q in questions)
        {
            if (q.Kind == PacketQuestion.Eeo)
                continue; // never answered, never flagged
            if (q.Type == PacketQuestion.File)
                continue; // attached at submit (résumé) — not a text answer
            var (answer, reason) = Deterministic(q, ctx);
            if (answer is not null)
            {
                answers[q.Id] = answer;
                continue;
            }
            if (reason is not null)
            {
                review.Add(new ReviewItem(q.Id, reason));
                continue;
            }
            forModel.Add(q);
        }

        if (forModel.Count > 0)
        {
            if (!cfg.IsConfigured)
            {
                foreach (var q in forModel)
                    review.Add(new ReviewItem(q.Id, "no model configured to draft an answer"));
            }
            else
            {
                try
                {
                    var drafted = await AskModelAsync(forModel.Take(MaxModelQuestions).ToList(), ctx, cfg, ct);
                    foreach (var q in forModel)
                    {
                        if (drafted.TryGetValue(q.Id, out var a) && !string.IsNullOrWhiteSpace(a))
                            answers[q.Id] = a;
                        else
                            review.Add(new ReviewItem(q.Id, "the model could not answer this from your résumé"));
                    }
                }
                catch (LlmUnavailableException ex)
                {
                    // The packet is still built; every model-owed answer becomes the human's.
                    foreach (var q in forModel)
                        review.Add(new ReviewItem(q.Id, "no draft: " + ex.Message));
                }
            }
        }

        // Optional questions nobody could answer don't block Submit.
        review = review.Where(r => questions.First(q => q.Id == r.Id).Required
                                   || r.Reason.StartsWith("the model", StringComparison.Ordinal)
                                   || r.Reason.StartsWith("no draft", StringComparison.Ordinal)).ToList();
        return (answers, review);
    }

    /// <summary>
    /// The answer, or a review reason, or (null, null) meaning "ask the model". Public for tests.
    /// </summary>
    public static (string? Answer, string? Reason) Deterministic(PacketQuestion q, AnswerContext ctx)
    {
        var id = q.Id.StartsWith("std:", StringComparison.Ordinal) ? q.Id[4..] : q.Id;
        var label = q.Label;
        var (first, last) = SplitName(ctx.Resume.FullName);

        if (id is "first_name" || FirstName().IsMatch(label))
            return first.Length > 0 ? (first, null) : (null, "add your name in Résumé settings");
        if (id is "last_name" || LastName().IsMatch(label))
            return last.Length > 0 ? (last, null) : (null, "add your name in Résumé settings");
        if (FullName().IsMatch(label))
            return ctx.Resume.FullName.Length > 0 ? (ctx.Resume.FullName, null) : (null, "add your name in Résumé settings");
        if (id is "email" || EmailRe().IsMatch(label))
            return ctx.Email.Length > 0 ? (ctx.Email, null) : (null, "no email on the account");
        if (id is "phone" || Phone().IsMatch(label))
            return ctx.Settings.Phone.Length > 0 ? (ctx.Settings.Phone, null) : (null, "add a phone number in Settings · Agent");
        if (id is "cover_letter")
            return ctx.CoverLetter.Length > 0 ? (ctx.CoverLetter, null) : (null, "no cover letter drafted");
        if (id is "linkedin_profile" || LinkedIn().IsMatch(label))
            return Link(ctx.Resume, "linkedin") is { } li ? (li, null) : (null, "no LinkedIn link in your résumé");
        if (GitHub().IsMatch(label))
            return Link(ctx.Resume, "github") is { } gh ? (gh, null) : (null, "no GitHub link in your résumé");
        if (id is "website" || Website().IsMatch(label))
            return FirstLink(ctx.Resume) is { } site ? (site, null) : (null, "no website link in your résumé");
        if (id is "location")
            return ctx.Resume.Location.Length > 0 ? (ctx.Resume.Location, null) : (null, "add a location in Résumé settings");

        if (Sponsorship().IsMatch(label))
            return YesNo(q, ctx.Settings.NeedsSponsorship, "Settings · Agent says whether you need sponsorship");
        if (Clearance().IsMatch(label))
            return YesNo(q, ctx.Settings.ClearanceOk, "Settings · Agent says whether you hold a clearance");
        if (Authorization().IsMatch(label))
        {
            var auth = ctx.Settings.WorkAuthorization;
            if (auth.Length == 0)
                return (null, "set your work authorization in Settings · Agent");
            return q.Options.Count > 0
                ? YesNo(q, !LooksNegative(auth), "pick the work-authorization option yourself")
                : (auth, null);
        }
        if (Salary().IsMatch(label))
            return ctx.Settings.SalaryExpectation.Length > 0 && q.Options.Count == 0
                ? (ctx.Settings.SalaryExpectation, null)
                : (null, "salary is yours to state — set an expectation in Settings · Agent");
        if (HumanOnly().IsMatch(label))
            return (null, "only you can answer this one");

        return (null, null);
    }

    private static (string? Answer, string? Reason) YesNo(PacketQuestion q, bool yes, string fallback)
    {
        if (q.Options.Count == 0)
            return (yes ? "Yes" : "No", null);
        var pick = q.Options.FirstOrDefault(o => o.StartsWith(yes ? "yes" : "no", StringComparison.OrdinalIgnoreCase));
        return pick is null ? (null, fallback) : (pick, null);
    }

    private static bool LooksNegative(string auth) =>
        auth.Contains("not ", StringComparison.OrdinalIgnoreCase)
        || auth.StartsWith("no", StringComparison.OrdinalIgnoreCase)
        || auth.Contains("require", StringComparison.OrdinalIgnoreCase);

    private static (string First, string Last) SplitName(string full)
    {
        var parts = full.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => ("", ""),
            1 => (parts[0], ""),
            _ => (parts[0], string.Join(' ', parts.Skip(1))),
        };
    }

    private static string? Link(Resume r, string host) =>
        r.Links.FirstOrDefault(l => l.Url.Contains(host, StringComparison.OrdinalIgnoreCase)
                                    || l.Label.Contains(host, StringComparison.OrdinalIgnoreCase))?.Url;

    private static string? FirstLink(Resume r) =>
        r.Links.FirstOrDefault(l => !l.Url.Contains("linkedin", StringComparison.OrdinalIgnoreCase)
                                    && !l.Url.Contains("github", StringComparison.OrdinalIgnoreCase))?.Url
        ?? r.Links.FirstOrDefault()?.Url;

    private async Task<Dictionary<string, string>> AskModelAsync(
        List<PacketQuestion> questions, AnswerContext ctx, EffectiveLlmConfig cfg, CancellationToken ct)
    {
        var system =
            """
            You are filling in a job application's screening questions on behalf of a
            candidate. Answer in the first person, briefly and concretely. Use ONLY the
            facts in the CANDIDATE BRIEF; do not invent experience, numbers, employers, or
            preferences. When a question cannot be answered truthfully from the brief —
            or asks about something personal the brief does not cover — set its answer
            to null so a human can fill it in. For a question with OPTIONS, the answer
            must be exactly one of the options, verbatim, or null.

            Reply with JSON of this exact shape:
            {"answers": [{"id": "<question id>", "answer": "<text>" | null}, ...]}
            """;
        var sb = new StringBuilder();
        sb.AppendLine("QUESTIONS:");
        foreach (var q in questions)
        {
            sb.Append("- id: ").Append(q.Id).Append(" | ").Append(q.Required ? "required" : "optional")
              .Append(" | ").Append(q.Type).Append(" | ").AppendLine(q.Label);
            if (q.Options.Count > 0)
                sb.Append("  OPTIONS: ").AppendLine(string.Join(" / ", q.Options));
        }
        var posting = ctx.PostingExcerpt.Trim();
        if (posting.Length > MaxPostingChars) posting = posting[..MaxPostingChars] + "\n[…truncated]";
        var user =
            $"""
            {sb}
            JOB POSTING (for context):
            {(posting.Length > 0 ? posting : "(not available)")}

            ELIGIBILITY (fixed facts):
            {ctx.Settings.ToEligibilityBrief()}

            CANDIDATE BRIEF (the only facts you may use):
            {ctx.Resume.ToBrief()}
            """;

        var ids = questions.Select(q => q.Id).ToHashSet();
        var reply = await _completer.CompleteAsync<ModelReply>(system, user, cfg,
            r => r.Answers is null ? "\"answers\" must be an array" : null, ct);

        var byId = questions.ToDictionary(q => q.Id);
        var outAnswers = new Dictionary<string, string>();
        foreach (var a in reply.Answers!)
        {
            if (a.Id is null || !ids.Contains(a.Id) || string.IsNullOrWhiteSpace(a.Answer))
                continue;
            var q = byId[a.Id];
            var text = a.Answer.Trim();
            if (q.Options.Count > 0)
            {
                // Exact option or nothing — a paraphrased option would not submit.
                var match = q.Options.FirstOrDefault(o => o.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (match is null) continue;
                text = match;
            }
            if (text.Length > InputLimits.PacketAnswer) text = text[..InputLimits.PacketAnswer];
            outAnswers[a.Id] = text;
        }
        return outAnswers;
    }
}
