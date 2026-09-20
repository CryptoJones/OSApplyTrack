// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Which ATS a link is on, where its form is, and whether the browser may drive it —
/// the long-tail boards whose Apply leads somewhere else (#180) and the aggregator
/// listings that have no form at all (#191).
/// </summary>
public class AtsProviderTests
{
    [Theory]
    [InlineData("https://jobs.workable.com/view/abc123XYZ/senior-engineer", "", AtsProvider.Workable)]
    [InlineData("https://apply.workable.com/crewbloom/j/ABC123/", "", AtsProvider.Workable)]
    [InlineData("https://voyagu.breezy.hr/p/1a2b3c4d5e6f", "", AtsProvider.Breezy)]
    [InlineData("https://jobs.smartrecruiters.com/SoftwareMind/744000012345-senior-net-developer", "", AtsProvider.SmartRecruiters)]
    [InlineData("https://join.com/companies/bechange/14567890-consultant", "", AtsProvider.Join)]
    [InlineData("https://remoteok.com/remote-jobs/remote-senior-engineer-creative-fabrica-123", "auto:remoteok", AtsProvider.Aggregator)]
    [InlineData("https://remotive.com/remote-jobs/software-dev/senior-engineer-987", "auto:remotive", AtsProvider.Aggregator)]
    [InlineData("https://weworkremotely.com/remote-jobs/acme-senior-engineer", "auto:weworkremotely", AtsProvider.Aggregator)]
    [InlineData("https://news.ycombinator.com/item?id=41234567", "auto:hn", AtsProvider.Aggregator)]
    [InlineData("https://www.linkedin.com/jobs/view/4464862474", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://jobright.ai/jobs/info/b2b_1769670052353_604?utm_source=5012", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://www.sundayy.com/job-description/us1E83527B7B0B0D02B7BEFC83FD25AD65?source=Linkedin", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://us.thebigjobsite.com/redirectjob?id=1E83&source=sundayyapius", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://www.fetchjobs.co/job-description-usb/DBE262F9F85264450F4C41420DB74AAC?src=LinkedIn", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://lensa.com/software-engineer-jobs-near-me-in-lafayette-co/cpc-hl-v3/8bfb", "auto:linkedin", AtsProvider.Aggregator)]
    [InlineData("https://careers.stryker.com/software-engineer-cloud/job/62F1?source=LinkedIn", "auto:linkedin", AtsProvider.Unknown)]
    [InlineData("https://www.assuresoft.com/careers/senior-engineer?gh_jid=4567890", "auto:remoteok", AtsProvider.Greenhouse)]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123", "", AtsProvider.Greenhouse)]
    [InlineData("https://jobs.lever.co/acme/1234-abcd", "", AtsProvider.Lever)]
    [InlineData("https://career4.successfactors.com/careers?company=Kiewit", "", AtsProvider.SuccessFactors)]
    [InlineData("https://jobs.sapsf.eu/career?company=acme", "", AtsProvider.SuccessFactors)]
    // A SuccessFactors career site on the employer's own domain is not knowable from the link:
    // the browser learns it when Apply leads to career*.successfactors.com (#214).
    [InlineData("https://kiewitcareers.kiewit.com/job/Omaha-Sr-AI-Engineer-NE-68046/1410311300/", "", AtsProvider.Unknown)]
    [InlineData("https://careers.example.com/jobs/1", "", AtsProvider.Unknown)]
    public void Detects_the_long_tail_boards_and_the_aggregators(string link, string source, string expected) =>
        Assert.Equal(expected, AtsProvider.Detect(link, source));

    [Theory]
    // A job-finder link goes through Workable's shortlink, which redirects to the account's page.
    [InlineData("https://jobs.workable.com/view/abc123XYZ/senior-engineer", "https://apply.workable.com/j/abc123XYZ/")]
    [InlineData("https://apply.workable.com/crewbloom/j/ABC123/", "https://apply.workable.com/crewbloom/j/ABC123/apply/")]
    [InlineData("https://apply.workable.com/crewbloom/j/ABC123/apply/", "https://apply.workable.com/crewbloom/j/ABC123/apply/")]
    [InlineData("https://voyagu.breezy.hr/p/1a2b3c4d5e6f", "https://voyagu.breezy.hr/p/1a2b3c4d5e6f/apply")]
    [InlineData("https://voyagu.breezy.hr/p/1a2b3c4d5e6f/apply", "https://voyagu.breezy.hr/p/1a2b3c4d5e6f/apply")]
    [InlineData("https://jobs.lever.co/acme/1234-abcd", "https://jobs.lever.co/acme/1234-abcd/apply")]
    [InlineData("https://jobs.ashbyhq.com/acme/1234", "https://jobs.ashbyhq.com/acme/1234/application")]
    // SmartRecruiters and join.com open their form behind the Apply click, on the posting.
    [InlineData("https://jobs.smartrecruiters.com/SoftwareMind/744000012345-senior", "https://jobs.smartrecruiters.com/SoftwareMind/744000012345-senior")]
    [InlineData("https://join.com/companies/bechange/14567890-consultant", "https://join.com/companies/bechange/14567890-consultant")]
    public void The_apply_url_is_the_forms_page(string link, string expected) =>
        Assert.Equal(expected, AtsProvider.ApplyUrl(link, AtsProvider.Detect(link, "")));

    [Fact]
    public void The_browser_drives_the_discovered_boards_and_never_an_aggregator_or_workday()
    {
        foreach (var p in new[] { AtsProvider.Greenhouse, AtsProvider.Lever, AtsProvider.Ashby, AtsProvider.Workable, AtsProvider.Breezy, AtsProvider.SmartRecruiters, AtsProvider.Join })
            Assert.True(AtsProvider.BrowserCanSubmit(p, longTailOptIn: false), p);
        Assert.False(AtsProvider.BrowserCanSubmit(AtsProvider.Workday, longTailOptIn: true));
        Assert.False(AtsProvider.BrowserCanSubmit(AtsProvider.SuccessFactors, longTailOptIn: true));
        Assert.False(AtsProvider.BrowserCanSubmit(AtsProvider.Aggregator, longTailOptIn: true));
        Assert.False(AtsProvider.BrowserCanSubmit(AtsProvider.Unknown, longTailOptIn: false));
        Assert.True(AtsProvider.BrowserCanSubmit(AtsProvider.Unknown, longTailOptIn: true));
    }

    [Theory]
    [InlineData("""<script src="https://boards.greenhouse.io/embed/job_board/js?for=assuresoft"></script>""", "assuresoft")]
    [InlineData("""<iframe src="https://boards.greenhouse.io/embed/job_app?for=acme&token=4567890&b=https%3A%2F%2Fwww.acme.com"></iframe>""", "acme")]
    [InlineData("""fetch("https://boards-api.greenhouse.io/v1/boards/globex/jobs/1")""", "globex")]
    [InlineData("""<a href="https://job-boards.greenhouse.io/initech/jobs/5">all jobs</a>""", "initech")]
    [InlineData("""<p>We use Greenhouse.</p>""", null)]
    [InlineData("", null)]
    public void The_board_token_is_read_off_a_careers_page_that_embeds_greenhouse(string html, string? expected) =>
        Assert.Equal(expected, AtsProvider.FindGreenhouseBoard(html));

    [Fact]
    public void A_gh_jid_link_with_the_board_from_the_page_parses_like_a_hosted_one()
    {
        Assert.False(AtsProvider.TryParseGreenhouse("https://www.assuresoft.com/careers/x?gh_jid=4567890", "auto:remoteok", out _, out _));
        Assert.True(AtsProvider.TryParseGreenhouse("https://www.assuresoft.com/careers/x?gh_jid=4567890", "auto:greenhouse:assuresoft", out var board, out var id));
        Assert.Equal(("assuresoft", "4567890"), (board, id));
    }

    [Theory]
    [InlineData("https://www.bestjobtool.com/job-description-usb/3D6EACBA24A0C358CF1D5D1DF544AAB0?src=LinkedIn", true)]
    [InlineData("https://www.fetchjobs.co/job-description-usb/DBE262F9F85264450F4C41420DB74AAC?src=LinkedIn", true)]
    [InlineData("https://us.thebigjobsite.com/redirectjob?id=3D6E", true)]
    [InlineData("https://sundayy.com/jobs/1", true)]
    // Where the chain ends is a board a person can sign up to: apply-by-hand, not retired.
    [InlineData("https://lensa.com/job/1", false)]
    [InlineData("https://www.linkedin.com/jobs/view/4457796892", false)]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/1", false)]
    [InlineData("https://notbestjobtool.com/x", false)]
    [InlineData("", false)]
    public void A_dead_end_aggregator_is_told_from_an_apply_by_hand_one(string link, bool expected) =>
        Assert.Equal(expected, AtsProvider.IsDeadEndLink(link));

    [Theory]
    [InlineData("https://job-boards.greenhouse.io/fieldwire/jobs/8633653002", "https://job-boards.greenhouse.io/embed/job_app?for=fieldwire&token=8633653002")]
    [InlineData("https://boards.greenhouse.io/acme/jobs/42?gh_src=x", "https://job-boards.greenhouse.io/embed/job_app?for=acme&token=42")]
    [InlineData("https://job-boards.eu.greenhouse.io/acme/jobs/42", "https://job-boards.eu.greenhouse.io/embed/job_app?for=acme&token=42")]
    [InlineData("https://www.fieldwire.com/job/8633653002/?gh_jid=8633653002", null)]
    [InlineData("https://jobs.lever.co/acme/1", null)]
    [InlineData("", null)]
    public void A_hosted_greenhouse_posting_has_greenhouses_own_copy_of_its_form(string link, string? expected) =>
        Assert.Equal(expected, AtsProvider.GreenhouseEmbedForm(link));

    [Theory]
    [InlineData("careers-mheducation.icims.com", "iCIMS")]
    [InlineData("uhg.taleo.net", "Oracle Taleo")]
    [InlineData("allstate.wd5.myworkdayjobs.com", "Workday")]
    [InlineData("career41.sapsf.com", "SAP SuccessFactors")]
    [InlineData("jobs.lever.co", null)]
    public void An_ats_that_only_takes_applications_from_an_account_is_named(string host, string? expected) =>
        Assert.Equal(expected, ApplyTrack.Api.Agent.Browser.BrowserSession.AccountOnlyAts(host));

    [Theory]
    // Refused outright, and named.
    [InlineData("career41.sapsf.com", "", "Apply leads to career41.sapsf.com (SAP SuccessFactors), which only takes")]
    [InlineData("evil.example", "", "Apply led off the posting's site to evil.example")]
    // Let through — it hosts forms — and landed on: where the run ended up says what it needs.
    [InlineData("", "allstate.wd5.myworkdayjobs.com", "Apply leads to allstate.wd5.myworkdayjobs.com (Workday), which only takes")]
    [InlineData("", "uhg.taleo.net", "Apply leads to uhg.taleo.net (Oracle Taleo), which only takes")]
    [InlineData("", "careers.example.com", "Apply was clicked but no form appeared within 10 s")]
    public void A_run_that_found_no_form_says_where_apply_led(string refused, string landed, string expected) =>
        Assert.StartsWith(expected, ApplyTrack.Api.Agent.Browser.BrowserSession.NoFormNote(refused.Length > 0 ? [refused] : [], landed));
}
