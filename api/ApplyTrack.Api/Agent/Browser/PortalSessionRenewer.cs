// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>A signed-in MyGreenhouse session: the portal's cookie and when to stop trusting it.</summary>
public sealed record PortalSession(string Cookie, DateTimeOffset ExpiresAt);

/// <summary>The renewal did not end with a session; the message says where it stopped.</summary>
public sealed class PortalRenewalException(string message) : Exception(message);

/// <summary>The one seam the worker calls; a worker-level test stands in for the browser.</summary>
public interface IPortalSessionRenewer
{
    /// <inheritdoc cref="PortalSessionRenewer.RenewAsync"/>
    Task<PortalSession> RenewAsync(string email, Func<DateTimeOffset, CancellationToken, Task<string?>> readCode, CancellationToken ct);
}

/// <summary>
/// Signs in to MyGreenhouse — Greenhouse's candidate portal, the poller's signed-in
/// discovery source (#218) — the way a person does, in the agent's browser, and hands
/// back the session cookie the poller then searches with (#221). The portal's sign-in is
/// passwordless: it emails a security code. A plain HTTP client's request for one is
/// answered with the same redirect a browser gets and no mail ever follows (four tries
/// with browser-identical headers, 2026-09-14), while a headless browser's click on
/// <b>Send security code</b> brings the mail within seconds. So the browser presses it,
/// the tenant's mailbox is read for the code, the code goes into the boxes, and the
/// <c>_session_id</c> cookie the portal sets is the result. Nothing here reads the
/// portal's data; the poller does that.
/// </summary>
public sealed partial class PortalSessionRenewer : IPortalSessionRenewer
{
    public const string SignInUrl = "https://my.greenhouse.io/users/sign_in";
    public const string CookieName = "_session_id";
    /// <summary>The cookie lasts a fortnight; renewal is due a day before that runs out.</summary>
    public static readonly TimeSpan SessionLife = TimeSpan.FromDays(13);

    [GeneratedRegex(@"^\s*(?:send(?: me)?(?: the| a| my)? (?:security |verification )?code|continue|next|sign in)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SendCode();

    [GeneratedRegex(@"google|linkedin|apple|microsoft|facebook", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityProviders();

    private readonly BrowserOptions _options;
    private readonly ILogger<PortalSessionRenewer> _log;
    private readonly string _signInUrl;
    private readonly int _codeWaitSeconds;

    /// <param name="signInUrl">Tests only: a loopback stand-in for the portal's sign-in page.</param>
    /// <param name="codeWaitSeconds">How long to keep reading the mailbox for the code.</param>
    public PortalSessionRenewer(BrowserOptions options, ILogger<PortalSessionRenewer> log,
        string signInUrl = SignInUrl, int codeWaitSeconds = 180)
    {
        _options = options;
        _log = log;
        _signInUrl = signInUrl;
        _codeWaitSeconds = codeWaitSeconds;
    }

    /// <summary>
    /// Open the portal's sign-in, type <paramref name="email"/>, press Send security code,
    /// take what <paramref name="readCode"/> finds in the mailbox for mail after the press
    /// (the code, or the sign-in link when the mail carries one), finish the sign-in in the
    /// same browser, and return the session cookie. Throws <see cref="PortalRenewalException"/>
    /// with the step it stopped at.
    /// </summary>
    public async Task<PortalSession> RenewAsync(string email, Func<DateTimeOffset, CancellationToken, Task<string?>> readCode, CancellationToken ct)
    {
        await using var session = await BrowserSession.OpenAsync(_options, _signInUrl, ct);
        var page = session.Page;
        var box = page.Locator("input[type=email]:visible, input[name='email']:visible").First;
        try { await box.FillAsync(email, new() { Timeout = 10_000 }); }
        catch (PlaywrightException ex) { throw new PortalRenewalException("the portal's sign-in page shows no email box: " + ex.Message.Split('\n')[0]); }
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var go = page.GetByRole(AriaRole.Button, new() { NameRegex = SendCode() })
            .Filter(new() { HasNotTextRegex = IdentityProviders() }).First;
        try
        {
            if (await go.CountAsync() > 0 && await go.IsVisibleAsync()) await go.ClickAsync(new() { Timeout = 5_000 });
            else await box.PressAsync("Enter");
        }
        catch (PlaywrightException ex) { throw new PortalRenewalException("Send security code could not be pressed: " + ex.Message.Split('\n')[0]); }

        // The boxes the code goes into, or the portal's complaint.
        var deadline = DateTime.UtcNow.AddSeconds(15);
        var codeBox = false;
        while (!codeBox && DateTime.UtcNow < deadline)
        {
            foreach (var frame in page.Frames)
                if (await BrowserSubmitter.HasSignInCodeBoxAsync(frame)) { codeBox = true; break; }
            if (!codeBox) await page.WaitForTimeoutAsync(500);
        }
        if (!codeBox)
            throw new PortalRenewalException("Send security code was pressed but no code box followed — " + await ComplaintAsync(page));
        _log.LogInformation("MyGreenhouse: security code requested for {Email}; reading the mailbox", email);

        string? reply = null;
        var codeDeadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _codeWaitSeconds));
        while (reply is null && DateTime.UtcNow < codeDeadline)
        {
            ct.ThrowIfCancellationRequested();
            reply = (await readCode(since, ct) ?? "").Trim();
            if (reply.Length == 0) { reply = null; await Task.Delay(TimeSpan.FromSeconds(8), ct); }
        }
        if (reply is null)
            throw new PortalRenewalException($"no security code arrived in the mailbox for {email} within {_codeWaitSeconds} s");

        if (Uri.TryCreate(reply, UriKind.Absolute, out var link) && (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps))
        {
            // The mail carried a sign-in link: followed only onto the portal's own site.
            if (!Uri.TryCreate(_signInUrl, UriKind.Absolute, out var portal) || !BrowserSession.HostAllowed(link.Host, portal.Host))
                throw new PortalRenewalException($"the mailed sign-in link points off the portal ({link.Host}) — not followed");
            try { await page.GotoAsync(link.ToString(), new() { Timeout = 20_000, WaitUntil = WaitUntilState.DOMContentLoaded }); }
            catch (PlaywrightException ex) { throw new PortalRenewalException("the mailed sign-in link could not be opened: " + ex.Message.Split('\n')[0]); }
        }
        else if (!await BrowserSubmitter.EnterSignInCodeAsync(page, reply))
            throw new PortalRenewalException("the security code was not taken — " + await ComplaintAsync(page));

        // Signed in: the sign-in page is gone, or its code boxes are.
        deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var still = false;
            foreach (var frame in page.Frames)
                if (await BrowserSubmitter.HasSignInCodeBoxAsync(frame)) { still = true; break; }
            if (!still && !page.Url.Contains("/users/sign_in", StringComparison.OrdinalIgnoreCase)) break;
            await page.WaitForTimeoutAsync(500);
        }
        var cookies = await session.CookiesAsync(_signInUrl);
        var cookie = cookies.FirstOrDefault(c => c.Name == CookieName && c.Value.Length > 0);
        if (cookie is null)
            throw new PortalRenewalException("the code was entered but the portal set no session — " + await ComplaintAsync(page));
        _log.LogInformation("MyGreenhouse: signed in as {Email}; session kept until {Until:u}", email, DateTimeOffset.UtcNow + SessionLife);
        return new PortalSession(cookie.Value, DateTimeOffset.UtcNow + SessionLife);
    }

    /// <summary>What the page is saying, for the error: an alert or error line, else its first words.</summary>
    private static async Task<string> ComplaintAsync(IPage page)
    {
        try
        {
            var text = await page.EvaluateAsync<string>("""
                () => {
                  const shown = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
                  const errs = [...document.querySelectorAll('[role=alert], [class*="error" i], [class*="invalid" i]')]
                    .filter(shown).map(e => (e.innerText || '').trim()).filter(t => t && t.length < 240);
                  return errs.length ? errs.join('; ') : (document.body.innerText || '').replace(/\s+/g, ' ').trim().slice(0, 200);
                }
                """);
            return text.Length > 0 ? $"the page says: {text}" : $"at {page.Url}";
        }
        catch (PlaywrightException) { return $"at {page.Url}"; }
    }
}
