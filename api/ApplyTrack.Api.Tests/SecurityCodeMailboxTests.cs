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

    private const string JoinCodeText = """
        Hi Ada,

        Your code is 482913

        Enter it on the page to continue your application at Piston.

        join.com · https://join.com/privacy · https://join.com/unsubscribe?u=1
        """;

    private const string JoinLinkText = """
        Hi Ada,

        Click here to continue your application: https://join.com/apply/verify?token=abc123.

        Or copy this code: 482913

        join.com · https://join.com/privacy · https://join.com/unsubscribe?u=1
        """;

    [Fact]
    public void A_sign_in_mails_code_is_read_when_it_has_no_link_to_offer() =>
        Assert.Equal("482913", ImapSecurityCodeSource.ExtractSignIn(JoinCodeText, "join.com"));

    [Fact]
    public void A_sign_in_mails_link_on_the_boards_host_is_preferred_and_the_footers_links_never_taken()
    {
        Assert.Equal("https://join.com/apply/verify?token=abc123", ImapSecurityCodeSource.ExtractSignIn(JoinLinkText, "join.com"));
        Assert.Equal("https://join.com/apply/verify?token=abc123", ImapSecurityCodeSource.ExtractSignIn(JoinLinkText, "www.join.com"));
        // A link on some other host is never the answer, whatever it says.
        Assert.Equal("482913", ImapSecurityCodeSource.ExtractSignIn(JoinLinkText.Replace("join.com/apply", "evil.example/apply"), "join.com"));
        Assert.Null(ImapSecurityCodeSource.ExtractSignIn("Welcome! https://join.com/privacy https://join.com/help", "join.com"));
    }

    [Theory]
    [InlineData("mail.join.com", "join.com", true)]
    [InlineData("join.com", "www.join.com", true)]
    [InlineData("notjoin.com", "join.com", false)]
    [InlineData("evil.example", "join.com", false)]
    public void The_boards_host_covers_its_subdomains_and_nothing_else(string host, string board, bool expected) =>
        Assert.Equal(expected, ImapSecurityCodeSource.OnBoard(host, board));

    [Theory]
    [InlineData("Security code for your application to Aperia", "Aperia", true)]
    [InlineData("Security code for your application to GitLab", "GitLab Inc.", true)]
    [InlineData("Security code for your application to Miris", "Aperia", false)]
    [InlineData("Thank you for applying to Aperia", "Aperia", false)]
    public void The_subject_must_be_a_security_code_for_this_company(string subject, string company, bool expected) =>
        Assert.Equal(expected, ImapSecurityCodeSource.SubjectMatches(subject, company));
}
