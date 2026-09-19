// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
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

    [Fact]
    public void Preserves_an_apostrophe_in_a_double_quoted_meta_value()
    {
        // A plain ["'] close would truncate the value at the apostrophe → "Bob".
        const string html = """
            <meta property="og:title" content="Staff Engineer at Bob's Burgers" />
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://jobs.bobs.example/1"));

        Assert.Equal("Staff Engineer", r.Role);
        Assert.Equal("Bob's Burgers", r.Company);
    }

    [Fact]
    public void Falls_back_to_the_raw_title_when_no_pattern_matches()
    {
        // Dash-separated (no " at "/"hiring"/"Job Application for") matches no pattern;
        // the raw title is the role of last resort rather than returning nothing.
        const string html = "<title>Senior Backend Engineer - Acme Careers</title>";

        var r = JobPostingParser.Parse(html, new Uri("https://jobs.acme.example/9"));

        Assert.Equal("Senior Backend Engineer - Acme Careers", r.Role);
    }

    [Fact]
    public void Keeps_marketing_prose_as_the_description_not_the_location()
    {
        // A short og:description that merely mentions remote work is a tagline, not a
        // place — it must not be lifted into the location (nor the description dropped).
        const string html = """
            <meta property="og:title" content="Platform Engineer at Globex" />
            <meta property="og:description" content="Help us build the future of remote work" />
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://jobs.globex.example/3"));

        Assert.Null(r.Location);
        Assert.Equal("Help us build the future of remote work", r.Description);
    }

    [Fact]
    public void Lifts_a_bare_remote_marker_into_the_location()
    {
        const string html = """
            <meta property="og:title" content="Designer at Hooli" />
            <meta property="og:description" content="100% Remote" />
            """;

        var r = JobPostingParser.Parse(html, new Uri("https://jobs.hooli.example/5"));

        Assert.Equal("100% Remote", r.Location);
        Assert.Null(r.Description);
    }

    [Theory]
    [InlineData("careers.acme.co.uk", "acme")]
    [InlineData("jobs.acme.com.au", "acme")]
    [InlineData("acme.co.jp", "acme")]
    [InlineData("careers.acme.com", "acme")]
    [InlineData("acme.io", "acme")]
    public void Reads_the_org_label_past_a_two_part_public_suffix(string host, string expected) =>
        Assert.Equal(expected, JobPostingParser.SourceFromHost(host));

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
    [InlineData("64:ff9b::a9fe:a9fe")]  // NAT64 of 169.254.169.254 (cloud metadata)
    [InlineData("64:ff9b::7f00:1")]     // NAT64 of 127.0.0.1
    [InlineData("2002:c0a8:0001::")]    // 6to4 of 192.168.0.1
    [InlineData("::a9fe:a9fe")]         // IPv4-compatible 169.254.169.254
    public void Blocks_embedded_ipv4_in_transitional_ipv6(string ip) =>
        Assert.True(JobPageFetcher.IsBlockedAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("93.184.216.34")]          // example.com
    [InlineData("140.82.112.3")]           // github.com
    [InlineData("2606:2800:220:1:248:1893:25c8:1946")]
    [InlineData("2002:5db8:d822::")]       // 6to4 wrapping a public v4 (93.184.216.34)
    public void Allows_public_addresses(string ip) =>
        Assert.False(JobPageFetcher.IsBlockedAddress(IPAddress.Parse(ip)));

    // ---- Whole-fetch budget (#266) ---------------------------------------------

    [Fact]
    public async Task The_budget_covers_a_stalled_body_read_not_just_the_headers()
    {
        // The bug #266 fixes: a board that sends headers and then trickles its body had no
        // ceiling at all. The whole-fetch budget is what bounds it.
        var fetcher = new JobPageFetcher(new StallingBodyHandler(), totalTimeout: TimeSpan.FromMilliseconds(400));

        var sw = Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<ScrapeUnavailableException>(
            () => fetcher.FetchAsync(BoardsUrl.ToString(), CancellationToken.None));
        sw.Stop();

        Assert.Equal("that page took too long to answer", ex.Message);
        // The budget is 400ms; anything near it not firing means the read is still unbounded.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"the stalled body ran for {sw.Elapsed}");
    }

    [Fact]
    public async Task A_page_that_finishes_inside_the_budget_still_loads()
    {
        // The other half of the contract: a budget that fails working fetches is as broken as
        // one that bounds nothing. This is also what distinguishes a shared budget from a
        // too-tight one.
        var fetcher = new JobPageFetcher(
            new DelayedBodyHandler(TimeSpan.FromMilliseconds(150), "<html><title>Slow but fine</title></html>"),
            totalTimeout: TimeSpan.FromSeconds(5));

        var (html, finalUrl) = await fetcher.FetchAsync(BoardsUrl.ToString(), CancellationToken.None);

        Assert.Contains("Slow but fine", html);
        Assert.Equal(BoardsUrl, finalUrl);
    }

    [Fact]
    public async Task The_budget_is_shared_across_redirect_hops_not_restarted_per_hop()
    {
        // A per-hop ceiling would let every one of these slow-but-legal redirects finish and
        // then report "too many redirects". A shared one cuts the chain off part-way and says
        // it ran out of time — that message is the assertion that actually discriminates.
        // The hop count is the anti-vacuity guard: at least one redirect has to have been
        // followed, or the budget merely killed the first request and proved nothing.
        var handler = new SlowRedirectHandler(TimeSpan.FromMilliseconds(1500));
        var fetcher = new JobPageFetcher(handler, totalTimeout: TimeSpan.FromSeconds(4));

        var ex = await Assert.ThrowsAsync<ScrapeUnavailableException>(
            () => fetcher.FetchAsync(BoardsUrl.ToString(), CancellationToken.None));

        Assert.Equal("that page took too long to answer", ex.Message);
        Assert.True(handler.Requests >= 2,
            $"only {handler.Requests} hop(s) were attempted — the chain never got past the first, so sharing was never exercised");
    }

    [Fact]
    public async Task A_page_that_cannot_be_reached_is_not_reported_as_a_timeout()
    {
        // Refused, NXDOMAIN and TLS failures are not timeouts. Saying they are sends the
        // reader after the wrong problem — only the clock gets to claim a timeout.
        var fetcher = new JobPageFetcher(new UnreachableHandler(), totalTimeout: TimeSpan.FromSeconds(30));

        var ex = await Assert.ThrowsAsync<ScrapeUnavailableException>(
            () => fetcher.FetchAsync(BoardsUrl.ToString(), CancellationToken.None));

        Assert.Equal("couldn't reach that page", ex.Message);
    }

    [Fact]
    public async Task A_caller_cancel_during_the_body_read_is_never_masked_as_a_502()
    {
        // A client that goes away is not a board that misbehaved: the endpoint has to see
        // cancellation, not a ScrapeUnavailableException the middleware turns into a 502.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var fetcher = new JobPageFetcher(new StallingBodyHandler(), totalTimeout: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync(BoardsUrl.ToString(), cts.Token));
    }

    [Fact]
    public async Task A_caller_cancel_while_waiting_for_headers_is_never_masked_as_a_502()
    {
        // The send path is where HttpClient wraps things, so this one has to be decided by the
        // token rather than by the exception type. It is the path a stopping agent worker
        // takes, and a disconnect there must not read as a dead board.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var fetcher = new JobPageFetcher(new StallingHeadersHandler(), totalTimeout: TimeSpan.FromMinutes(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fetcher.FetchAsync(BoardsUrl.ToString(), cts.Token));
    }

    /// <summary>Answers headers at once, then stalls the body until the token fires — the
    /// shape a board trickling its response has.</summary>
    private sealed class StallingBodyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var content = new StreamContent(new StallingStream());
            content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    /// <summary>Takes the request and never answers it, so the fetch is still inside
    /// SendAsync when the token fires.</summary>
    private sealed class StallingHeadersHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("the infinite delay only ends by throwing");
        }
    }

    /// <summary>Answers with a complete page after a delay — the legitimate slow fetch.</summary>
    private sealed class DelayedBodyHandler(TimeSpan delay, string html) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(delay, ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(html, Encoding.UTF8, "text/html"),
            };
        }
    }

    /// <summary>Refuses the connection, as a DNS, TLS or connect failure reaches the caller.</summary>
    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("Connection refused"));
    }

    /// <summary>A body that never yields a byte until it is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("the infinite delay only ends by throwing");
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Answers every request with a redirect after a delay, counting the hops.</summary>
    private sealed class SlowRedirectHandler(TimeSpan delay) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests++;
            await Task.Delay(delay, ct);
            var res = new HttpResponseMessage(HttpStatusCode.Redirect) { RequestMessage = request };
            res.Headers.Location = new Uri(request.RequestUri!, $"/acme/jobs/{Requests + 1}");
            return res;
        }
    }
}
