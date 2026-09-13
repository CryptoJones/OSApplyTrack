// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Agent.Greenhouse;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;

namespace ApplyTrack.Api.Agent;

/// <summary>The tenant-scoped repos a build writes through.</summary>
public sealed record PacketScope(
    ApplicationRepo Apps, CoverLetterRepo Letters, AgentPacketRepo Packets, AgentEventRepo Events,
    AnswerBankRepo Bank);

/// <summary>The tenant facts a build draws on.</summary>
public sealed record PacketInputs(
    Resume Resume, AgentSettings Settings, string Email, string Signature, bool LettersEnabled,
    EffectiveLlmConfig Cfg);

/// <summary>
/// Turns a <c>proceed</c> verdict into a prepared packet and parks the application
/// in <c>ready</c> — the Ready-to-submit queue. Greenhouse postings get their real
/// form from the public Job Board API; everything else gets the standard set
/// (name, email, phone, links, résumé, letter) and the copy-and-open path. The
/// cover letter is drafted if the tenant allows it and none exists; the answers
/// come from <see cref="AnswerDrafter"/>. Submission is still the human's.
/// </summary>
public sealed partial class PacketBuilder
{
    public const string PacketEvent = "packet";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly GreenhouseBoard _greenhouse;
    private readonly AnswerDrafter _answers;
    private readonly CoverLetterDrafter _letters;
    private readonly LeadEvaluator _evaluator;
    private readonly BrowserOptions _browser;
    private readonly FormDiscoverer _discoverer;
    private readonly ILogger<PacketBuilder> _log;

    public PacketBuilder(
        GreenhouseBoard greenhouse, AnswerDrafter answers, CoverLetterDrafter letters,
        LeadEvaluator evaluator, BrowserOptions browser, FormDiscoverer discoverer, ILogger<PacketBuilder> log)
    {
        _greenhouse = greenhouse;
        _answers = answers;
        _letters = letters;
        _evaluator = evaluator;
        _browser = browser;
        _discoverer = discoverer;
        _log = log;
    }

    /// <summary>The form every non-Greenhouse packet gets — what any ATS asks for first.</summary>
    public static List<PacketQuestion> StandardQuestions() =>
    [
        new("std:first_name", "First name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
        new("std:last_name", "Last name", true, PacketQuestion.Text, [], PacketQuestion.Standard),
        new("std:email", "Email", true, PacketQuestion.Text, [], PacketQuestion.Standard),
        new("std:phone", "Phone", false, PacketQuestion.Text, [], PacketQuestion.Standard),
        new("std:linkedin_profile", "LinkedIn profile", false, PacketQuestion.Text, [], PacketQuestion.Standard),
        new("std:resume", "Résumé", true, PacketQuestion.File, [], PacketQuestion.Standard),
        new("std:cover_letter", "Cover letter", false, PacketQuestion.Textarea, [], PacketQuestion.Standard),
    ];

    public async Task<AgentPacket> BuildAsync(
        AppRecord rec, Verdict verdict, PacketInputs inputs, PacketScope scope, CancellationToken ct = default)
    {
        var f = rec.Fields;
        var provider = AtsProvider.Detect(f.Link, f.Source);

        // 1. The form. Greenhouse publishes it; Lever, Ashby and (opted in) the long
        // tail are discovered read-only in the browser; Workday and everyone else get
        // the standard set and the copy-and-open path.
        List<PacketQuestion>? questions = null;
        var greenhouseContent = "";
        var source = f.Source;
        // A company careers page that embeds a Greenhouse job (?gh_jid=) names its board in
        // the embed script, not in the link; a lead from anywhere but the tenant's own board
        // list carries no board in its source either. Read it off the page once (#180).
        if (provider == AtsProvider.Greenhouse && !AtsProvider.TryParseGreenhouse(f.Link, source, out _, out _)
            && AtsProvider.FindGreenhouseBoard(await _evaluator.FetchHtmlAsync(f.Link, ct)) is { } embedded)
            source = $"auto:greenhouse:{embedded}";
        if (provider == AtsProvider.Greenhouse
            && AtsProvider.TryParseGreenhouse(f.Link, source, out var board, out var jobId)
            && await _greenhouse.GetJobAsync(board, jobId, ct) is { } job)
        {
            questions = job.Questions;
            greenhouseContent = job.ContentHtml;
        }
        else if (_browser.IsConfigured && f.Link.Length > 0 && provider != AtsProvider.Greenhouse
                 && AtsProvider.BrowserCanSubmit(provider, inputs.Settings.LongTail))
        {
            try
            {
                questions = await _discoverer.DiscoverAsync(AtsProvider.ApplyUrl(f.Link, provider), ct);
            }
            catch (Exception ex) when (ex is AppValidationException or Microsoft.Playwright.PlaywrightException or TimeoutException)
            {
                _log.LogInformation("{Name}: form discovery failed: {Reason}", rec.Name, ex.Message.Split('\n')[0]);
            }
        }
        questions ??= StandardQuestions();

        // 2. The posting the answers are grounded in — the page, else Greenhouse's copy.
        var excerpt = await _evaluator.ReadPostingAsync(f.Link, ct);
        if (excerpt.Length == 0 && greenhouseContent.Length > 0)
            excerpt = StripHtml(greenhouseContent);

        // 3. The letter: keep an existing one, draft when allowed, never block on it.
        var letter = await scope.Letters.GetBodyAsync(rec.Name) ?? "";
        if (letter.Length == 0 && inputs.LettersEnabled && !inputs.Resume.IsEmpty && inputs.Cfg.IsConfigured)
        {
            try
            {
                letter = await _letters.DraftAsync(f, inputs.Resume, inputs.Cfg, inputs.Signature, excerpt, ct);
                await scope.Letters.UpsertAsync(rec.Name, letter, inputs.Cfg.Model);
            }
            catch (Exception ex) when (ex is LlmUnavailableException or AppValidationException)
            {
                _log.LogWarning("{Name}: no cover letter: {Reason}", rec.Name, ex.Message);
            }
        }

        // 4. The answers — the person's own from the answer bank first, then the
        // deterministic set, then the model; and every screening question this form asked
        // goes into the bank with the answer it got, so the person can see and correct it.
        var ctx = new AnswerContext(inputs.Resume, inputs.Settings, inputs.Email, letter, excerpt);
        var (answers, review) = await _answers.DraftAsync(questions, ctx, inputs.Cfg, ct, await scope.Bank.PinnedAsync());
        await scope.Bank.RecordAsync(questions, answers, rec.Name);

        var packet = new AgentPacket
        {
            ApplicationName = rec.Name,
            Provider = provider,
            PostingExcerpt = excerpt.Length > 12000 ? excerpt[..12000] : excerpt,
            Verdict = JsonSerializer.SerializeToElement(verdict, Json),
            Questions = questions,
            Answers = answers,
            NeedsReview = review,
            Model = inputs.Cfg.Model,
        };
        packet.RecomputeReview();

        // 5. Park it. `ready` was a defined-but-unreachable status until now.
        await scope.Packets.UpsertAsync(packet);
        if (f.Status != "ready")
            await scope.Apps.UpdateStructuredAsync(rec.Name, f with { Status = "ready" }, null);
        await scope.Events.RecordAsync(PacketEvent, rec.Name, new
        {
            provider, questions = questions.Count, answered = answers.Count,
            needs_review = packet.NeedsReview.Count, has_letter = letter.Length > 0,
            f.Company, f.Role,
        });
        _log.LogInformation("{Name}: packet ready ({Provider}, {Answered}/{Questions} answered, {Review} to review)",
            rec.Name, provider, answers.Count, questions.Count, packet.NeedsReview.Count);
        return await scope.Packets.GetAsync(rec.Name) ?? packet;
    }

    /// <summary>
    /// Bring a packet's <b>standard</b> answers — name, email, phone, links, location,
    /// country, the letter — up to date with the profile as it is now. A packet froze
    /// these at build time, so when the account email changed every built packet had to be
    /// patched by hand (#189). Only questions the drafter answers deterministically move;
    /// a screening answer, the model's or the person's, is never touched. True when
    /// anything changed, so the caller can persist it.
    /// </summary>
    public static bool RefreshStandardAnswers(AgentPacket packet, AnswerContext ctx)
    {
        var changed = false;
        foreach (var q in packet.Questions)
        {
            if (q.Kind != PacketQuestion.Standard || q.Type == PacketQuestion.File)
                continue;
            var (answer, _) = AnswerDrafter.Deterministic(q, ctx);
            if (answer is null)
                continue;
            if (packet.Answers.TryGetValue(q.Id, out var current) && current == answer)
                continue;
            packet.Answers[q.Id] = answer;
            changed = true;
        }
        if (changed)
            packet.RecomputeReview();
        return changed;
    }

    private static string StripHtml(string html) =>
        Whitespace().Replace(System.Net.WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();
}
