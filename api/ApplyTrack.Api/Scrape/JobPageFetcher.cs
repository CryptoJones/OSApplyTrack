// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Sockets;
using System.Text;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Scrape;

/// <summary>
/// SSRF-guarded fetcher for <c>POST /api/scrape</c>. The URL is attacker-suppliable,
/// so every defense lives here rather than in the endpoint: http/https on default
/// ports only, DNS resolution pinned to public addresses (the connect callback
/// validates the resolved IPs and dials those — a rebinding name can't swap in a
/// private target between check and use), manual redirect following with the same
/// validation per hop, a hard response-size cap, and a single wall-clock budget for
/// the whole fetch (#266).
/// </summary>
public sealed class JobPageFetcher
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;
    // The only ceiling on a fetch (#266), and it has to be here because HttpClient.Timeout
    // cannot do the job: under ResponseHeadersRead it stops covering the response once the
    // headers arrive — measured, not assumed: a stalled body ran past it indefinitely — and
    // it restarts per hop, so a chain of redirects stacked. 15s is generous for one real page
    // load (a full-cap 2 MB body needs ~140 KB/s to land inside it) while still failing fast
    // on a stall.
    private static readonly TimeSpan DefaultTotalTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly TimeSpan _totalTimeout;

    public JobPageFetcher()
        : this(new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects re-validated by hand below
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectToPublicAddressOnlyAsync,
        })
    {
    }

    /// <summary>
    /// Build a fetcher over a supplied handler. The production path uses the
    /// parameterless constructor, whose SSRF-guarded handler pins DNS to public
    /// addresses; this overload lets the test suite drive the same fetch/redirect/cap
    /// logic against a scripted handler with no network. Public for that reason.
    /// <paramref name="totalTimeout"/> overrides the whole-fetch budget (tests use a
    /// tiny value; production takes the 15s default).
    /// </summary>
    public JobPageFetcher(HttpMessageHandler handler, TimeSpan? totalTimeout = null)
    {
        // Fail at construction rather than per request: a non-positive budget makes
        // CancelAfter fire immediately, so every fetch would look like a timeout.
        if (totalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(
                nameof(totalTimeout), totalTimeout, "the whole-fetch budget must be positive");
        _totalTimeout = totalTimeout ?? DefaultTotalTimeout;
        // Deliberately no timeout of its own: the budget above is the single ceiling, so a
        // slow hop is measured against 15s instead of being cut short by a second, smaller
        // clock that also made the class's own documentation untrue.
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // Some boards refuse the default HttpClient UA; identify as a browser-compatible
        // bot with a pointer back to the project.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; OSApplyTrack/1.2; +https://github.com/CryptoJones/OSApplyTrack)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }

    /// <summary>Fetch the page, following up to <see cref="MaxRedirects"/> redirects,
    /// and return the HTML plus the URL that finally answered.</summary>
    /// <param name="accept">An <c>Accept</c> header for this one request, in place of the page
    /// fetcher's <c>text/html</c>. Workday's job JSON answers <c>text/html</c> with a 406 — which
    /// says nothing — and only a JSON request with the 404 that means the job is gone (#277).</param>
    public async Task<(string Html, Uri FinalUrl)> FetchAsync(string rawUrl, CancellationToken ct, string? accept = null)
    {
        var uri = ValidateUrl(rawUrl);
        // Every redirect hop and the body read draw on the same clock, linked to the caller's
        // token so a disconnect still cancels at once. The expiry source is kept separate
        // from the linked one so "the budget fired" is positive evidence rather than an
        // inference from whichever exception type happened to surface.
        using var expiry = new CancellationTokenSource(_totalTimeout);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, expiry.Token);
        var token = budget.Token;
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            // Set on the request, a header is not overlaid by the client's default.
            if (accept is not null) req.Headers.Accept.ParseAdd(accept);
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, token);
            }
            catch (Exception ex) when (FindValidation(ex) is { } validation)
            {
                throw validation; // private-address rejection from the connect callback
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // The client went away. Ask the token rather than the type: a wrapping
                // handler can hand back an HttpRequestException for a cancelled connect.
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (Exception) when (expiry.IsCancellationRequested)
            {
                throw new ScrapeUnavailableException("that page took too long to answer");
            }
            catch (HttpRequestException)
            {
                // Refused, NXDOMAIN, TLS failure, reset before the headers — the page was
                // never reached. A different failure from a timeout, and a different fix.
                throw new ScrapeUnavailableException("couldn't reach that page");
            }

            using (res)
            {
                if ((int)res.StatusCode is >= 300 and < 400)
                {
                    if (hop >= MaxRedirects)
                        throw new ScrapeUnavailableException("too many redirects");
                    var location = res.Headers.Location
                        ?? throw new ScrapeUnavailableException("the page redirected nowhere");
                    uri = ValidateUrl(new Uri(uri, location).AbsoluteUri);
                    continue;
                }
                if (!res.IsSuccessStatusCode)
                    throw new ScrapeUnavailableException(
                        $"the page answered HTTP {(int)res.StatusCode}");

                var mediaType = res.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType.Length > 0 && !mediaType.Contains("html") && !mediaType.Contains("xml"))
                    throw new ScrapeUnavailableException("that URL isn't an HTML page");

                // The body read can still fail after headers arrive — a connection reset
                // mid-body, corrupt gzip/brotli under AutomaticDecompression, or the budget
                // firing during a slow body. Map those to the same 502 the contract uses for
                // "couldn't reach that page", not a generic 500.
                try
                {
                    return (await ReadCappedAsync(res, token), uri);
                }
                catch (ScrapeUnavailableException)
                {
                    throw; // the size cap — not a timeout, whatever the clock happens to say
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    ct.ThrowIfCancellationRequested();
                    throw;
                }
                catch (Exception) when (expiry.IsCancellationRequested)
                {
                    // The budget elapsed mid-body. Say that, rather than blaming a read that
                    // was working fine — a board trickling its body is the common case, and
                    // the two need different fixes.
                    throw new ScrapeUnavailableException("that page took too long to answer");
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidDataException)
                {
                    throw new ScrapeUnavailableException("couldn't read that page");
                }
            }
        }
    }

    /// <summary>http/https on the default port, absolute, with a host. Throws the
    /// 400-mapped validation exception otherwise. Public for the test suite.</summary>
    public static Uri ValidateUrl(string rawUrl)
    {
        if (!Uri.TryCreate(rawUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.HostNameType == UriHostNameType.Unknown
            || uri.Host.Length == 0)
            throw new AppValidationException("that doesn't look like an http(s) URL");
        if (!uri.IsDefaultPort)
            throw new AppValidationException("only standard http(s) ports are supported");
        return uri;
    }

    /// <summary>True for any address a scrape must never touch: loopback, RFC 1918,
    /// link-local (cloud metadata), CGNAT, benchmark, TEST-NET, multicast, unspecified, and the
    /// IPv6 unique-local/site-local equivalents. Public for the test suite.</summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        // Embedded-IPv4 forms (IPv4-mapped ::ffff:0:0/96, NAT64 64:ff9b::/96, 6to4
        // 2002::/16, IPv4-compatible ::a.b.c.d) all carry an IPv4 target inside an
        // IPv6 address — unwrap it so the v4 blocklist applies, or an AAAA like
        // 64:ff9b::a9fe:a9fe (169.254.169.254) would slip past the v6 checks.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
        else if (TryExtractTeredo(ip, out var server, out var client))
            return IsBlockedAddress(server) || IsBlockedAddress(client);
        else if (TryExtractEmbeddedIPv4(ip, out var embedded))
            return IsBlockedAddress(embedded);

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
            || ip.Equals(IPAddress.Broadcast))
            return true;

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 0                            // 0.0.0.0/8 "this network"
                || b[0] == 10                           // 10/8
                || (b[0] == 100 && (b[1] & 0xC0) == 64) // 100.64/10 CGNAT
                || (b[0] == 169 && b[1] == 254)         // 169.254/16 link-local + metadata
                || (b[0] == 172 && (b[1] & 0xF0) == 16) // 172.16/12
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)   // 192.0.0/24 special-purpose
                || (b[0] == 192 && b[1] == 0 && b[2] == 2)   // 192.0.2/24 TEST-NET-1
                || (b[0] == 198 && b[1] == 51 && b[2] == 100) // 198.51.100/24 TEST-NET-2
                || (b[0] == 203 && b[1] == 0 && b[2] == 113) // 203.0.113/24 TEST-NET-3
                || (b[0] == 192 && b[1] == 168)         // 192.168/16
                || (b[0] == 198 && (b[1] & 0xFE) == 18) // 198.18/15 benchmarking
                || b[0] >= 224;                         // multicast + reserved + broadcast
        }

        var v6 = ip.GetAddressBytes();
        return ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || (v6[0] & 0xFE) == 0xFC                    // fc00::/7 unique local
            || (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0D && v6[3] == 0xB8); // 2001:db8::/32 documentation
    }

    // Teredo 2001:0000::/32 carries two IPv4 hosts: the server in bits 32-63 and the
    // client, bit-inverted, in the low 32. Both are judged — the poller's guard does the
    // same (#339).
    private static bool TryExtractTeredo(IPAddress ip, out IPAddress server, out IPAddress client)
    {
        server = client = IPAddress.None;
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        var b = ip.GetAddressBytes();
        if (!(b[0] == 0x20 && b[1] == 0x01 && b[2] == 0 && b[3] == 0))
            return false;
        server = new IPAddress(b[4..8]);
        client = new IPAddress([(byte)~b[12], (byte)~b[13], (byte)~b[14], (byte)~b[15]]);
        return true;
    }

    // Pulls the embedded IPv4 target out of the transitional IPv6 forms that carry one.
    // (IPv4-mapped ::ffff:a.b.c.d is handled separately by the caller via MapToIPv4.)
    private static bool TryExtractEmbeddedIPv4(IPAddress ip, out IPAddress v4)
    {
        v4 = IPAddress.None;
        if (ip.AddressFamily != AddressFamily.InterNetworkV6)
            return false;
        var b = ip.GetAddressBytes();

        // NAT64 64:ff9b::/96 — the IPv4 is the low 32 bits.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
            && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            v4 = new IPAddress(b[12..16]);
            return true;
        }

        // 6to4 2002::/16 — the IPv4 is bytes 2..5 (2002:V4::/48).
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            v4 = new IPAddress(b[2..6]);
            return true;
        }

        // IPv4-compatible ::a.b.c.d (first 96 bits zero), excluding :: (unspecified)
        // and ::1 (loopback), which the family-agnostic checks above already catch.
        var prefixZero = true;
        for (var i = 0; i < 12; i++)
            if (b[i] != 0) { prefixZero = false; break; }
        if (prefixZero && !(b[12] == 0 && b[13] == 0 && b[14] == 0 && b[15] <= 1))
        {
            v4 = new IPAddress(b[12..16]);
            return true;
        }

        return false;
    }

    private static async ValueTask<Stream> ConnectToPublicAddressOnlyAsync(
        SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct);
        var publicAddresses = Array.FindAll(addresses, a => !IsBlockedAddress(a));
        if (publicAddresses.Length == 0)
            throw new AppValidationException("that URL points at a private or internal address");

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(publicAddresses, ctx.DnsEndPoint.Port, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    // The connect callback's AppValidationException surfaces wrapped in an
    // HttpRequestException; dig it out so the client sees the real 400 message.
    private static AppValidationException? FindValidation(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException!)
            if (e is AppValidationException v)
                return v;
        return null;
    }

    private static async Task<string> ReadCappedAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Content.Headers.ContentLength is > MaxBytes)
            throw new ScrapeUnavailableException("that page is too large to scrape");

        await using var stream = await res.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[64 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                throw new ScrapeUnavailableException("that page is too large to scrape");
            buffer.Write(chunk, 0, read);
        }

        var charset = res.Content.Headers.ContentType?.CharSet?.Trim('"');
        Encoding encoding;
        try { encoding = charset is null ? Encoding.UTF8 : Encoding.GetEncoding(charset); }
        catch (ArgumentException) { encoding = Encoding.UTF8; }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }
}
