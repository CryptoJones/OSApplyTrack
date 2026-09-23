// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Materials;
using Xunit;

namespace ApplyTrack.Api.Tests;

/// <summary>Education read from an uploaded résumé's EDUCATION section (#277).</summary>
public class ResumeEducationTests
{
    // The owner's own section, as the PDF text extractor lays it out.
    private const string Text = """
        EXPERIENCE
        Ronin 48 · Principal Software Architect

        EDUCATION
         Eastern University                                                                        2026 – Present
         MS, Applied Artificial Intelligence (In Progress)
         University of Maryland University College
         Bachelor of Science, Computer and Information Science
         Rochester Institute of Technology
         MicroMaster’s in Cybersecurity
         Graduate & Professional Certificates
         Computer Science (Kennesaw State); IT Project Management (Colorado State); Unix System Administration
         (UMUC)

        VOLUNTEER EXPERIENCE
         Community & Industry Service                                                              2021 - Present
        """;

    [Fact]
    public void Each_school_is_paired_with_the_degree_under_it_and_its_dates()
    {
        var found = ResumePdfImporter.EducationFrom(Text);

        Assert.Equal(3, found.Count);
        Assert.Equal(new ResumeEducation("Eastern University", "MS", "Applied Artificial Intelligence (In Progress)", "2026 – Present"), found[0]);
        Assert.Equal(new ResumeEducation("University of Maryland University College", "Bachelor of Science", "Computer and Information Science", ""), found[1]);
        Assert.Equal(new ResumeEducation("Rochester Institute of Technology", "MicroMaster’s", "Cybersecurity", ""), found[2]);
    }

    [Fact]
    public void A_resume_with_no_education_heading_has_none_and_the_brief_lists_what_there_is()
    {
        Assert.Empty(ResumePdfImporter.EducationFrom("EXPERIENCE\nAcme · Engineer"));
        var brief = new Resume { Education = [new("Eastern University", "MS", "Applied AI", "2026 – Present")] }.ToBrief();
        Assert.Contains("Education:", brief);
        Assert.Contains("- MS · Applied AI · Eastern University · 2026 – Present", brief);
    }
}
