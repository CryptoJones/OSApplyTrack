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
}
