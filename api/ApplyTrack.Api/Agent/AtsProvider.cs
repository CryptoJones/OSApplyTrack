// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Which applicant-tracking system a lead's posting lives on. The poller already
/// stamps <c>source = auto:greenhouse:{board}</c> / <c>auto:lever:{site}</c> on leads it
/// found through a tenant's ATS boards — that is trusted over URL sniffing when
/// present. Only Greenhouse publishes its application form without an employer key,
/// so only Greenhouse gets a real field map here; everything else falls back to the
/// standard question set and the copy-and-open path.
/// </summary>
public static partial class AtsProvider
{
    public const string Greenhouse = "greenhouse";
    public const string Lever = "lever";
    public const string Ashby = "ashby";
    public const string Workday = "workday";
    public const string Unknown = "unknown";

    [GeneratedRegex(@"^auto:(greenhouse|lever):([a-z0-9][a-z0-9._-]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex SourceBoard();

    // boards.greenhouse.io/{board}/jobs/{id}, job-boards.greenhouse.io/..., EU variants.
    [GeneratedRegex(@"^https?://(?:job-)?boards(?:\.eu)?\.greenhouse\.io/([a-z0-9][a-z0-9._-]*)/jobs/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseHosted();

    // A company-hosted board embed: any page with ?gh_jid=123 (board from the source).
    [GeneratedRegex(@"[?&]gh_jid=(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GreenhouseEmbed();

    public static string Detect(string link, string source)
    {
        var m = SourceBoard().Match(source ?? "");
        if (m.Success)
            return m.Groups[1].Value.ToLowerInvariant();

        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri))
            return Unknown;
        var host = uri.Host.ToLowerInvariant();
        if (host.EndsWith("greenhouse.io") || GreenhouseEmbed().IsMatch(uri.Query))
            return Greenhouse;
        if (host.EndsWith("lever.co"))
            return Lever;
        if (host.EndsWith("ashbyhq.com"))
            return Ashby;
        if (host.EndsWith("myworkdayjobs.com") || host.EndsWith("myworkdaysite.com"))
            return Workday;
        return Unknown;
    }

    /// <summary>
    /// The page the application form is on. Lever and Ashby post their form one hop
    /// past the posting (<c>/apply</c>, <c>/application</c>); everyone else's is the
    /// posting itself (Greenhouse hosted pages carry the form; an embed reveals it).
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
            default:
                return link;
        }
    }

    /// <summary>
    /// Whether the browser may drive this provider's form. Workday never: applying
    /// needs an account with the employer's tenant, email verification and a
    /// multi-step wizard — the honest outcome is copy-and-open. The unknown long tail
    /// only with the tenant's explicit opt-in.
    /// </summary>
    public static bool BrowserCanSubmit(string provider, bool longTailOptIn) => provider switch
    {
        Greenhouse or Lever or Ashby => true,
        Workday => false,
        _ => longTailOptIn,
    };

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
}
