// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Greenhouse's Education block, filled from the résumé (#332): which education goes in, and
/// which of Greenhouse's options names it. The résumé is the owner's, as saved on 2026-09-24.
/// </summary>
public class GreenhouseEducationTests
{
    private static readonly List<ResumeEducation> Owners =
    [
        new("Eastern University", "MS", "Applied Artificial Intelligence (In Progress)", "2026 – Present"),
        new("University of Maryland University College", "Bachelor of Science", "Computer and Information Science", ""),
        new("Rochester Institute of Technology", "MicroMaster’s", "Cybersecurity", ""),
        new("Kennesaw State University", "Graduate Certificate", "Computer Science", ""),
    ];

    [Fact]
    public void The_highest_completed_degree_goes_first_not_one_in_progress()
    {
        var e = BrowserSubmitter.HighestCompleted(Owners);
        Assert.Equal("University of Maryland University College", e!.School);
    }

    [Theory]
    [InlineData("Bachelor of Science", "Bachelor's Degree")]
    [InlineData("BS", "Bachelor's Degree")]
    [InlineData("MS", "Master's Degree")]
    [InlineData("M.S.", "Master's Degree")]
    [InlineData("Master of Science", "Master's Degree")]
    [InlineData("MBA", "Master of Business Administration (M.B.A.)")]
    [InlineData("Ph.D.", "Doctor of Philosophy (Ph.D.)")]
    [InlineData("Graduate Certificate", null)]
    public void A_resume_degree_maps_to_greenhouses_option(string degree, string? option) =>
        Assert.Equal(option, BrowserSubmitter.DegreeOption(degree));

    [Fact]
    public void The_option_that_names_the_same_school_wins_over_its_neighbours()
    {
        const string target = "University of Maryland University College";
        var options = new[] { "University of Maryland - Baltimore", "University of Maryland - College Park", "University of Maryland - University College" };
        var best = options.OrderByDescending(o => BrowserSubmitter.OptionScore(o, target)).First();
        Assert.Equal("University of Maryland - University College", best);
        Assert.Equal(0, BrowserSubmitter.OptionScore("University of Maryland - Baltimore", target));
    }

    [Fact]
    public void A_discipline_takes_the_option_whose_words_it_holds()
    {
        Assert.True(BrowserSubmitter.OptionScore("Computer Science", "Computer and Information Science") > 0);
        Assert.Equal(0, BrowserSubmitter.OptionScore("Computer Engineering", "Computer and Information Science"));
        Assert.True(BrowserSubmitter.OptionScore("Computer and Information Science", "Computer and Information Science")
            > BrowserSubmitter.OptionScore("Computer Science", "Computer and Information Science"));
    }

    [Theory]
    [InlineData("2004 – 2008", "2008")]
    [InlineData("May 2012", "2012")]
    [InlineData("2026 – Present", null)]
    [InlineData("", null)]
    public void Only_a_stated_finish_year_is_an_end_year(string dates, string? year) =>
        Assert.Equal(year, BrowserSubmitter.EndYear(dates));

    [Theory]
    [InlineData("5+", "5")]
    [InlineData("10+ years", "10")]
    [InlineData("3-5", "3")]
    [InlineData("07", "7")]
    [InlineData("none", null)]
    public void A_linkedin_number_box_gets_the_whole_number_an_answer_states(string answer, string? number) =>
        Assert.Equal(number, ApplyTrack.Api.Agent.AnswerDrafter.WholeNumber(answer));

    [Fact]
    public void A_degree_in_progress_goes_before_a_completed_certificate()
    {
        var e = BrowserSubmitter.HighestCompleted([
            new("Kennesaw State University", "Graduate Certificate", "Computer Science", "2019"),
            new("Eastern University", "MS", "Applied Artificial Intelligence (In Progress)", "2026 – Present"),
        ]);
        Assert.Equal("Eastern University", e!.School);
    }

    [Fact]
    public void A_number_too_long_for_an_int_is_still_its_digits() =>
        Assert.Equal("123456789012345678901234567890", ApplyTrack.Api.Agent.AnswerDrafter.WholeNumber("123456789012345678901234567890+"));
}
