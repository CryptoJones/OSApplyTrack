// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The code-side vetoes that run before any tokens are spent. They must fire on
/// plainly stated employer requirements and stay quiet on anything ambiguous.
/// </summary>
public class DisqualifierTests
{
    private static AppFields App(string location = "Remote", string role = "Engineer") =>
        new() { Company = "Acme", Role = role, Location = location };

    [Theory]
    [InlineData("Must hold an active TS/SCI clearance.")]
    [InlineData("Active Secret clearance required")]
    [InlineData("Candidates must have an active security clearance to be considered.")]
    public void A_clearance_requirement_disqualifies_a_candidate_without_one(string posting)
    {
        var reasons = Disqualifiers.Find(App(), posting, new Criteria(), new AgentSettings());
        Assert.Contains(reasons, r => r.Contains("clearance"));

        var cleared = Disqualifiers.Find(App(), posting, new Criteria(), new AgentSettings { ClearanceOk = true });
        Assert.DoesNotContain(cleared, r => r.Contains("clearance"));
    }

    [Fact]
    public void Mentioning_clearance_in_passing_is_not_a_requirement()
    {
        var posting = "Our platform is used by teams working on cleared programs; no clearance is needed for this role.";
        Assert.Empty(Disqualifiers.Find(App(), posting, new Criteria(), new AgentSettings()));
    }

    [Theory]
    [InlineData("We are unable to sponsor visas for this position.")]
    [InlineData("This role does not offer visa sponsorship.")]
    [InlineData("Must be authorized to work in the US without sponsorship.")]
    public void No_sponsorship_only_matters_when_the_candidate_needs_it(string posting)
    {
        var needs = Disqualifiers.Find(App(), posting, new Criteria(), new AgentSettings { NeedsSponsorship = true });
        Assert.Contains(needs, r => r.Contains("sponsor"));

        var doesNot = Disqualifiers.Find(App(), posting, new Criteria(), new AgentSettings());
        Assert.Empty(doesNot);
    }

    [Fact]
    public void An_excluded_location_in_the_location_field_disqualifies()
    {
        var criteria = new Criteria { ExcludeLocations = ["San Francisco"] };
        var reasons = Disqualifiers.Find(App(location: "San Francisco, CA"), "", criteria, new AgentSettings());
        Assert.Contains(reasons, r => r.Contains("San Francisco"));

        // The body naming the place is not enough — an office list is not a requirement.
        var body = Disqualifiers.Find(App(location: "Remote"), "Offices in San Francisco and Austin.", criteria, new AgentSettings());
        Assert.Empty(body);
    }

    [Fact]
    public void An_on_site_role_is_skipped_for_a_remote_only_profile_unless_remote_is_mentioned()
    {
        var remoteOnly = new Criteria { RemoteOnly = true };
        Assert.Contains(
            Disqualifiers.Find(App(location: "Lincoln, NE (on-site)"), "", remoteOnly, new AgentSettings()),
            r => r.Contains("on-site"));
        Assert.Empty(
            Disqualifiers.Find(App(location: "Lincoln, NE"), "Hybrid: on-site two days, remote otherwise.", remoteOnly, new AgentSettings()));
        Assert.Empty(
            Disqualifiers.Find(App(location: "On-site"), "", new Criteria(), new AgentSettings()));
    }

    // The employer's own pages, as the browser rendered them on 2026-09-24. LinkedIn listed
    // both as "(Remote)".
    private const string DtccPage = "Do you want to work on innovative projects? At DTCC, we are at the forefront of innovation.\n"
        + "DTCC offers a flexible/hybrid model of 3 days onsite and 2 days remote (onsite Tuesdays, Wednesdays and a third day unique to each team or employee).\n"
        + "The salary range is indicative for roles at the same level within DTCC across all US locations.";

    private static readonly string WingstopPage = "Staff Engineer - .Net / C#\nDallas, TX, United States\n"
        + string.Concat(Enumerable.Repeat("Design and own distributed backend systems using .NET / C# in a cloud-native AWS environment. ", 16))
        + "\nLunch provided every Tuesday and Thursday in office\nOnsite game room\nLocations\n2801 N Central Expressway, Dallas, TX, 75204, US";

    [Fact]
    public void An_employer_page_that_says_hybrid_contradicts_a_remote_listing()
    {
        var why = Disqualifiers.EmployerContradictsRemote(DtccPage, new Criteria { RemoteOnly = true });
        Assert.NotNull(why);
        Assert.Contains("hybrid model of 3 days onsite", why);
        Assert.Null(Disqualifiers.EmployerContradictsRemote(DtccPage, new Criteria { RemoteOnly = false }));
    }

    [Fact]
    public void A_whole_employer_posting_that_never_says_remote_contradicts_a_remote_listing()
    {
        // "distributed backend systems" is not remote; "in office" twice-weekly lunch is no statement.
        var why = Disqualifiers.EmployerContradictsRemote(WingstopPage, new Criteria { RemoteOnly = true });
        Assert.Equal("the employer's own posting never says the role is remote — the profile is remote-only", why);
    }

    [Theory]
    [InlineData("This role is fully remote within the US. " + "We build hybrid cloud platforms for regulated industries. ")]
    [InlineData("Work from anywhere in the United States. Our hybrid cloud team ships weekly. ")]
    public void A_remote_posting_is_never_held_against_the_role(string sentence)
    {
        var page = string.Concat(Enumerable.Repeat(sentence, 20));
        Assert.Null(Disqualifiers.EmployerContradictsRemote(page, new Criteria { RemoteOnly = true }));
    }

    [Theory]
    [InlineData("This is a hybrid role based in Austin.")]
    [InlineData("You will work 4 days a week in the office.")]
    [InlineData("This position is on-site in Denver.")]
    [InlineData("This is not a remote position.")]
    public void Plain_statements_that_the_role_is_not_remote_are_caught(string sentence) =>
        Assert.NotNull(Disqualifiers.EmployerContradictsRemote(sentence, new Criteria { RemoteOnly = true }));

    [Fact]
    public void A_page_too_short_to_judge_is_never_held_against_the_role() =>
        Assert.Null(Disqualifiers.EmployerContradictsRemote("Loading…", new Criteria { RemoteOnly = true }));

    [Fact]
    public void A_hybrid_schedule_in_other_words_is_caught_and_a_hybrid_model_of_something_is_not()
    {
        var remoteOnly = new Criteria { RemoteOnly = true };
        Assert.NotNull(Disqualifiers.EmployerContradictsRemote("Hybrid — three days at our office, two days remote.", remoteOnly));
        // A model built, not a way of working.
        var fraud = string.Concat(Enumerable.Repeat("Fully remote role. Build a hybrid model for fraud detection. ", 30));
        Assert.Null(Disqualifiers.EmployerContradictsRemote(fraud, remoteOnly));
    }

    [Fact]
    public void Remote_said_of_a_tool_is_not_remote_said_of_the_job()
    {
        var page = string.Concat(Enumerable.Repeat("You will build remote access tools and distributed systems in our Dallas office. ", 25));
        Assert.NotNull(Disqualifiers.EmployerContradictsRemote(page, new Criteria { RemoteOnly = true }));
    }

    [Fact]
    public void Silence_in_text_that_may_have_been_cut_short_proves_nothing() =>
        Assert.Null(Disqualifiers.EmployerContradictsRemote(WingstopPage, new Criteria { RemoteOnly = true }, complete: false));

    [Theory]
    [InlineData("https://job-boards.eu.greenhouse.io/neoris/jobs/4904857101", "United States", true)]
    [InlineData("https://boards.eu.greenhouse.io/acme/jobs/1", "USA", true)]
    [InlineData("https://job-boards.greenhouse.io/acme/jobs/1", "United States", false)]
    [InlineData("https://job-boards.eu.greenhouse.io/neoris/jobs/4904857101", "Germany", false)]
    [InlineData("https://job-boards.eu.greenhouse.io/neoris/jobs/4904857101", "", false)]
    public void Greenhouses_EU_board_is_not_a_US_job(string link, string country, bool disqualified)
    {
        var app = new AppFields { Company = "NEORIS", Role = ".NET Developer", Location = "Remote", Link = link };
        var reasons = Disqualifiers.Find(app, "", new Criteria(), new AgentSettings { Country = country });
        Assert.Equal(disqualified, reasons.Any(r => r.Contains("EU job board")));
    }

    [Fact]
    public void A_description_that_never_says_remote_is_not_judged_when_it_is_not_the_whole_posting()
    {
        // Bankjoy's Ashby posting is marked Remote in its location field; the description never
        // says so, and it was passed for that (#334). The worker now asks without silence.
        var page = string.Concat(Enumerable.Repeat("Build and ship full-stack .NET features for credit unions. ", 40));
        Assert.Null(Disqualifiers.EmployerContradictsRemote(page, new Criteria { RemoteOnly = true }, complete: false));
        // An explicit statement still counts.
        Assert.NotNull(Disqualifiers.EmployerContradictsRemote(page + " This role is hybrid (Mon through Thu on-site / Fri remote).", new Criteria { RemoteOnly = true }, complete: false));
    }
}
