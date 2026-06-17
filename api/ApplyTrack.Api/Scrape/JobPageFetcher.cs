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
/// validation per hop, and a hard response-size cap and timeout.
/// </summary>
public sealed class JobPageFetcher
{
    private const int MaxBytes = 2 * 1024 * 1024;
    private const int MaxRedirects = 5;

    private readonly HttpClient _http;

    public JobPageFetcher()
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false, // redirects re-validated by hand below
            AutomaticDecompression = DecompressionMethods.All,
            ConnectCallback = ConnectToPublicAddressOnlyAsync,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        // Some boards refuse the default HttpClient UA; identify as a browser-compatible
        // bot with a pointer back to the project.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; OSApplyTrack/1.2; +https://github.com/CryptoJones/OSApplyTrack)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en");
    }

    /// <summary>Fetch the page, following up to <see cref="MaxRedirects"/> redirects,
    /// and return the HTML plus the URL that finally answered.</summary>
    public async Task<(string Html, Uri FinalUrl)> FetchAsync(string rawUrl, CancellationToken ct)
    {
        var uri = ValidateUrl(rawUrl);
        for (var hop = 0; ; hop++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            HttpResponseMessage res;
            try
            {
                res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (Exception ex) when (FindValidation(ex) is { } validation)
            {
                throw validation; // private-address rejection from the connect callback
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
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
                // mid-body, corrupt gzip/brotli under AutomaticDecompression, or the
                // timeout firing during a slow body. Map those to the same 502 the
                // contract uses for "couldn't reach that page", not a generic 500.
                // (ReadCappedAsync's own size-cap ScrapeUnavailableException passes through.)
                try
                {
                    return (await ReadCappedAsync(res, ct), uri);
                }
                catch (Exception ex)
                    when (ex is IOException or HttpRequestException or TaskCanceledException
                        or InvalidDataException)
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
    /// link-local (cloud metadata), CGNAT, benchmark, multicast, unspecified, and the
    /// IPv6 unique-local/site-local equivalents. Public for the test suite.</summary>
    public static bool IsBlockedAddress(IPAddress ip)
    {
        // Embedded-IPv4 forms (IPv4-mapped ::ffff:0:0/96, NAT64 64:ff9b::/96, 6to4
        // 2002::/16, IPv4-compatible ::a.b.c.d) all carry an IPv4 target inside an
        // IPv6 address — unwrap it so the v4 blocklist applies, or an AAAA like
        // 64:ff9b::a9fe:a9fe (169.254.169.254) would slip past the v6 checks.
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();
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
                || (b[0] == 192 && b[1] == 168)         // 192.168/16
                || (b[0] == 198 && (b[1] & 0xFE) == 18) // 198.18/15 benchmarking
                || b[0] >= 224;                         // multicast + reserved + broadcast
        }

        return ip.IsIPv6Multicast || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || (ip.GetAddressBytes()[0] & 0xFE) == 0xFC; // fc00::/7 unique local
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
