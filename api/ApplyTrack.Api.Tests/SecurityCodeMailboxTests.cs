// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Data;
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
    public void Mygreenhouses_sign_in_wording_yields_the_code_between_the_stars()
    {
        Assert.Equal("szo8ve06", ImapSecurityCodeSource.ExtractCode("Hi Aaron,\n\nYour security code is:\n\n******** szo8ve06 ********\n\nEnter this code on the sign-in page."));
        Assert.Equal("szo8ve06", ImapSecurityCodeSource.ExtractSignIn("Your security code is: **** szo8ve06 ****\n\nhttps://my.greenhouse.io/users/sign_in", "my.greenhouse.io"));
        Assert.Null(ImapSecurityCodeSource.ExtractCode("Your security code is expiring soon."));
    }

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
    [InlineData("ebxr.fa.us2.oraclecloud.com", true)]
    [InlineData("iaxmqy.fa.ocs.oraclecloud.com", true)]
    [InlineData("join.com", false)]
    [InlineData("my.greenhouse.io", false)]
    public void Oracle_tenants_share_one_sender(string board, bool shared) =>
        Assert.Equal(shared, ImapSecurityCodeSource.SharedSender(board));

    [Fact]
    public void A_shared_senders_mail_is_told_apart_by_the_employer_it_names()
    {
        // Two Oracle runs two minutes apart: Wingstop's read DTCC's code (#318).
        const string dtcc = "The Depository Trust & Clearing Corporation (DTCC)";
        const string wingstop = "Wingstop Restaurants Inc.";
        var dtccMail = "Verify your email\nThank you for your interest in DTCC. Your verification code is 482913.";
        var wingstopMail = "Wingstop Careers: your one-time code is 771204";
        Assert.True(ImapSecurityCodeSource.NamesCompany(dtccMail, dtcc));
        Assert.False(ImapSecurityCodeSource.NamesCompany(dtccMail, wingstop));
        Assert.True(ImapSecurityCodeSource.NamesCompany(wingstopMail, wingstop));
        Assert.False(ImapSecurityCodeSource.NamesCompany(wingstopMail, dtcc));
        Assert.True(ImapSecurityCodeSource.NamesCompany("the Depository Trust sign-in", dtcc));
        // A whole word only: "Dragos" is not in "Dragoslav".
        Assert.False(ImapSecurityCodeSource.NamesCompany("Hello Dragoslav, your code is 123456", "Dragos, Inc."));
        Assert.True(ImapSecurityCodeSource.NamesCompany("anything", "The Inc."));
    }

    [Fact]
    public void The_mailbox_pins_its_dial_to_public_addresses_and_never_a_private_one()
    {
        // A rebinding server can answer public at check time and private at connect time. The
        // mailbox now resolves once and pins the dial to the public addresses of that one
        // resolution, so whatever the name resolves to, no private address is ever handed to the
        // socket — the check-then-connect window is gone (#231).
        var publicV4 = IPAddress.Parse("93.184.216.34");
        var publicV6 = IPAddress.Parse("2606:2800:220:1:248:1893:25c8:1946");
        var metadata = IPAddress.Parse("169.254.169.254"); // cloud metadata, link-local
        var loopback = IPAddress.Parse("127.0.0.1");
        var rfc1918 = IPAddress.Parse("10.1.2.3");

        var dialable = ImapSecurityCodeSource.PublicAddressesOrThrow([publicV4, metadata, publicV6, loopback, rfc1918]);

        Assert.Equal(new[] { publicV4, publicV6 }, dialable);
        Assert.DoesNotContain(metadata, dialable);
        Assert.DoesNotContain(loopback, dialable);
        Assert.DoesNotContain(rfc1918, dialable);

        // The rebind's second answer — nothing public — resolves to nothing dialable, so the
        // connection is refused rather than made to an internal service.
        Assert.Throws<AppValidationException>(() =>
            ImapSecurityCodeSource.PublicAddressesOrThrow([metadata, loopback, rfc1918]));
        Assert.Throws<AppValidationException>(() =>
            ImapSecurityCodeSource.PublicAddressesOrThrow([]));
    }

    [Theory]
    [InlineData("Security code for your application to Aperia", "Aperia", true)]
    [InlineData("Security code for your application to GitLab", "GitLab Inc.", true)]
    [InlineData("Security code for your application to Dragos", "Dragos, Inc.", true)]   // the comma kept it unread
    [InlineData("Security code for your application to Miris", "Aperia", false)]
    [InlineData("Thank you for applying to Aperia", "Aperia", false)]
    public void The_subject_must_be_a_security_code_for_this_company(string subject, string company, bool expected) =>
        Assert.Equal(expected, ImapSecurityCodeSource.SubjectMatches(subject, company));
}
