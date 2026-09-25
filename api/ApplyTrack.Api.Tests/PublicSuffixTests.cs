// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Domains;

namespace ApplyTrack.Api.Tests;

public class PublicSuffixTests
{
    [Theory]
    [InlineData("careers.acme.com", "acme.com")]
    [InlineData("acme.com", "acme.com")]
    [InlineData("careers.acme.co.uk", "acme.co.uk")]
    [InlineData("a.b.acme.com.au", "acme.com.au")]
    [InlineData("acme.azurewebsites.net", "acme.azurewebsites.net")]   // PRIVATE section
    [InlineData("www.acme.github.io", "acme.github.io")]
    [InlineData("acme.herokuapp.com", "acme.herokuapp.com")]
    [InlineData("CAREER4.SuccessFactors.com.", "successfactors.com")]
    [InlineData("a.b.ck", "a.b.ck")]      // wildcard rule *.ck
    [InlineData("www.ck", "www.ck")]      // exception rule !www.ck
    [InlineData("foo.unlistedtld", "foo.unlistedtld")]   // the default rule "*"
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("xn--85x722f.xn--55qx5d.cn", "xn--85x722f.xn--55qx5d.cn")]   // punycode host, Unicode rule 公司.cn
    public void A_hosts_registrable_domain_comes_from_the_public_suffix_list(string host, string expected) =>
        Assert.Equal(expected, PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("co.uk")]
    [InlineData("com.au")]
    [InlineData("github.io")]
    [InlineData("azurewebsites.net")]
    [InlineData("com")]
    [InlineData("b.ck")]
    [InlineData("")]
    public void A_public_suffix_has_no_registrable_domain(string host) =>
        Assert.Null(PublicSuffix.RegistrableDomain(host));

    [Theory]
    [InlineData("jobs.acme.co.uk", "careers.acme.co.uk", true)]
    [InlineData("evil.co.uk", "careers.acme.co.uk", false)]
    [InlineData("evil.com.au", "acme.com.au", false)]
    [InlineData("evil.azurewebsites.net", "acme.azurewebsites.net", false)]
    [InlineData("evil.github.io", "acme.github.io", false)]
    [InlineData("jobs.acme.com", "careers.acme.com", true)]
    [InlineData("boards.greenhouse.io", "careers.acme.co.uk", true)]   // a listed ATS is always allowed
    public void Top_level_navigation_stays_on_the_postings_registrable_domain(string host, string original, bool expected) =>
        Assert.Equal(expected, BrowserSession.HostAllowed(host, original));
}
