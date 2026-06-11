// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Scrape;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Unit tests for the Autofill scrape pipeline: the JSON-LD/OpenGraph parser is pure
/// (HTML in, fields out), and the fetcher's SSRF gates (URL validation + the
/// blocked-address predicate) are static so they're testable without a network.
/// </summary>
public class ScrapeTests
{
    private static readonly Uri BoardsUrl = new("https://boards.greenhouse.io/acme/jobs/123");

    // ---- Tier 1: JSON-LD JobPosting -----------------------------------------

    [Fact]
    public void Parses_a_json_ld_job_posting()
    {
        const string html = """
            <html><head>
            <script type="application/ld+json">
            {
              "@context": "https://schema.org/",
              "@type": "JobPosting",
              "title": "Senior .NET Engineer",
              "hiringOrganization": { "@type": "Organization", "name": "Acme Corp" },
              "jobLocation": { "@type": "Place", "address": {
                "@type": "PostalAddress", "addressLocality": "Lincoln",
                "addressRegion": "NE", "addressCountry": "US" } },
              "baseSalary": { "@type": "MonetaryAmount", "currency": "USD",
                "value": { "@type": "QuantitativeValue",
                  "minValue": 140000, "maxValue": 170000, "unitText": "YEAR" } },
              "description": "<p>Build APIs.</p><p>Ship things.</p>"
            }
            </script>
            </head><body></body></html>
            """;

        var r = JobPostingParser.Parse(html, BoardsUrl);

        Assert.Equal("Acme Corp", r.Company);
        Assert.Equal("Senior .NET Engineer", r.Role);
        Assert.Equal("Lincoln, NE, US", r.Location);
        Assert.Equal("USD 140,000–170,000/year", r.Salary);
        Assert.Equal("greenhouse", r.Source);
        Assert.Contains("Build APIs.", r.Description);
        Assert.Contains("Ship things.", r.Description);
        Assert.DoesNotContain("<p>", r.Description); // description HTML is flattened
    }

    [Fact]
    public void Finds_the_posting_inside_a_graph_wrapper_and_type_arrays()
    {
        const string html = """
            <script type="application/ld+json">
            { "@graph": [
                { "@type": "WebSite", "name": "Acme Careers" },
                { "@type": ["JobPosting"], "title": "Platform Engineer",
                  "hiringOrganization": { "name": "Acme" } }
            ] }
            </script>
            """;

        var r = JobPostingParser.Parse(html, BoardsUrl);

        Assert.Equal("Platform Engineer", r.Role);
        Assert.Equal("Acme", r.Company);
    }

    [Fact]
    public void Remote_postings_combine_telecommute_with_the_eligible_region()
    {
        const string html = """
            <script type="application/ld+json">
            { "@type": "JobPosting", "title": "SRE",
              "jobLocationType": "TELECOMMUTE",
              "applicantLocationRequirements": { "@type": "Country", "name": "USA" } }
            </script>
            """;

        var r = JobPostingParser.Parse(html, BoardsUrl);

        Assert.Equal("Remote · USA", r.Location);
    }

    [Fact]
    public void Skips_malformed_json_ld_and_falls_through_to_a_later_block()
    {
        const string html = """
            <script type="application/ld+json">{ not json at all</script>
            <script type="application/ld+json">
            { "@type": "JobPosting", "title": "Data Engineer" }
            </script>
            """;

        Assert.Equal("Data Engineer", JobPostingParser.Parse(html, BoardsUrl).Role);
    }

    // ---- Tier 2: OpenGraph / <title> fallback --------------------------------

    [Fact]
    public void Falls_back_to_greenhouse_style_og_titles()
    {
        const string html = """
            <meta property="og:title" content="Job Application for Staff Engineer at Initech" />
            <meta property="og:description" content="We need a staff engineer." />
            """;

        var r = JobPostingParser.Parse(html, BoardsUrl);

        Assert.Equal("Staff Engineer", r.Role);
        Assert.Equal("Initech", r.Company);
        Assert.Equal("We need a staff engineer.", r.Description);
    }

    [Fact]
    public void Handles_the_new_greenhouse_job_boards_pages()
    {
        // Real shape of job-boards.greenhouse.io (June 2026): no JSON-LD, bare role
        // in og:title, the company only in <title>, and the LOCATION in og:description.
        const string html = """
            <meta property="og:title" content="Backend Engineer, Analytics Instrumentation (Golang)   "/>
            <meta property="og:description" content="Remote, India"/>
            <title>Job Application for Backend Engineer, Analytics Instrumentation (Golang)    at GitLab</title>
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://job-boards.greenhouse.io/gitlab/jobs/8481929002"));

        Assert.Equal("Backend Engineer, Analytics Instrumentation (Golang)", r.Role);
        Assert.Equal("GitLab", r.Company);
        Assert.Equal("Remote, India", r.Location);
        Assert.Null(r.Description); // the og:description WAS the location
        Assert.Equal("greenhouse", r.Source);
    }

    [Fact]
    public void Falls_back_to_linkedin_style_og_titles_with_a_location()
    {
        const string html = """
            <meta property="og:title" content="Hooli hiring Backend Developer in Omaha, NE | LinkedIn" />
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://www.linkedin.com/jobs/view/42"));

        Assert.Equal("Hooli", r.Company);
        Assert.Equal("Backend Developer", r.Role);
        Assert.Equal("Omaha, NE", r.Location);
        Assert.Equal("linkedin", r.Source);
    }

    [Fact]
    public void Falls_back_to_role_at_company_titles_and_site_name()
    {
        const string html = """
            <title>Senior Engineer at Vandelay Industries | Careers</title>
            <meta content="Vandelay Industries" property="og:site_name" />
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://careers.vandelay.com/jobs/9"));

        Assert.Equal("Senior Engineer", r.Role);
        Assert.Equal("Vandelay Industries", r.Company);
        Assert.Equal("vandelay", r.Source); // unknown host → second-level label
    }

    [Fact]
    public void A_page_with_no_job_data_still_reports_the_source()
    {
        var r = JobPostingParser.Parse("<html><body>nothing here</body></html>",
            new Uri("https://jobs.example.com/x"));

        Assert.Null(r.Company);
        Assert.Null(r.Role);
        Assert.Equal("example", r.Source);
    }

    // ---- SSRF gates -----------------------------------------------------------

    [Theory]
    [InlineData("ftp://example.com/file")]
    [InlineData("file:///etc/passwd")]
    [InlineData("javascript:alert(1)")]
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("https://example.com:8443/jobs")] // non-default port
    public void Rejects_non_http_or_non_default_port_urls(string url) =>
        Assert.Throws<AppValidationException>(() => JobPageFetcher.ValidateUrl(url));

    [Fact]
    public void Accepts_a_normal_https_url() =>
        Assert.Equal("example.com", JobPageFetcher.ValidateUrl("https://example.com/jobs/1").Host);

    [Theory]
    [InlineData("127.0.0.1")]        // loopback
    [InlineData("10.1.2.3")]         // RFC 1918
    [InlineData("172.16.28.162")]    // RFC 1918 (this LAN)
    [InlineData("192.168.0.10")]     // RFC 1918
    [InlineData("169.254.169.254")]  // link-local / cloud metadata
    [InlineData("100.64.0.1")]       // CGNAT
    [InlineData("198.18.0.1")]       // benchmarking
    [InlineData("0.0.0.0")]          // unspecified
    [InlineData("224.0.0.1")]        // multicast
    [InlineData("::1")]              // IPv6 loopback
    [InlineData("fd00::1")]          // IPv6 unique local
    [InlineData("fe80::1")]          // IPv6 link-local
    [InlineData("::ffff:192.168.1.1")] // IPv4-mapped private
    public void Blocks_private_and_special_addresses(string ip) =>
        Assert.True(JobPageFetcher.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]          // example.com
    [InlineData("140.82.112.3")]           // github.com
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    public void Allows_public_addresses(string ip) =>
        Assert.False(JobPageFetcher.IsBlockedAddress(IPAddress.Parse(ip)));
}
