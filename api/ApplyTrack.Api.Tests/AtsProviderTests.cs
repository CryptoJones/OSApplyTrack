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
    [InlineData("https://www.assuresoft.com/careers/senior-engineer?gh_jid=4567890", "auto:remoteok", AtsProvider.Greenhouse)]
    [InlineData("https://boards.greenhouse.io/acme/jobs/123", "", AtsProvider.Greenhouse)]
    [InlineData("https://jobs.lever.co/acme/1234-abcd", "", AtsProvider.Lever)]
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
}
