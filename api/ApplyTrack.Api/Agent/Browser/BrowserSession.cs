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
    [
        "greenhouse.io", "lever.co", "ashbyhq.com", "myworkdayjobs.com", "myworkdaysite.com",
        "workable.com", "breezy.hr", "smartrecruiters.com", "join.com",
    ];

    // Localised too: join.com renders its posting in the employer's language, so the
    // real Apply on a French posting is "Postuler maintenant" (#210).
    [GeneratedRegex(@"^\s*(?:apply\b|i['’]?m interested|postuler\b|(?:jetzt )?bewerben|aplicar\b|solicitar\b|candidat)", RegexOptions.IgnoreCase)]
    private static partial Regex ApplyTrigger();

    /// <summary>
    /// A control that only LOOKS like the way in: "Apply later", "Send me the link",
    /// "Remind me", a job-alert subscription. join.com puts an email box and an
    /// "Apply later" submit on every posting page; clicking it emails the candidate the
    /// posting's own link and sends no application (#210). Never the Apply trigger,
    /// never the Submit button — here and in <see cref="BrowserSubmitter"/>.
    /// </summary>
    [GeneratedRegex(@"later|plus tard|später|spaeter|más tarde|mais tarde|remind|send (?:me )?(?:the |a )?link|envoyer|subscribe|alert|newsletter", RegexOptions.IgnoreCase)]
    public static partial Regex LaterWords();

    /// <summary>
    /// A JavaScript predicate, <c>(el) => bool</c>, for a control that is a page widget and
    /// never part of an application form: the careers site's job search (keyword, location,
    /// the facet selects) and its job-alert / subscribe box. SAP SuccessFactors career sites
    /// put both on every posting page, and counting them as "the form" is how the agent never
    /// pressed Apply now on Kiewit's posting and discovered "Search by Keyword" as a question
    /// (#214). Spliced into every page script that enumerates controls, so all of them agree.
    /// </summary>
    public const string WidgetJs = """
        (el) => {
          const a = (e, n) => (e.getAttribute(n) || '').toLowerCase();
          if (a(el, 'type') === 'search') return true;
          const own = [a(el, 'name'), a(el, 'id'), a(el, 'placeholder'), a(el, 'aria-label'), a(el, 'class')].join(' ');
          // "search" as its own word or suffix (keywordsearch, locationsearch, columnized-search),
          // never inside another word (research_area); the alert box by its own field names.
          if (/(?:^|[^a-z])(?:search|keyword)|(?:location|job|keyword)search|createnewalert|(?:^|[^a-z])frequency(?:$|[^a-z])|job.?alert|subscribe/.test(own)) return true;
          return !!el.closest('[role=search], form[action*="search" i], form[class*="search" i], form[id*="search" i], form[name*="search" i], '
            + 'form[action*="subscribe" i], form[class*="subscribe" i], form[id*="subscribe" i], form[name*="subscribe" i], '
            + 'form[action*="alert" i], form[class*="alert" i], form[id*="alert" i], [class*="job-alert" i], [class*="jobalert" i], [id*="jobalert" i], [class*="emailsubscribe" i]');
        }
        """;

    /// <summary>
    /// The name of an applicant-tracking system that only takes applications from a
    /// signed-in candidate account (email + password), for a host Apply led to — or null.
    /// The browser never creates accounts, so such a posting is the person's by Copy answers
    /// and open, and the run says so instead of "the browser refuses to follow" (#214).
    /// </summary>
    public static string? AccountOnlyAts(string host)
    {
        host = (host ?? "").ToLowerInvariant();
        if (host.EndsWith("successfactors.com") || host.EndsWith("successfactors.eu") || host.EndsWith("sapsf.com") || host.EndsWith("sapsf.eu"))
            return "SAP SuccessFactors";
        if (host.EndsWith("myworkdayjobs.com") || host.EndsWith("myworkdaysite.com"))
            return "Workday";
        return null;
    }

    // Cookie-consent banners (#202): the well-known managers by their own ids first, then
    // any visible button that reads as "accept" inside a box that calls itself a cookie,
    // consent or privacy notice. Zoho Recruit and Workable's job finder both put an
    // "Accept all" over the page; Playwright will not click Apply through it.
    private static readonly string[] ConsentSelectors =
    [
        "#onetrust-accept-btn-handler",
        "#CybotCookiebotDialogBodyLevelButtonLevelOptinAllowAll", "#CybotCookiebotDialogBodyButtonAccept",
        ".cc-btn.cc-allow", ".cc-btn.cc-dismiss",
        "#truste-consent-button", ".osano-cm-accept-all", "#hs-eu-confirmation-button",
        "button[data-cookiebanner='accept_button']",
        ".cw-cookie-banner .cookie-accept-btn",
        // Radancy (TalentBrew) career sites — UnitedHealth Group, and a great many employers'
        // own careers pages: "Important System Message", nothing in it named for cookies (#283).
        "#system-ialert-button",
        // The rest of the field, by the ids and classes each manager ships with: Didomi,
        // Usercentrics (in an open shadow root, which a locator pierces), Quantcast Choice,
        // CookieYes, Complianz, Iubenda, Termly, Klaro, Civic, Cookie Script.
        "#didomi-notice-agree-button", "[data-testid='uc-accept-all-button']",
        ".qc-cmp2-summary-buttons button[mode='primary']",
        ".cky-btn-accept", ".cmplz-btn.cmplz-accept", ".iubenda-cs-accept-btn",
        "[data-tid='banner-accept']", ".klaro .cm-btn-success", "#ccc-notify-accept",
        "#cookiescript_accept",
    ];
    [GeneratedRegex(@"^\s*(?:accept|allow|agree|got it|ok(?:ay)?|i (?:agree|accept|understand)|yes)(?:\s+(?:all|everything|cookies|all cookies|and close|& close|to all|and continue|& continue|and proceed))?\s*[.!]?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ConsentAccept();

    private readonly IPlaywright _playwright;
    private readonly IBrowser _browser;
    private readonly IBrowserContext _context;
    private readonly List<string> _refused = [];
    private IReadOnlyList<BoardAccount> _accounts = [];

    /// <summary>The account the session signed in with, when Apply led to a sign-in (#216); null otherwise.</summary>
    public BoardAccount? SignedInAs { get; private set; }

    /// <summary>The page the form is on. Usually the one opened at the link; the popup an
    /// Apply link opened in a new tab when there was one.</summary>
    public IPage Page { get; private set; }

    /// <summary>Hosts a top-level navigation was refused for — for the error message.</summary>
    public IReadOnlyList<string> Refused { get { lock (_refused) return _refused.ToList(); } }

    /// <summary>What happened while looking for the form behind an Apply button, when that
    /// did not end with a form — the reason a "no form found" outcome can name (#180).</summary>
    public string RevealNote { get; private set; } = "";

    private BrowserSession(IPlaywright playwright, IBrowser browser, IBrowserContext context, IPage page)
    {
        _playwright = playwright;
        _browser = browser;
        _context = context;
        Page = page;
    }

    /// <param name="accounts">The candidate's own sign-ins on account-only ATSs (#216). A top-level
    /// navigation onto one of their hosts is allowed, and a password page on one of them is
    /// signed in to, so the form behind it can be reached.</param>
    public static async Task<BrowserSession> OpenAsync(BrowserOptions options, string link, CancellationToken ct,
        IReadOnlyList<BoardAccount>? accounts = null)
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
        var session = new BrowserSession(playwright, browser, context, page) { _accounts = accounts ?? [] };
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
            // can load its scripts and styles. Any top-level frame in the context — the
            // tab an Apply link opens is held to the same rule as the first page.
            if (req.IsNavigationRequest && req.Frame.ParentFrame is null && !HostAllowed(u.Host, allowedHost)
                && BoardAccount.For(session._accounts, u.Host) is null)
            {
                lock (session._refused) session._refused.Add(u.Host);
                await route.AbortAsync();
                return;
            }
            await route.ContinueAsync();
        });
        var response = await page.GotoAsync(target.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        // The status the posting answered with: a 404 or 410 is a gone job, whatever the
        // page says (#203). Zero when the navigation produced no response (about:blank).
        session.Status = response?.Status ?? 0;
        await DismissConsentAsync(page);
        await session.RevealFormAsync();
        // A form that arrives folded — SuccessFactors' application is an accordion of
        // sections, every one but the first collapsed — is opened before anyone reads it.
        await ExpandSectionsAsync(session.Page);
        return session;
    }

    /// <summary>The HTTP status of the first navigation, or 0 when there was no response.</summary>
    public int Status { get; private set; }

    /// <summary>
    /// Click away a cookie-consent banner when one is up (#202), in the page or any frame
    /// it embeds. Bounded and quiet: a banner that will not go is left where it is and the
    /// run carries on. Public: the submitter calls it again on the page the form is on,
    /// since a banner can arrive after load or with the tab Apply opens. True when one was
    /// dismissed.
    /// </summary>
    public static async Task<bool> DismissConsentAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                foreach (var selector in ConsentSelectors)
                {
                    var known = frame.Locator(selector).First;
                    if (!await known.IsVisibleAsync()) continue;
                    await known.ClickAsync(new() { Timeout = 2_000 });
                    await page.WaitForTimeoutAsync(300);
                    return true;
                }
                // Buttons, and links: a good many banners make "Accept" an anchor.
                var buttons = frame.GetByRole(AriaRole.Button, new() { NameRegex = ConsentAccept() })
                    .Or(frame.GetByRole(AriaRole.Link, new() { NameRegex = ConsentAccept() }));
                var count = Math.Min(await buttons.CountAsync(), 6);
                for (var i = 0; i < count; i++)
                {
                    var candidate = buttons.Nth(i);
                    if (!await candidate.IsVisibleAsync()) continue;
                    // "I agree" on the application form itself is an answer, not a banner:
                    // only a button inside something that calls itself cookie/consent/privacy —
                    // or inside a dialog, or a box pinned over the page, that *says* it is about
                    // cookies and asks for nothing else — not even a checkbox: an acknowledgement
                    // box beside "I agree" is an application's, and a manager that shows category
                    // toggles up front is one of the named ones above. Radancy's box is id "system-imessage", labelled "Important System
                    // Message"; only its text mentions cookies, and the Apply click died
                    // behind it for want of reading that (#283).
                    var inBanner = await candidate.EvaluateAsync<bool>("""
                        el => {
                          for (let a = el; a && a !== document.body; a = a.parentElement) {
                            const id = a.id || '';
                            const cls = typeof a.className === 'string' ? a.className : '';
                            const label = a.getAttribute('aria-label') || '';
                            if (/cookie|consent|gdpr|privacy/i.test(id + ' ' + cls + ' ' + label)) return true;
                            const role = a.getAttribute('role') || '';
                            const pos = getComputedStyle(a).position;
                            // A dialog, or anything pinned over the page the way a banner is.
                            if ((role === 'dialog' || role === 'alertdialog' || a.getAttribute('aria-modal') === 'true'
                                 || pos === 'fixed' || pos === 'sticky')
                                && /\bcookies?\b/i.test(a.innerText || '')
                                && !a.querySelector('input:not([type=hidden]):not([type=button]):not([type=submit]), select, textarea'))
                              return true;
                          }
                          return false;
                        }
                        """);
                    if (!inBanner) continue;
                    await candidate.ClickAsync(new() { Timeout = 2_000 });
                    await page.WaitForTimeoutAsync(300);
                    return true;
                }
            }
            catch (TimeoutException) { /* the click did not land; carry on without it */ }
            catch (PlaywrightException) { /* a frame mid-navigation, or the banner went away on its own */ }
        }
        return false;
    }

    /// <summary>The cookies the context holds for <paramref name="url"/> — how a signed-in
    /// portal session is read back off the browser (#221).</summary>
    public Task<IReadOnlyList<BrowserContextCookiesResult>> CookiesAsync(string url) => _context.CookiesAsync([url]);

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

    public static bool HostAllowed(string host, string original)
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

    private static readonly string AnyFieldScript = """
        () => {
          const widget = __WIDGET__;
          const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          return [...document.querySelectorAll('input[type=file], input[type=text], input[type=email], input:not([type]), textarea, select')]
            .some(el => !widget(el) && (el.getAttribute('type') === 'file' || shown(el)));
        }
        """.Replace("__WIDGET__", WidgetJs);

    /// <summary>Is a form control on screen — in the page, or in any frame it embeds? A
    /// search or job-alert widget's controls never count (#214).</summary>
    public static async Task<bool> AnyFieldVisibleAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try { if (await frame.EvaluateAsync<bool>(AnyFieldScript)) return true; }
            catch (PlaywrightException) { /* a frame mid-navigation */ }
        }
        return false;
    }

    /// <summary>Wait, up to <paramref name="timeoutMs"/>, for a form control to appear anywhere
    /// in the page. True when one did. Forms that arrive late are the rule, not the exception:
    /// Ashby fetches its form after the page is idle, Comeet loads it into an iframe on Apply.</summary>
    public static async Task<bool> WaitForFieldAsync(IPage page, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await AnyFieldVisibleAsync(page)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>
    /// Does the page show something that reads as an <i>application</i> form — a file
    /// input, a textarea, or at least two text-like boxes (email counts as one) — rather
    /// than merely a control? A posting page's job-search box is one text input, and
    /// taking it for the form is how Workable's job finder was never asked to reveal its
    /// real one (#180). A lone email box is not a form either: join.com's posting page
    /// carries one for "Apply later", and taking it for the form is how the agent never
    /// clicked Apply, typed the candidate's email into it, and had the board email the
    /// posting's link back instead of an application (#210). Nor are the careers site's
    /// job-search and job-alert widgets, which SuccessFactors puts on every posting (#214).
    /// </summary>
    public static Task<bool> ApplicationFormVisibleAsync(IPage page) => AnyFrameAsync(page, ApplicationFormScript);

    /// <summary>The same as <see cref="ApplicationFormVisibleAsync"/>, but a file input only
    /// counts when it is on screen. Used solely for the reveal's early return (#246): a form that
    /// is in the page from load but collapsed behind an "Apply Now" link — Freshteam keeps its
    /// résumé <c>&lt;input type=file&gt;</c> and every other control hidden until the link is
    /// clicked — has a hidden file input, and the plain check would wrongly call that "on screen"
    /// and skip the click, so only the résumé field is ever discovered. Requiring the file input
    /// to be shown lets the reveal fall through and click "Apply Now" to expand the real form.</summary>
    private static Task<bool> ApplicationFormRevealedAsync(IPage page) => AnyFrameAsync(page, RevealedFormScript);

    private static async Task<bool> AnyFrameAsync(IPage page, string script)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                if (await frame.EvaluateAsync<bool>(script)) return true;
            }
            catch (PlaywrightException) { /* a frame mid-navigation */ }
        }
        return false;
    }

    // The application-form check. Only the file rule differs between the two builds below: the
    // plain check counts any file input (Greenhouse hides its résumé input behind a styled
    // button, so it must count even when not shown), the revealed check only a shown one.
    private const string ApplicationFormScriptTemplate = """
                    () => {
                      const widget = __WIDGET__;
                      const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                      let texts = 0;
                      for (const el of document.querySelectorAll('input, textarea, select')) {
                        if (widget(el)) continue;
                        const type = (el.getAttribute('type') || (el.tagName === 'SELECT' ? 'select' : 'text')).toLowerCase();
                        __FILE_RULE__
                        if (!shown(el)) continue;
                        if (el.tagName === 'TEXTAREA') return true;
                        if (type === 'text' || type === 'email' || type === 'tel' || type === 'url' || type === 'select') texts++;
                        if (texts >= 2) return true;
                      }
                      return false;
                    }
                    """;

    private static readonly string ApplicationFormScript = ApplicationFormScriptTemplate
        .Replace("__FILE_RULE__", "if (type === 'file') return true;")
        .Replace("__WIDGET__", WidgetJs);

    private static readonly string RevealedFormScript = ApplicationFormScriptTemplate
        .Replace("__FILE_RULE__", "if (type === 'file' && shown(el)) return true;")
        .Replace("__WIDGET__", WidgetJs);

    public static Task<bool> WaitForApplicationFormAsync(IPage page, int timeoutMs)
        => WaitForAsync(page, timeoutMs, ApplicationFormVisibleAsync);

    private static async Task<bool> WaitForAsync(IPage page, int timeoutMs, Func<IPage, Task<bool>> present)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (true)
        {
            if (await present(page)) return true;
            if (DateTime.UtcNow >= deadline) return false;
            await page.WaitForTimeoutAsync(500);
        }
    }

    /// <summary>
    /// Some boards hide the form behind an Apply button or link; press it when no
    /// application form is on screen yet. The click is bounded (5 s) and every way it can
    /// fail to produce a form is written to <see cref="RevealNote"/>, so the run reports
    /// "the Apply button did not respond" rather than a Playwright timeout. A link that
    /// opens the form in a new tab (Workable, SmartRecruiters) hands that tab over as
    /// <see cref="Page"/>; the route guard already holds it to the same site.
    /// </summary>
    public async Task RevealFormAsync()
    {
        // The link itself may be the board's sign-in (a direct career*.successfactors.com link).
        if (await SignInFormVisibleAsync(Page) && !await TrySignInAsync()) return;
        // A moment for a form that renders itself after load, before deciding it is hidden. A
        // hidden file input does not count as the form being on screen here (#246): Freshteam
        // ships the whole form collapsed behind "Apply Now", so counting its hidden résumé input
        // would skip the click and leave the real fields — every text, select and textarea, and
        // the Submit button — hidden and undiscovered.
        if (await WaitForAsync(Page, 3_000, ApplicationFormRevealedAsync)) return;
        ILocator? apply = null;
        foreach (var candidate in new[]
                 {
                     Page.GetByRole(AriaRole.Button, new() { NameRegex = ApplyTrigger() }).Filter(new() { HasNotTextRegex = LaterWords() }).First,
                     Page.GetByRole(AriaRole.Link, new() { NameRegex = ApplyTrigger() }).Filter(new() { HasNotTextRegex = LaterWords() }).First,
                 })
        {
            try { if (await candidate.IsVisibleAsync()) { apply = candidate; break; } }
            catch (PlaywrightException) { /* next */ }
        }
        if (apply is null)
        {
            // A page whose only controls are radio/checkbox questions and a Continue is a
            // pre-screening step, not a formless dead end — name it, so a run that cannot
            // advance it (no answer to hand) says what is in the way (#239).
            RevealNote = await AnyFieldVisibleAsync(Page) ? ""
                : await BrowserSubmitter.IsPreScreenAsync(Page)
                    ? "the page is a pre-screening step (a question and Continue) before the application form"
                    : "no Apply button or link on the page";
            return;
        }

        var popup = new TaskCompletionSource<IPage>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnPage(object? _, IPage p) => popup.TrySetResult(p);
        _context.Page += OnPage;
        // The banner may only now be up: Zoho Recruit renders its consent component after
        // the page has booted, and with it a transparent freeze layer over the whole
        // viewport — so the first dismissal, run straight after navigation, found nothing,
        // and every click on "I'm interested" then timed out under the layer (fyerx). Clear
        // it before the click, and once more if the click still would not land.
        await DismissConsentAsync(Page);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await apply.ClickAsync(new() { Timeout = 5_000 });
                break;
            }
            catch (TimeoutException)
            {
                if (attempt == 0 && await DismissConsentAsync(Page)) continue;
                _context.Page -= OnPage;
                RevealNote = "the Apply button did not respond within 5 s";
                return;
            }
            catch (PlaywrightException ex)
            {
                _context.Page -= OnPage;
                RevealNote = "clicking Apply failed: " + ex.Message.Split('\n')[0];
                return;
            }
        }
        // Gainwell's "Apply Now" only opens a menu — "Apply Now" / "Start applying with LinkedIn" —
        // and the way in is its item, not the button. Without this click the run watched an open
        // menu for ten seconds and reported "no form appeared".
        var item = Page.GetByRole(AriaRole.Menuitem, new() { NameRegex = ApplyTrigger() })
            .Filter(new() { HasNotTextRegex = new Regex(@"linkedin|indeed|google|facebook", RegexOptions.IgnoreCase) }).First;
        try
        {
            await item.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 1_500 });
            await item.ClickAsync(new() { Timeout = 5_000 });
        }
        catch (TimeoutException) { /* no menu: the button was the way in */ }
        catch (PlaywrightException) { /* ditto */ }
        // What Apply led to: the form, or the board's sign-in first (#216), or a sign-up wall
        // (#235), or nothing. A new tab is adopted whenever it arrives within the wait: Alignerr's
        // Apply opens its sign-in in a new tab only after a round trip, later than the moment
        // after the click, and a run that kept watching the posting saw nothing (#235).
        var deadline = DateTime.UtcNow.AddSeconds(10);
        var adopted = false;
        try
        {
            while (true)
            {
                if (!adopted && popup.Task.IsCompletedSuccessfully)
                {
                    adopted = true;
                    Page = popup.Task.Result;
                    try { await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10_000 }); }
                    catch (TimeoutException) { /* judged by what renders */ }
                    catch (PlaywrightException) { /* ditto */ }
                    await DismissConsentAsync(Page);
                    // A new tab is a whole app booting through the proxy: Alignerr's goes /signup →
                    // /signin behind a spinner and showed its wall well after ten seconds on pluto.
                    deadline = DateTime.UtcNow.AddSeconds(30);
                }
                // A field on screen settles it — and only then is the sign-in question asked, so a
                // page still mid-navigation cannot answer "no sign-in" a moment before it renders one.
                if (await AnyFieldVisibleAsync(Page))
                {
                    if (await SignInFormVisibleAsync(Page)) await TrySignInAsync();
                    return;
                }
                // Alignerr and its kind: Apply opens an account sign-up that only offers "Continue
                // with Google / LinkedIn" — no username box, no form, nothing the browser can fill or
                // sign in to. Name it, rather than "no form appeared", so the person knows what is
                // behind Apply.
                var providers = await SocialSignUpWallAsync(Page);
                if (providers.Count > 0)
                {
                    RevealNote = SocialWallNote(Page.Url, providers);
                    return;
                }
                if (DateTime.UtcNow >= deadline) break;
                await Page.WaitForTimeoutAsync(500);
            }
        }
        finally { _context.Page -= OnPage; }
        var refused = Refused;
        RevealNote = refused.Count > 0
            ? AccountOnlyAts(refused[0]) is { } ats
                ? $"Apply leads to {refused[0]} ({ats}), which only takes applications from a signed-in candidate account — save yours under Settings · Agent · Board accounts and the browser will sign in, or apply by Copy answers and open the posting"
                : $"Apply led off the posting's site to {refused[0]}, which the browser refuses to follow"
            : "Apply was clicked but no form appeared within 10 s";
    }

    // A sign-up wall that is only identity providers: the visible "Continue / Sign in / Sign
    // up with <provider>" buttons or links, when nothing else on the page can be typed into.
    private const string SocialWallScript = """
        () => {
          const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const typed = [...document.querySelectorAll('input[type=text], input[type=email], input[type=password], input:not([type]), textarea, select')].some(shown);
          if (typed) return [];
          const re = /^\s*(?:continue|sign\s*in|sign\s*up|log\s*in|register|join)\s+(?:with|using|via)\s+(google|linkedin|apple|microsoft|github|facebook|okta)\b/i;
          const found = [];
          for (const el of document.querySelectorAll('button, a, [role=button]')) {
            if (!shown(el)) continue;
            const m = re.exec((el.innerText || el.getAttribute('aria-label') || '').trim());
            if (!m) continue;
            const name = m[1][0].toUpperCase() + m[1].slice(1).toLowerCase();
            const known = { Linkedin: 'LinkedIn', Github: 'GitHub' }[name] || name;
            if (!found.includes(known)) found.push(known);
          }
          return found;
        }
        """;

    /// <summary>The verdict for a page that is only identity providers: what Apply led to and
    /// that the posting is the person's by Copy answers and open.</summary>
    public static string SocialWallNote(string url, IReadOnlyList<string> providers)
    {
        var at = Uri.TryCreate(url, UriKind.Absolute, out var wall) ? wall.Host : "the page";
        return $"Apply leads to {at}, which only signs candidates up through {string.Join(", ", providers)} — there is no application form for the browser to fill; create the account yourself and apply by Copy answers and open the posting";
    }

    /// <summary>The identity providers a page offers as its only way in ("Continue with
    /// Google"), in every frame — or nothing when the page has a box to type into.</summary>
    public static async Task<IReadOnlyList<string>> SocialSignUpWallAsync(IPage page)
    {
        foreach (var frame in page.Frames)
        {
            try
            {
                var found = await frame.EvaluateAsync<string[]>(SocialWallScript);
                if (found is { Length: > 0 }) return found;
            }
            catch (PlaywrightException) { /* a frame mid-navigation */ }
        }
        return [];
    }

    // The board's own sign-in: a password box beside a username or email box. Never a
    // widget's, and never taken for the application form.
    private static readonly string SignInFormScript = """
        () => {
          const widget = __WIDGET__;
          const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
            return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
          const pw = [...document.querySelectorAll('input[type=password]')].some(shown);
          if (!pw) return false;
          return [...document.querySelectorAll('input[type=text], input[type=email], input:not([type])')].some(el => shown(el) && !widget(el));
        }
        """.Replace("__WIDGET__", WidgetJs);

    /// <summary>Is the page showing a sign-in (a password box with a username box)?</summary>
    public static async Task<bool> SignInFormVisibleAsync(IPage page) => await SignInFormStateAsync(page) == true;

    /// <summary>The same, but null when the page cannot be read (mid-navigation).</summary>
    private static async Task<bool?> SignInFormStateAsync(IPage page)
    {
        try { return await page.MainFrame.EvaluateAsync<bool>(SignInFormScript); }
        catch (PlaywrightException) { return null; }
    }

    [GeneratedRegex(@"^\s*(?:sign in|log ?in|login|continue|submit|next|weiter|anmelden|connexion|se connecter|iniciar sesi[oó]n|entrar)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SignInWords();

    /// <summary>
    /// Sign in to the board with the candidate's own account for this host (#216): type the
    /// username and password, press Sign In, and wait for the password box to go. True when
    /// the page moved on; false — with the reason in <see cref="RevealNote"/> — when there is
    /// no account for this host or the board refused the sign-in. Never creates an account.
    /// </summary>
    private async Task<bool> TrySignInAsync()
    {
        var host = Uri.TryCreate(Page.Url, UriKind.Absolute, out var here) ? here.Host : "";
        var account = BoardAccount.For(_accounts, host);
        var ats = AccountOnlyAts(host);
        if (account is null)
        {
            RevealNote = $"Apply leads to a sign-in at {host}{(ats is null ? "" : $" ({ats})")} and no account is saved for it — save yours under Settings · Agent · Board accounts and the browser will sign in, or apply by Copy answers and open the posting";
            return false;
        }
        if (account.Password.Length == 0)
        {
            RevealNote = $"the sign-in at {host} asks for a password and the account saved for it ({account.Username}) has none — add it under Settings · Agent · Board accounts";
            return false;
        }
        try
        {
            // The username box, best name first — a CSS list is matched in DOM order, and the
            // careers site's search box comes before the sign-in on the page. Never a widget's.
            const string notWidget = ":not([type='search']):not([name*='search' i]):not([id*='search' i]):not([placeholder*='search' i]):not([class*='search' i])";
            ILocator? userBox = null;
            foreach (var sel in new[]
                     {
                         "#username", "input[name='username']", "input[autocomplete='username']", "input[type='email']",
                         "input[name*='user' i]", "input[name*='email' i]", "input[id*='email' i]", "input[type='text']",
                     })
            {
                var c = Page.Locator(sel + notWidget);
                var n = Math.Min(await c.CountAsync(), 6);
                for (var i = 0; i < n && userBox is null; i++)
                    if (await c.Nth(i).IsVisibleAsync() && await c.Nth(i).IsEditableAsync()) userBox = c.Nth(i);
                if (userBox is not null) break;
            }
            var pass = Page.Locator("input[type='password']:visible").First;
            if (userBox is null || await pass.CountAsync() == 0)
            {
                RevealNote = $"the sign-in at {host} has no username or password box the browser can find";
                return false;
            }
            await userBox.FillAsync(account.Username, new() { Timeout = 5_000 });
            await pass.FillAsync(account.Password, new() { Timeout = 5_000 });
            var notIdp = new LocatorFilterOptions { HasNotTextRegex = new Regex(@"google|linkedin|apple|microsoft|facebook|forgot|create|register", RegexOptions.IgnoreCase) };
            ILocator? go = null;
            foreach (var c in new[]
                     {
                         Page.GetByRole(AriaRole.Button, new() { NameRegex = SignInWords() }).Filter(notIdp).First,
                         Page.Locator("form button[type=submit], form input[type=submit], button[type=submit], input[type=submit]").Filter(notIdp).First,
                     })
            {
                try { if (await c.CountAsync() > 0 && await c.IsVisibleAsync()) { go = c; break; } }
                catch (PlaywrightException) { /* next */ }
            }
            var urlBefore = Page.Url;
            string textBefore;
            try { textBefore = await Page.InnerTextAsync("body"); } catch (PlaywrightException) { textBefore = ""; }
            if (go is not null) await go.ClickAsync(new() { Timeout = 5_000 });
            else await pass.PressAsync("Enter");
            // What the board answered: the sign-in gone (signed in), or still there on a page
            // that changed (refused, with its complaint), or unchanged until the deadline. A
            // page that cannot be read mid-navigation answers nothing and is asked again.
            var deadline = DateTime.UtcNow.AddSeconds(20);
            var signedIn = false;
            await Page.WaitForTimeoutAsync(800);
            while (DateTime.UtcNow < deadline)
            {
                var state = await SignInFormStateAsync(Page);
                if (state == false) { signedIn = true; break; }
                if (state == true)
                {
                    string text;
                    try { text = await Page.InnerTextAsync("body"); } catch (PlaywrightException) { text = textBefore; }
                    if (Page.Url != urlBefore || text != textBefore) break;
                }
                await Page.WaitForTimeoutAsync(500);
            }
            if (!signedIn)
            {
                var complaint = await Page.EvaluateAsync<string>("""
                    () => [...document.querySelectorAll('[role=alert], .error, .errorMessage, [class*="error" i], [class*="invalid" i]')]
                      .map(e => (e.innerText || '').trim()).filter(t => t && t.length < 240).join('; ').slice(0, 240)
                    """);
                RevealNote = $"the sign-in at {host} as {account.Username} was refused" + (complaint.Length > 0 ? $": {complaint}" : "") + " — check the saved password under Settings · Agent · Board accounts";
                return false;
            }
            SignedInAs = account;
            try { await Page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new() { Timeout = 10_000 }); }
            catch (TimeoutException) { /* judged by what renders */ }
            catch (PlaywrightException) { /* ditto */ }
            await DismissConsentAsync(Page);
            await WaitForFieldAsync(Page, 10_000);
            return true;
        }
        catch (PlaywrightException ex)
        {
            RevealNote = $"signing in at {host} failed: " + ex.Message.Split('\n')[0];
            return false;
        }
        catch (TimeoutException)
        {
            RevealNote = $"the sign-in at {host} did not respond";
            return false;
        }
    }

    /// <summary>
    /// Open every collapsed section of a form that arrives folded — SuccessFactors' accordion
    /// (<c>…:topBar</c> headers), or any disclosure button whose region holds form controls.
    /// Navigation menus, which also carry <c>aria-expanded</c>, are left alone.
    /// </summary>
    public static async Task ExpandSectionsAsync(IPage page)
    {
        try
        {
            for (var round = 0; round < 3; round++)
            {
                var ids = await page.MainFrame.EvaluateAsync<string[]>("""
                    () => {
                      const shown = el => { const r = el.getBoundingClientRect(); const s = getComputedStyle(el);
                        return r.width > 0 && r.height > 0 && s.visibility !== 'hidden' && s.display !== 'none'; };
                      const out = [];
                      let i = 0;
                      for (const el of document.querySelectorAll('[aria-expanded="false"]')) {
                        if (!shown(el)) continue;
                        const controls = el.getAttribute('aria-controls');
                        const region = controls ? document.getElementById(controls) : null;
                        const isTopBar = /topBar$/i.test(el.id || '');
                        const holdsForm = region && region.querySelector('input, select, textarea');
                        if (!isTopBar && !holdsForm) continue;
                        if (!el.id) el.id = 'at-expand-' + (i++);
                        out.push(el.id);
                      }
                      return out;
                    }
                    """);
                if (ids.Length == 0) return;
                foreach (var id in ids)
                {
                    // Scrolled into view and clicked through whatever floats over it: SuccessFactors'
                    // sticky footer bar sat over two of Kiewit's section headers and a plain click
                    // timed out on both.
                    var bar = page.Locator($"[id=\"{id}\"]");
                    try { await bar.ScrollIntoViewIfNeededAsync(new() { Timeout = 2_000 }); } catch (PlaywrightException) { } catch (TimeoutException) { }
                    try { await bar.ClickAsync(new() { Timeout = 3_000, Force = true }); }
                    catch (PlaywrightException) { /* next */ }
                    catch (TimeoutException) { /* next */ }
                    await page.WaitForTimeoutAsync(700);
                }
            }
        }
        catch (PlaywrightException) { /* a page mid-navigation: nothing to open */ }
    }

    public async ValueTask DisposeAsync()
    {
        try { await _context.DisposeAsync(); } catch (PlaywrightException) { }
        try { await _browser.DisposeAsync(); } catch (PlaywrightException) { }
        _playwright.Dispose();
    }
}
