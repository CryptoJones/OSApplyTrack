// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Notifications;

namespace ApplyTrack.Api.Tests;

/// <summary>The code comes out of Greenhouse's email as it is actually written.</summary>
public class SecurityCodeMailboxTests
{
    private const string GreenhouseText = """
        Hi Aaron,

        Copy and paste this code into the security code field on your application:

        w6Gx6jHG

        After you enter the code, resubmit your application.

        Regards,
        Greenhouse
        """;

    [Fact]
    public void The_code_is_read_from_greenhouses_wording_or_from_a_bare_line() =>
        Assert.Equal("w6Gx6jHG", ImapSecurityCodeSource.ExtractCode(GreenhouseText));

    [Fact]
    public void Html_wording_on_one_line_still_yields_the_code() =>
        Assert.Equal("HpEq0DVP", ImapSecurityCodeSource.ExtractCode("Copy and paste this code into the security code field on your application: HpEq0DVP After you enter the code, resubmit your application."));

    [Fact]
    public void Prose_without_a_code_yields_nothing()
    {
        Assert.Null(ImapSecurityCodeSource.ExtractCode("Thank you for applying to the AI Engineer role at Aperia!\n\nRegards,\nGreenhouse\n"));
        Assert.Null(ImapSecurityCodeSource.ExtractCode(""));
    }

    [Theory]
    [InlineData("Security code for your application to Aperia", "Aperia", true)]
    [InlineData("Security code for your application to GitLab", "GitLab Inc.", true)]
    [InlineData("Security code for your application to Miris", "Aperia", false)]
    [InlineData("Thank you for applying to Aperia", "Aperia", false)]
    public void The_subject_must_be_a_security_code_for_this_company(string subject, string company, bool expected) =>
        Assert.Equal(expected, ImapSecurityCodeSource.SubjectMatches(subject, company));
}
