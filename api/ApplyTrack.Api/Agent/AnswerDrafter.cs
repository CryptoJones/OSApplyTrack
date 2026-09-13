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
    // "remuneration" was missing, so the live GR8 Tech field never reached the
    // deterministic branch at all — it fell through to the model, which answered it from
    // the brief's saved figure with no units check. Keep this list generous: a salary
    // question that is NOT recognised skips every guard below.
    [GeneratedRegex(@"salary|compensation|remunerat|renumerat|pay (?:expectation|range|rate)|desired pay|rate expectation|expected (?:pay|rate)|day rate", RegexOptions.IgnoreCase)]
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
    // Anchored, and checked AFTER the eligibility rules: "…to remain in your current
    // location?" is a sponsorship question that once got a city typed into it.
    [GeneratedRegex(@"^(?:current )?location\b|\(city\)|^city\b|^(?:what is )?your (?:current )?location\b|where (?:are you|do you) (?:based|located|live)", RegexOptions.IgnoreCase)]
    private static partial Regex LocationRe();
    [GeneratedRegex(@"^country\b|country of residence|(?:which|what) country|(?:choose|select|enter) (?:your )?(?:current )?(?:location |residence |home )?country\b", RegexOptions.IgnoreCase)]
    private static partial Regex CountryRe();

    private sealed record ModelAnswer(string? Id, string? Answer);
    private sealed record ModelReply(List<ModelAnswer>? Answers);

    private readonly StructuredCompleter _completer;

    public AnswerDrafter(StructuredCompleter completer) => _completer = completer;

    public async Task<(Dictionary<string, string> Answers, List<ReviewItem> NeedsReview)> DraftAsync(
        IReadOnlyList<PacketQuestion> questions, AnswerContext ctx, EffectiveLlmConfig cfg,
        CancellationToken ct = default, IReadOnlyDictionary<string, string>? pinned = null)
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
            // The person's own answer to this question, from the answer bank, wins over
            // everything: it is how a wrong answer gets corrected once instead of per form.
            if (pinned is not null && pinned.TryGetValue(AnswerBankRepo.KeyFor(q), out var mine) && mine.Length > 0)
            {
                var (fitted, why) = FitToOptions(q, mine);
                if (fitted is not null) answers[q.Id] = fitted;
                else review.Add(new ReviewItem(q.Id, why!));
                continue;
            }
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
    /// A banked answer as this form will take it: verbatim for free text; for a fixed list,
    /// the option it names (case-insensitively; a multi-select takes a comma-separated
    /// set), else a reason for the human. Public for tests.
    /// </summary>
    public static (string? Answer, string? Reason) FitToOptions(PacketQuestion q, string answer)
    {
        if (q.Options.Count == 0 || q.Type is not (PacketQuestion.Select or PacketQuestion.MultiSelect))
            return (answer, null);
        var wanted = q.Type == PacketQuestion.MultiSelect
            ? answer.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [answer.Trim()];
        var picked = new List<string>();
        foreach (var w in wanted)
        {
            var option = q.Options.FirstOrDefault(o => string.Equals(o.Trim(), w, StringComparison.OrdinalIgnoreCase));
            if (option is null)
                return (null, $"your saved answer \"{w}\" isn't one of this form's options — pick one");
            picked.Add(option);
        }
        return (string.Join(", ", picked), null);
    }

    /// <summary>
    /// The answer, or a review reason, or (null, null) meaning "ask the model". Public for tests.
    /// </summary>
    public static (string? Answer, string? Reason) Deterministic(PacketQuestion q, AnswerContext ctx)
    {
        var id = q.Id.StartsWith("std:", StringComparison.Ordinal) ? q.Id[4..] : q.Id;
        // Match on the label AND its helper text. The qualifier that decides what a correct
        // answer is often lives only in the hint ("per month, in EUR"), so matching the
        // label alone reads half the question.
        var label = q.FullPrompt;
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
        // A question that asks about authorization AND sponsorship in one breath, with fixed
        // options ("No, I require support now" / "Yes, and I will not need sponsorship"), is
        // not a yes/no: the option's polarity is not the answer's. The model gets it, with
        // the eligibility brief; a bare yes/no pick put "No, I require support now" on a
        // citizen's application.
        var combined = Sponsorship().IsMatch(label) && Authorization().IsMatch(label) && q.Options.Count > 0;
        if (combined)
            return (null, null);
        if (Sponsorship().IsMatch(label))
            return YesNo(q, ctx.Settings.NeedsSponsorship, "Settings · Agent says whether you need sponsorship");
        if (Clearance().IsMatch(label))
            return YesNo(q, ctx.Settings.ClearanceOk, "Settings · Agent says whether you hold a clearance");
        // "Minden, Nebraska (Remote)" is how a résumé says it; "Minden, Nebraska" is what a
        // form's city autocomplete can geocode. The suffix is for the reader, not the form.
        if (id is "location" || LocationRe().IsMatch(q.Label))
            return StripLocationSuffix(ctx.Resume.Location) is { Length: > 0 } loc ? (loc, null) : (null, "add a location in Résumé settings");
        if (id is "country" || CountryRe().IsMatch(q.Label))
        {
            var country = Country(ctx);
            if (country.Length == 0) return (null, "add your country in Settings · Agent");
            // A fixed list spells it its own way: "United States of America", "United
            // States, U.S., USA". The answer must be one of the options, verbatim.
            if (q.Options.Count == 0) return (country, null);
            return PickCountryOption(q.Options, country) is { } option ? (option, null)
                : (null, "pick the country option yourself — none reads as " + country);
        }
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
        {
            if (ctx.Settings.SalaryExpectation.Length == 0 || q.Options.Count > 0)
                return (null, "salary is yours to state — set an expectation in Settings · Agent");
            return SalaryFor(label, ctx.Settings);
        }
        if (HumanOnly().IsMatch(label))
            return (null, "only you can answer this one");

        return (null, null);
    }

    /// <summary>
    /// The salary answer for a form's question, from the saved expectation. The saved
    /// figure carries a period and a currency — stated in Settings · Agent, else whatever
    /// the figure itself says. A question that asks in another <b>currency</b> is never
    /// converted: rates move and a wrong figure reads as a well-formed answer. One that
    /// asks for another <b>period</b> in the same currency is converted (annual ÷ 12 per
    /// month, annual ÷ 2080 per hour) when the saved period is known, so "expected
    /// monthly salary (USD)" no longer needs the person every time (#188). Observed live
    /// before any of this: an annual USD expectation typed into "remuneration (Gross) per
    /// month, in EUR" — which is why a mismatch that cannot be converted is refused, not
    /// guessed. Public for tests.
    /// </summary>
    public static (string? Answer, string? Reason) SalaryFor(string prompt, AgentSettings settings)
    {
        var saved = settings.SalaryExpectation.Trim();
        if (saved.Length == 0)
            return (null, "salary is yours to state — set an expectation in Settings · Agent");
        var havePeriod = settings.SalaryPeriod.Length > 0 ? settings.SalaryPeriod : MoneyPeriod(saved);
        var haveCurrency = settings.SalaryCurrency.Length > 0 ? settings.SalaryCurrency : MoneyCurrency(saved);
        var wantPeriod = MoneyPeriod(prompt);
        var wantCurrency = MoneyCurrency(prompt);

        var have = string.Join(' ', new[] { havePeriod, haveCurrency }.Where(u => u.Length > 0));
        if (wantCurrency.Length > 0 && wantCurrency != haveCurrency)
            return (null, $"this field wants {MoneyUnits(prompt)} — your saved expectation "
                + (have.Length > 0 ? $"is {have}" : "does not say which currency")
                + ", so state it yourself");
        if (wantPeriod.Length == 0 || wantPeriod == havePeriod)
            return (saved, null);
        if (havePeriod.Length == 0)
            return (null, $"this field wants {MoneyUnits(prompt)} — your saved expectation "
                + (have.Length > 0 ? $"is {have}" : "does not say which currency")
                + " with no period stated; set the period in Settings · Agent or state it yourself");
        var amount = ParseAmount(saved);
        if (amount is null)
            return (null, $"this field wants a {wantPeriod} figure and your saved expectation "
                + $"\"{saved}\" is not a plain number to convert, so state it yourself");
        var annual = havePeriod switch
        {
            "monthly" => amount.Value * 12m,
            "hourly" => amount.Value * HoursPerYear,
            _ => amount.Value,
        };
        var converted = wantPeriod switch
        {
            "monthly" => annual / 12m,
            "hourly" => annual / HoursPerYear,
            _ => annual,
        };
        return (FormatAmount(converted), null);
    }

    /// <summary>A working year: 40 hours × 52 weeks, the figure payroll uses.</summary>
    private const decimal HoursPerYear = 2080m;

    /// <summary>"$140,000" → 140000; "125k" → 125000; "60/hr" → 60; a range or prose → null.</summary>
    public static decimal? ParseAmount(string text)
    {
        var m = Regex.Match(text, @"(?<!\d)(\d{1,3}(?:,\d{3})+|\d+)(?:\.(\d+))?\s*([kK])?(?!\d)");
        if (!m.Success) return null;
        // Two numbers (a range "120-140k") cannot be converted honestly.
        if (Regex.Matches(text, @"\d+(?:,\d{3})*(?:\.\d+)?").Count > 1) return null;
        var whole = m.Groups[1].Value.Replace(",", "");
        var frac = m.Groups[2].Success ? "." + m.Groups[2].Value : "";
        if (!decimal.TryParse(whole + frac, System.Globalization.NumberStyles.Number,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            return null;
        if (m.Groups[3].Success) value *= 1000m;
        return value;
    }

    /// <summary>A plain figure as a form's numeric box takes it: whole dollars, or to the
    /// cent for an hourly rate; no separators, no symbol.</summary>
    private static string FormatAmount(decimal value)
    {
        var rounded = value >= 1000m ? Math.Round(value, 0) : Math.Round(value, 2);
        return rounded.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The period and currency a money string commits to, as a comparable key like
    /// "monthly EUR" — or "" when it names neither, which means "unknown, do not assume".
    /// Deliberately conservative: it only reports what the text actually says.
    /// </summary>
    public static string MoneyUnits(string text) =>
        string.Join(' ', new[] { MoneyPeriod(text), MoneyCurrency(text) }.Where(p => p.Length > 0));

    /// <summary>"annual" | "monthly" | "hourly" | "" — only what the text says.</summary>
    public static string MoneyPeriod(string text) =>
        MonthlyRe().IsMatch(text) ? "monthly"
        : HourlyRe().IsMatch(text) ? "hourly"
        : AnnualRe().IsMatch(text) ? "annual"
        : "";

    /// <summary>"EUR" | "GBP" | "USD" | "" — only what the text says.</summary>
    public static string MoneyCurrency(string text) =>
        text.Contains('€') || EurRe().IsMatch(text) ? "EUR"
        : text.Contains('£') || GbpRe().IsMatch(text) ? "GBP"
        : text.Contains('$') || UsdRe().IsMatch(text) ? "USD"
        : "";

    [GeneratedRegex(@"per month|monthly|/\s*mo\b|\bmonth\b", RegexOptions.IgnoreCase)]
    private static partial Regex MonthlyRe();

    [GeneratedRegex(@"per hour|hourly|/\s*hr\b|\bhour\b", RegexOptions.IgnoreCase)]
    private static partial Regex HourlyRe();

    [GeneratedRegex(@"per year|per annum|annual|yearly|/\s*yr\b|\byear\b", RegexOptions.IgnoreCase)]
    private static partial Regex AnnualRe();

    [GeneratedRegex(@"\bEUR\b|\beuros?\b", RegexOptions.IgnoreCase)]
    private static partial Regex EurRe();

    [GeneratedRegex(@"\bGBP\b|\bpounds?\b|\bsterling\b", RegexOptions.IgnoreCase)]
    private static partial Regex GbpRe();

    [GeneratedRegex(@"\bUSD\b|\bdollars?\b", RegexOptions.IgnoreCase)]
    private static partial Regex UsdRe();

    /// <summary>The standing country from Settings · Agent, else what the résumé's location
    /// line implies. Public for tests.</summary>
    public static string Country(AnswerContext ctx) =>
        ctx.Settings.Country.Length > 0 ? ctx.Settings.Country : CountryFromLocation(ctx.Resume.Location);

    /// <summary>The option in a fixed country list that means <paramref name="country"/>: the
    /// same name, a known alias ("USA", "United States of America"), or a comma-joined option
    /// one of whose parts is either. Public for tests.</summary>
    public static string? PickCountryOption(IReadOnlyList<string> options, string country)
    {
        static string Norm(string s) => Regex.Replace(s.Trim().ToLowerInvariant(), @"\s+", " ");
        var want = Norm(country);
        var aliases = CountryNames.Where(kv => Norm(kv.Value) == want).Select(kv => Norm(kv.Key)).Append(want)
            .ToHashSet(StringComparer.Ordinal);
        return options.FirstOrDefault(o => Norm(o) == want)
            ?? options.FirstOrDefault(o => aliases.Contains(Norm(o)))
            ?? options.FirstOrDefault(o => o.Split(',').Select(Norm).Any(aliases.Contains))
            ?? options.FirstOrDefault(o => Norm(o).StartsWith(want + " of ", StringComparison.Ordinal));
    }

    private static readonly HashSet<string> UsStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY", "LA",
        "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND", "OH", "OK",
        "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC", "PR",
        "Alabama", "Alaska", "Arizona", "Arkansas", "California", "Colorado", "Connecticut", "Delaware", "Florida",
        "Georgia", "Hawaii", "Idaho", "Illinois", "Indiana", "Iowa", "Kansas", "Kentucky", "Louisiana", "Maine",
        "Maryland", "Massachusetts", "Michigan", "Minnesota", "Mississippi", "Missouri", "Montana", "Nebraska",
        "Nevada", "New Hampshire", "New Jersey", "New Mexico", "New York", "North Carolina", "North Dakota", "Ohio",
        "Oklahoma", "Oregon", "Pennsylvania", "Rhode Island", "South Carolina", "South Dakota", "Tennessee", "Texas",
        "Utah", "Vermont", "Virginia", "Washington", "West Virginia", "Wisconsin", "Wyoming", "Puerto Rico",
    };

    private static readonly Dictionary<string, string> CountryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["US"] = "United States", ["USA"] = "United States", ["U.S."] = "United States", ["U.S.A."] = "United States",
        ["United States"] = "United States", ["United States of America"] = "United States",
        ["UK"] = "United Kingdom", ["U.K."] = "United Kingdom", ["United Kingdom"] = "United Kingdom",
        ["England"] = "United Kingdom", ["Scotland"] = "United Kingdom", ["Wales"] = "United Kingdom",
        ["Canada"] = "Canada", ["CA"] = "Canada",
    };

    /// <summary>
    /// "Omaha, NE" → "United States"; "Minden, Nebraska (Remote)" → "United States";
    /// "Berlin, Germany" → "Germany"; "Remote" → "". A parenthesised or dashed suffix
    /// ("(Remote)", "- Hybrid") is dropped first, then only the last comma-separated part is
    /// read, and a US state (name or postal code) means the US. A bare "CA" is ambiguous
    /// (California, Canada) and reads as the state, since that is how a US résumé writes
    /// it. Conservative on purpose: "" means "ask the human".
    /// </summary>
    public static string CountryFromLocation(string location)
    {
        var parts = StripLocationSuffix(location).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return "";
        var last = parts[^1].Trim().TrimEnd('.').Trim();
        // "Lincoln, NE 68508" — drop a trailing postal code.
        last = Regex.Replace(last, @"\s+\d[\d-]*$", "");
        if (last.Length == 0) return "";
        if (UsStates.Contains(last)) return "United States";
        if (CountryNames.TryGetValue(last, out var name)) return name;
        if (parts.Length >= 2 && last.Length >= 4 && last.All(c => char.IsLetter(c) || c == ' ' || c == '\''))
            return last;
        return "";
    }

    /// <summary>"Minden, Nebraska (Remote)" → "Minden, Nebraska"; "Omaha, NE - Hybrid" → "Omaha, NE".
    /// A parenthesised or bracketed aside, or a dashed work-mode suffix, is dropped.</summary>
    public static string StripLocationSuffix(string location)
    {
        location = Regex.Replace(location, @"\s*[\(\[][^\)\]]*[\)\]]\s*", " ");
        location = Regex.Replace(location, @"\s+[-–—|/]\s*(remote|hybrid|on-?site|relocat\w*)\b.*$", "", RegexOptions.IgnoreCase);
        return Regex.Replace(location, @"\s+", " ").Trim().TrimEnd(',').Trim();
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

            Obey a question's HINT: it carries the qualifiers that decide what a correct
            answer is — units, currency, period ("per month, in EUR"), or a condition
            ("only if referred by an employee"). If the hint asks for something the brief
            cannot supply in the form requested, answer null rather than converting or
            guessing.

            An OPTIONAL question you have no real answer to must be null. Never fill one
            with a placeholder like "None", "N/A" or a restatement of the question —
            leaving it blank is correct and reads better than filler.

            Reply with JSON of this exact shape:
            {"answers": [{"id": "<question id>", "answer": "<text>" | null}, ...]}
            """;
        var sb = new StringBuilder();
        sb.AppendLine("QUESTIONS:");
        foreach (var q in questions)
        {
            sb.Append("- id: ").Append(q.Id).Append(" | ").Append(q.Required ? "required" : "optional")
              .Append(" | ").Append(q.Type).Append(" | ").AppendLine(q.Label);
            // The hint is where the units, the currency and the "only if…" conditions live.
            if (q.Help.Length > 0)
                sb.Append("  HINT: ").AppendLine(q.Help);
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
