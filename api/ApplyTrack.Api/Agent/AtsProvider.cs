// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Which applicant-tracking system a lead's posting lives on. The poller already
/// stamps <c>source = auto:greenhouse:{board}</c> / <c>auto:lever:{site}</c> on leads it
/// found through a tenant's ATS boards — that is trusted over URL sniffing when
/// present. Only Greenhouse publishes its application form without an employer key,
/// so only Greenhouse gets a real field map here; Lever, Ashby, Workable, Breezy,
/// SmartRecruiters and join.com are discovered read-only in the browser; everything
/// else falls back to the standard question set and the copy-and-open path.
/// </summary>
public static partial class AtsProvider
{
    public const string Greenhouse = "greenhouse";
    public const string Lever = "lever";
    public const string Ashby = "ashby";
    public const string Workday = "workday";
    /// <summary>SAP SuccessFactors: the employer-branded career site (Career Site Builder) shows the
    /// posting, and Apply leads to career*.successfactors.com, which only takes applications from
    /// a signed-in candidate account — the Workday class, never driven (#214).</summary>
    public const string SuccessFactors = "successfactors";
    public const string Workable = "workable";
    public const string Breezy = "breezy";
    public const string SmartRecruiters = "smartrecruiters";
    public const string Join = "join";
    /// <summary>A job aggregator's listing page (remoteOK, Remotive, …), not the employer's
    /// posting: there is no form on it to fill. The poller resolves these to the employer's
    /// page where it can (#191); one still on the aggregator's host is applied to by hand.</summary>
    public const string Aggregator = "aggregator";
    /// <summary>A LinkedIn posting that takes applications through LinkedIn's own Easy Apply
    /// dialog (#278): the poller stages it as <c>auto:linkedin:easy</c> on the posting's own
    /// linkedin.com link, and the browser drives the dialog signed in as the tenant — but only
    /// with the tenant's <c>linkedin_easy</c> switch on. Every other linkedin.com link is still a
    /// listing of the employer's, and an <see cref="Aggregator"/>.</summary>
    public const string LinkedInEasy = "linkedin_easy";
    /// <summary>The <c>source</c> the poller stages an Easy Apply posting under.</summary>
    public const string LinkedInEasySource = "auto:linkedin:easy";
    public const string Unknown = "unknown";

    /// <summary>
    /// Hosts that list other employers' postings. The same set the Python poller carries
    /// (<c>AGGREGATOR_HOSTS</c> in <c>poll.py</c>): it tries to follow the listing's apply
    /// link to the employer, and a lead whose link is still on one of these is one it
    /// could not resolve. Kept in step by hand — the schema, not the code, is the contract,
    /// and this is a reading of the schema's <c>link</c> column.
    /// </summary>
    private static readonly string[] AggregatorHosts =
    [
        "remoteok.com", "remoteok.io", "remotive.com", "remotive.io", "jobicy.com", "arbeitnow.com",
        "weworkremotely.com", "remotefirstjobs.com", "workanywhere.pro", "news.ycombinator.com",
        // A LinkedIn posting is a listing of the employer's (#233): the poller stores the
        // employer's link; one it could not resolve is apply-by-hand like any aggregator's.
        "linkedin.com",
        // Jobright reposts employers' openings with its own "send my profile" flow in place
        // of a form (#238).
        "jobright.ai",
        // Sundayy lists employers' openings (GE Aerospace, …) and its Apply is an ad-monetised
        // redirect chain — sundayy.com → thebigjobsite.com → appcast.io → lensa.com — through
        // more aggregators, never a form. The browser followed Apply off-site and reported "no
        // form appeared"; all three job-board hosts in the chain are apply-by-hand (#243).
        "sundayy.com", "thebigjobsite.com", "lensa.com",
        // FetchJobs.co: the same chain under another name, Apply off to thebigjobsite.com (#251).
        "fetchjobs.co",
        // A Handshake posting is a listing of the employer's too (#267): the poller stores
        // the employer's link from jobApplySetting.externalUrl; one it could not resolve is
        // apply-by-hand like any aggregator's.
        "joinhandshake.com",
        // BestJobTool reposts job listings across boards without direct employer forms.
        "bestjobtool.com",
        // Adzuna lists other boards' postings; its "Apply" is a tracked redirect off-site, and
        // driven as a form its page took a click and confirmed nothing (inherent, 2026-09-22).
        "adzuna.com",
    ];

    [GeneratedRegex(@"^auto:(greenhouse|lever):([a-z0-9][a-z0-9._-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceBoard();

    // boards.greenhouse.io/{board}/jobs/{id}, job-boards.greenhouse.io/..., EU variants.
    [GeneratedRegex(@"^https?://(?:job-)?boards(?:\.eu)?\.greenhouse\.io/([a-z0-9][a-z0-9._-]*)/jobs/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseHosted();

    // A company-hosted board embed: any page with ?gh_jid=123 (board from the source).
    [GeneratedRegex(@"[?&]gh_jid=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseEmbed();

    // The board token as a careers page that embeds Greenhouse names it: the embed script
    // (…/embed/job_board?for=acme, …/embed/job_app?for=acme&token=123), the board API
    // (boards-api.greenhouse.io/v1/boards/acme/…), or a link to the hosted board.
    [GeneratedRegex(@"greenhouse\.io/embed/job_(?:board|app)[^""'\s]*?[?&](?:for|board)=([a-z0-9][a-z0-9._-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseEmbedFor();

    [GeneratedRegex(@"boards-api\.greenhouse\.io/v1/boards/([a-z0-9][a-z0-9._-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseBoardApi();

    [GeneratedRegex(@"(?:job-)?boards(?:\.eu)?\.greenhouse\.io/(?!embed\b)([a-z0-9][a-z0-9._-]*)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseBoardLink();

    // Workable's job finder (jobs.workable.com/view/{shortcode}/{slug}) and its hosted
    // application (apply.workable.com/{account}/j/{shortcode}/, form at …/apply/).
    [GeneratedRegex(@"^/view/([A-Za-z0-9]+)(?:/|$)")]
    private static partial Regex WorkableView();

    [GeneratedRegex(@"^/([^/]+)/j/([A-Za-z0-9]+)/?$")]
    private static partial Regex WorkableHosted();

    // Breezy's public posting: {company}.breezy.hr/p/{id}[-slug]; the form is at …/apply.
    [GeneratedRegex(@"^/p/([A-Za-z0-9-]+)/?$")]
    private static partial Regex BreezyPosting();

    public static string Detect(string link, string source)
    {
        var m = SourceBoard().Match(source ?? "");
        if (m.Success)
            return m.Groups[1].Value.ToLowerInvariant();

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return Unknown;
        var host = uri.Host.ToLowerInvariant();
        // Before the aggregator rule, which linkedin.com otherwise falls under: only the
        // poller's own flag makes a LinkedIn link an Easy Apply one (#278).
        if (string.Equals(source, LinkedInEasySource, StringComparison.OrdinalIgnoreCase)
            && (host == "linkedin.com" || host.EndsWith(".linkedin.com", StringComparison.Ordinal)))
            return LinkedInEasy;
        if (IsAggregatorHost(host))
            return Aggregator;
        if (host.EndsWith("greenhouse.io") || GreenhouseEmbed().IsMatch(uri.Query))
            return Greenhouse;
        if (host.EndsWith("lever.co"))
            return Lever;
        if (host.EndsWith("ashbyhq.com"))
            return Ashby;
        if (host.EndsWith("myworkdayjobs.com") || host.EndsWith("myworkdaysite.com"))
            return Workday;
        if (host.EndsWith("successfactors.com") || host.EndsWith("successfactors.eu")
            || host.EndsWith("sapsf.com") || host.EndsWith("sapsf.eu") || host.EndsWith("jobs2web.com"))
            return SuccessFactors;
        if (host.EndsWith("workable.com"))
            return Workable;
        if (host.EndsWith("breezy.hr"))
            return Breezy;
        if (host.EndsWith("smartrecruiters.com"))
            return SmartRecruiters;
        if (host == "join.com" || host.EndsWith(".join.com"))
            return Join;
        return Unknown;
    }

    /// <summary>
    /// Aggregators whose Apply never reaches anything a person or a browser can fill: one
    /// ad-monetised redirect network under four names, its hop through
    /// <c>us.thebigjobsite.com</c> sitting behind a bot check, its listings routinely another
    /// employer's posting under an invented title (#281). Nothing here is apply-by-hand —
    /// the poller does not stage it and the agent retires what is already parked.
    /// <c>lensa.com</c>, where the chain ends, is a board a person can sign up to and stays
    /// a plain aggregator. Keep in step with <c>DEAD_END_HOSTS</c> in <c>poll.py</c>.
    /// </summary>
    private static readonly string[] DeadEndHosts =
        ["sundayy.com", "thebigjobsite.com", "fetchjobs.co", "bestjobtool.com"];

    /// <summary>Whether <paramref name="link"/> is a listing on a dead-end aggregator (#281).</summary>
    public static bool IsDeadEndLink(string link)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        return DeadEndHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    /// <summary>A host that lists other employers' postings (see <see cref="Aggregator"/>).</summary>
    public static bool IsAggregatorHost(string host)
    {
        host = (host ?? "").ToLowerInvariant();
        return AggregatorHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    /// <summary>
    /// The page the application form is on. Lever and Ashby post their form one hop
    /// past the posting (<c>/apply</c>, <c>/application</c>); Workable's is at
    /// <c>…/j/{shortcode}/apply/</c> (a job-finder link goes through the shortlink, which
    /// redirects to the account's page); Breezy's is at <c>/p/{id}/apply</c>. Everyone
    /// else's is the posting itself (Greenhouse hosted pages carry the form; an embed
    /// reveals it; SmartRecruiters and join.com open theirs behind an Apply click).
    /// </summary>
    public static string ApplyUrl(string link, string provider)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return link;
        var path = uri.AbsolutePath.TrimEnd('/');
        switch (provider)
        {
            case Lever when uri.Host.EndsWith("lever.co", StringComparison.OrdinalIgnoreCase)
                            && !path.EndsWith("/apply", StringComparison.OrdinalIgnoreCase):
                return $"{uri.Scheme}://{uri.Host}{path}/apply";
            case Ashby when uri.Host.EndsWith("ashbyhq.com", StringComparison.OrdinalIgnoreCase)
                            && !path.EndsWith("/application", StringComparison.OrdinalIgnoreCase):
                return $"{uri.Scheme}://{uri.Host}{path}/application";
            case Workable when uri.Host.Equals("jobs.workable.com", StringComparison.OrdinalIgnoreCase)
                               && WorkableView().Match(uri.AbsolutePath) is { Success: true } view:
                return $"https://apply.workable.com/j/{view.Groups[1].Value}/";
            case Workable when uri.Host.Equals("apply.workable.com", StringComparison.OrdinalIgnoreCase)
                               && WorkableHosted().Match(uri.AbsolutePath) is { Success: true } hosted:
                return $"https://apply.workable.com/{hosted.Groups[1].Value}/j/{hosted.Groups[2].Value}/apply/";
            case Breezy when uri.Host.EndsWith("breezy.hr", StringComparison.OrdinalIgnoreCase)
                             && BreezyPosting().IsMatch(uri.AbsolutePath):
                return $"{uri.Scheme}://{uri.Host}{path}/apply";
            default:
                return link;
        }
    }

    /// <summary>
    /// Whether the browser may drive this provider's form. Workday and SuccessFactors never:
    /// applying needs a candidate account with the employer's tenant (a password, email
    /// verification, a multi-step wizard) — the honest outcome is copy-and-open. An
    /// aggregator's listing never: there is no form on it. The unknown long tail only with
    /// the tenant's explicit opt-in.
    /// </summary>
    public static bool BrowserCanSubmit(string provider, bool longTailOptIn, bool linkedInEasyOptIn = false) => provider switch
    {
        // Easy Apply is the tenant's own LinkedIn account being driven: only ever by its own switch (#278).
        LinkedInEasy => linkedInEasyOptIn,
        Greenhouse or Lever or Ashby or Workable or Breezy or SmartRecruiters or Join => true,
        Workday or SuccessFactors or Aggregator => false,
        _ => longTailOptIn,
    };

    [GeneratedRegex(@"^https?://([a-z0-9-]+)\.(wd\d+)\.(myworkdayjobs|myworkdaysite)\.com/(?:[a-z]{2}-[A-Z]{2}/)?([^/?#]+)/(job/[^?#]+)", RegexOptions.IgnoreCase)]
    private static partial Regex WorkdayPosting();

    /// <summary>
    /// The JSON Workday's own page reads a posting from, or null when <paramref name="link"/> is not
    /// a Workday posting. The page itself is a script that renders "The page you are looking for
    /// doesn't exist" under an HTTP 200, so nothing unattended can tell a removed job from a live
    /// one by fetching it — but this answers <b>404</b> for a removed job and 200 for a live one
    /// (checked 2026-09-21: CVS Health and Voya gone, Allstate live). Some tenants refuse it a
    /// bot (Broadridge, 403), which says nothing either way (#277).
    /// </summary>
    public static string? WorkdayJobApi(string link)
    {
        if (WorkdayPosting().Match(link ?? "") is not { Success: true } m) return null;
        var tenant = m.Groups[1].Value.ToLowerInvariant();
        var host = $"{tenant}.{m.Groups[2].Value.ToLowerInvariant()}.{m.Groups[3].Value.ToLowerInvariant()}.com";
        // The site and the job path are case-sensitive on Workday's side: passed through as written.
        return $"https://{host}/wday/cxs/{tenant}/{m.Groups[4].Value}/{m.Groups[5].Value.TrimEnd('/')}";
    }

    /// <summary>
    /// Greenhouse's own copy of the application form for a hosted posting, or null when
    /// <paramref name="link"/> is not one. An employer may point its board at its own careers
    /// site: <c>job-boards.greenhouse.io/fieldwire/jobs/8633653002</c> answers with a redirect
    /// to <c>www.fieldwire.com/job/…</c>, which the browser will not follow off the posting's
    /// site — and there the form is only this same page in a frame. Going to it directly keeps
    /// the run on Greenhouse (#280).
    /// </summary>
    public static string? GreenhouseEmbedForm(string link)
    {
        if (GreenhouseHosted().Match(link ?? "") is not { Success: true } m || !Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return null;
        // The same region the posting is in (job-boards.eu.greenhouse.io); the older
        // boards.* host serves the embed from its job-boards.* twin.
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("boards.", StringComparison.Ordinal)) host = "job-" + host;
        return $"https://{host}/embed/job_app?for={Uri.EscapeDataString(m.Groups[1].Value)}&token={Uri.EscapeDataString(m.Groups[2].Value)}";
    }

    /// <summary>
    /// Greenhouse's own copy of the form for an employer page that embeds a Greenhouse job
    /// (<c>?gh_jid=</c>), with the board read off that page's HTML; null when either is missing.
    /// Bridgeway's careers page renders its embed from a script the browser never ran, so the
    /// run found "no Apply button or link on the page" on a posting whose packet held all 17
    /// Greenhouse questions.
    /// </summary>
    public static string? GreenhouseEmbedFormFor(string link, string html)
    {
        if (GreenhouseEmbed().Match(link ?? "") is not { Success: true } m || FindGreenhouseBoard(html) is not { } board)
            return null;
        // The board's region as the page names it: an EU board is served from job-boards.eu.
        var host = Regex.IsMatch(html, @"(?:job-)?boards\.eu\.greenhouse\.io", RegexOptions.IgnoreCase) ? "job-boards.eu.greenhouse.io" : "job-boards.greenhouse.io";
        return $"https://{host}/embed/job_app?for={Uri.EscapeDataString(board)}&token={m.Groups[1].Value}";
    }

    /// <summary>Board token + job id for a Greenhouse posting, from the hosted URL or an
    /// embed's <c>gh_jid</c> plus the poller's <c>auto:greenhouse:{board}</c> source.</summary>
    public static bool TryParseGreenhouse(string link, string source, out string board, out string jobId)
    {
        board = "";
        jobId = "";
        var hosted = GreenhouseHosted().Match(link ?? "");
        if (hosted.Success)
        {
            board = hosted.Groups[1].Value;
            jobId = hosted.Groups[2].Value;
            return true;
        }
        var embed = GreenhouseEmbed().Match(link ?? "");
        var src = SourceBoard().Match(source ?? "");
        if (embed.Success && src.Success && src.Groups[1].Value.Equals(Greenhouse, StringComparison.OrdinalIgnoreCase))
        {
            board = src.Groups[2].Value;
            jobId = embed.Groups[1].Value;
            return true;
        }
        return false;
    }

    /// <summary>
    /// The Greenhouse board token a company careers page embeds, read from its HTML —
    /// for a <c>?gh_jid=</c> lead that came from anywhere but the tenant's own board list
    /// (an aggregator, a hand-pasted link), where the source carries no board. Null when
    /// the page does not name one.
    /// </summary>
    public static string? FindGreenhouseBoard(string html)
    {
        if (string.IsNullOrEmpty(html)) return null;
        foreach (var re in new[] { GreenhouseEmbedFor(), GreenhouseBoardApi(), GreenhouseBoardLink() })
        {
            var m = re.Match(html);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }
        return null;
    }
}
