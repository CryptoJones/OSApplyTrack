// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Scrape;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>
/// One contained trip to a posting in the browser container: the pre-flight, the
/// connection (with the proxy and Chromium flags handed to <c>run-server</c> as launch
/// options), a fresh context holding no credentials, route interception, and the
/// page. Shared by the submitter (fill, maybe click) and the discoverer (look, never
/// click). Dispose closes everything.
/// </summary>
public sealed partial class BrowserSession : IAsyncDisposable
{
    private static readonly string[] AtsSuffixes =
        ["greenhouse.io", "lever.co", "ashbyhq.com", "myworkdayjobs.com", "myworkdaysite.com"];

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly List<string> _refused = [];

    public IPage Page { get; }

    /// <summary>Hosts a top-level navigation was refused for — for the error message.</summary>
    public IReadOnlyList<string> Refused { get { lock (_refused) return _refused.ToList(); } }

    private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
    }

    public static async Task<BrowserSession> OpenAsync(BrowserOptions options, string link, CancellationToken ct)
    {
        if (!options.IsConfigured)
            throw new AppValidationException("browser submission isn't configured on this instance");
        var target = await PreflightAsync(options, link, ct);

        var playwright = await Playwright.CreateAsync();
        IBrowser browser;
        try
        {
            browser = await playwright.Chromium.ConnectAsync(options.Endpoint, new()
            {
                Timeout = 30_000,
                Headers = LaunchHeaders(options),
            });
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
        var context = await browser.NewContextAsync(new()
        {
            AcceptDownloads = false,
            ViewportSize = new() { Width = 1280, Height = 1600 },
        });
        context.SetDefaultTimeout(Math.Max(5_000, options.TimeoutSeconds * 1000 / 6));
        var page = await context.NewPageAsync();
        var session = new BrowserSession(playwright, browser, context, page);
        var allowedHost = target.Host;
        await context.RouteAsync("**/*", async route =>
        {
            var req = route.Request;
            if (!Uri.TryCreate(req.Url, UriKind.Absolute, out var u)
                || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                await route.AbortAsync();
                return;
            }
            // The live-form verification harness: nothing that could create an application
            // leaves. The one exception is the board's résumé uploader, which stages the file
            // straight to object storage with a multipart POST — an application is always
            // posted to the board's own host, and that is always aborted.
            if (options.BlockSubmissions && req.Method is not ("GET" or "HEAD") && !IsStorageUpload(u, req))
            {
                await route.AbortAsync();
                return;
            }
            // Top-level navigations stay on the posting's site (redirects to the ATS
            // that hosts its form are fine); sub-resources are left alone so the form
            // can load its scripts and styles.
            if (req.IsNavigationRequest && req.Frame == page.MainFrame && !HostAllowed(u.Host, allowedHost))
            {
                lock (session._refused) session._refused.Add(u.Host);
                await route.AbortAsync();
                return;
            }
            await route.ContinueAsync();
        });
        await page.GotoAsync(target.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await RevealFormAsync(page);
        return session;
    }

    public async Task<byte[]?> ScreenshotAsync()
    {
        try { return await Page.ScreenshotAsync(new() { FullPage = true, Type = ScreenshotType.Png }); }
        catch (PlaywrightException) { return null; }
    }

    /// <summary>The scraper's URL rules plus a resolved-address check, so the browser is
    /// never pointed at a private target by a lead's link.</summary>
    private static async Task<Uri> PreflightAsync(BrowserOptions options, string link, CancellationToken ct)
    {
        if (options.AllowPrivateTargets)
        {
            // Tests only: a loopback fixture on an ephemeral port. Still http(s) only.
            if (!Uri.TryCreate(link?.Trim(), UriKind.Absolute, out var lax)
                || (lax.Scheme != Uri.UriSchemeHttp && lax.Scheme != Uri.UriSchemeHttps))
                throw new AppValidationException("that doesn't look like an http(s) URL");
            return lax;
        }
        var uri = JobPageFetcher.ValidateUrl(link);
        IPAddress[] addresses;
        try { addresses = await Dns.GetHostAddressesAsync(uri.Host, ct); }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or ArgumentException)
        {
            throw new AppValidationException("the posting's host could not be resolved");
        }
        if (addresses.Length == 0 || addresses.Any(JobPageFetcher.IsBlockedAddress))
            throw new AppValidationException("refusing to drive the browser at a private address");
        return uri;
    }

    private static Dictionary<string, string> LaunchHeaders(BrowserOptions options)
    {
        // run-server launches a browser per connection with the options in this header.
        var args = new List<string> { "--disable-quic" };
        object launch;
        if (options.Proxy.Trim().Length > 0)
        {
            args.Add("--proxy-bypass-list=<-loopback>");
            launch = new { proxy = new { server = options.Proxy.Trim() }, args };
        }
        else
        {
            launch = new { args };
        }
        return new() { ["x-playwright-launch-options"] = JsonSerializer.Serialize(launch) };
    }

    private static bool IsStorageUpload(Uri u, IRequest req) =>
        u.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
        && req.Headers.TryGetValue("content-type", out var ct)
        && ct.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);

    private static bool HostAllowed(string host, string original)
    {
        host = host.ToLowerInvariant();
        original = original.ToLowerInvariant();
        if (host == original) return true;
        if (AtsSuffixes.Any(s => host == s || host.EndsWith("." + s))) return true;
        // Same registrable domain (careers.acme.com -> acme.com), two-label approximation.
        static string Reg(string h)
        {
            var parts = h.Split('.');
            return parts.Length <= 2 ? h : string.Join('.', parts[^2..]);
        }
        return Reg(host) == Reg(original);
    }

    /// <summary>Some boards hide the form behind an "Apply" button; press it when no field is visible yet.</summary>
    private static async Task RevealFormAsync(IPage page)
    {
        var anyField = page.Locator("input[type=file], input[type=text], input[type=email], textarea").First;
        if (await anyField.IsVisibleAsync()) return;
        var apply = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"^apply", RegexOptions.IgnoreCase) }).First;
        if (!await apply.IsVisibleAsync())
            apply = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^apply", RegexOptions.IgnoreCase) }).First;
        if (await apply.IsVisibleAsync())
        {
            await apply.ClickAsync();
            try { await anyField.WaitForAsync(new() { Timeout = 10_000 }); }
            catch (TimeoutException) { /* judged below by what maps */ }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.DisposeAsync(); } catch (PlaywrightException) { }
        try { await _browser.DisposeAsync(); } catch (PlaywrightException) { }
        _playwright.Dispose();
    }
}
