// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>The one seam the worker calls; a worker-level test stands in for the browser.</summary>
public interface IHandshakeSessionRenewer
{
    /// <inheritdoc cref="HandshakeSessionRenewer.RenewAsync"/>
    Task<PortalSession> RenewAsync(string username, string password, CancellationToken ct);
}

/// <summary>
/// Signs in to Handshake the way a person does, in the agent's browser, with the board
/// account saved for <c>joinhandshake.com</c>, and hands back the session the poller then
/// searches with (#267): the <c>hss-global</c> cookie (about ninety days) as one JSON
/// string sealed on the account row. Nothing here reads Handshake's data; the poller does
/// that.
///
/// The sign-in is two steps. The school email goes first, because that is how Handshake
/// finds the school; the school's own form comes back and takes the same address
/// ("School username or email"), so one saved value serves both. A bare username cannot
/// be used: Handshake has nothing to route on.
///
/// <para><b>Which schools this drives.</b> A school whose sign-in is a plain username and
/// password form on Handshake's own page. A school that federates to Microsoft, Okta,
/// Shibboleth or Google, or that requires a second factor, is refused by name rather than
/// left to time out with a message that blames Handshake.</para>
/// </summary>
public sealed partial class HandshakeSessionRenewer : IHandshakeSessionRenewer
{
    public const string SignInUrl = "https://app.joinhandshake.com/access";
    public const string CookieName = "hss-global";
    /// <summary>The cookie is issued for about ninety days; renew well inside that.</summary>
    public static readonly TimeSpan SessionLife = TimeSpan.FromDays(90);

    [GeneratedRegex(@"^\s*continue with email\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex EmailStepButton();

    [GeneratedRegex(@"^\s*continue\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex ContinueButton();

    [GeneratedRegex(@"wrong|incorrect|invalid|doesn.t match|couldn.t find|unable to|try again|no account|not recognized", RegexOptions.IgnoreCase)]
    private static partial Regex Refusal();

    /// <summary>An identity provider this renewer does not drive, or a second factor.</summary>
    [GeneratedRegex(@"microsoftonline|okta|shibboleth|auth0|duo|verification code|two-factor|two factor|authenticator|approve (this|the) (request|sign-in)", RegexOptions.IgnoreCase)]
    private static partial Regex ForeignSignIn();

    private readonly BrowserOptions _options;
    private readonly ILogger<HandshakeSessionRenewer> _log;
    private readonly string _signInUrl;
    private readonly int _stepWaitSeconds;

    /// <param name="signInUrl">Tests only: a loopback stand-in for Handshake's sign-in page.</param>
    /// <param name="stepWaitSeconds">How long to wait for the school's form, then for the cookie.</param>
    public HandshakeSessionRenewer(BrowserOptions options, ILogger<HandshakeSessionRenewer> log,
        string signInUrl = SignInUrl, int stepWaitSeconds = 60)
    {
        _options = options;
        _log = log;
        _signInUrl = signInUrl;
        _stepWaitSeconds = stepWaitSeconds;
    }

    /// <summary>The kept session's plaintext: the cookie as JSON, the way the poller reads it.</summary>
    public static string Encode(string cookie) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { [CookieName] = cookie });

    /// <summary>
    /// Open Handshake's sign-in, give it the school email, then fill the school's own form
    /// with the same address and the password, and return the session once
    /// <c>hss-global</c> is set. Throws <see cref="PortalRenewalException"/> with the step
    /// it stopped at — naming the host it landed on, so a school this does not drive is
    /// legible rather than a mystery timeout.
    /// </summary>
    public async Task<PortalSession> RenewAsync(string username, string password, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new PortalRenewalException("the Handshake board account has no password saved");
        if (!username.Contains('@'))
            throw new PortalRenewalException(
                "the Handshake board account needs the school email address, not a bare username — Handshake finds the school by it");

        await using var session = await BrowserSession.OpenAsync(_options, _signInUrl, ct);
        var page = session.Page;

        // Step one: the school email. Named selectors first — a union with `.First` will
        // happily fill whatever search box the page happens to render first.
        var email = await FirstPresentAsync(page,
            "input[type=email]:visible", "input[name*=email i]:visible", "input[type=text]:visible");
        if (email is null)
            throw new PortalRenewalException(
                $"Handshake's sign-in page at {HostOf(page.Url)} shows no email box");
        try
        {
            await email.FillAsync(username, new() { Timeout = 10_000 });
        }
        catch (PlaywrightException ex)
        {
            throw new PortalRenewalException("the email box could not be filled: " + FirstLine(ex));
        }
        await PressAsync(page, EmailStepButton(), email, "Continue with email");

        // Whatever the school does next is the school's choice, not Handshake's: record
        // where we landed so the commonest failure names itself.
        var landed = HostOf(page.Url);

        // Step two: the school's own form.
        var pass = await FirstPresentAsync(page, "input[type=password]:visible");
        if (pass is null)
        {
            var text = landed + " " + await BodyTextAsync(page);
            if (ForeignSignIn().IsMatch(text))
                throw new PortalRenewalException(
                    $"the sign-in for {username} went to {landed}, which wants an identity provider or a second factor the agent cannot complete — sign in yourself and keep the session, or use a school whose sign-in is a plain username and password form");
            throw new PortalRenewalException(
                $"after the email step we landed on {landed}, which this renewer does not drive — it handles a school's own username-and-password form only");
        }
        var schoolUser = await FirstPresentAsync(page,
            "input[name*=user i]:visible", "input[name*=login i]:visible",
            "input[type=email]:visible", "input[type=text]:visible");
        if (schoolUser is null)
            throw new PortalRenewalException($"the sign-in form at {landed} shows no username box");
        try
        {
            await schoolUser.FillAsync(username, new() { Timeout = 10_000 });
            await pass.FillAsync(password, new() { Timeout = 5_000 });
        }
        catch (PlaywrightException ex)
        {
            throw new PortalRenewalException(
                "the school's sign-in form could not be filled: " + FirstLine(ex));
        }

        // The cookie has to be *earned*: remember what was there before the password, so a
        // school that hands out an anonymous session cookie on first load cannot be
        // mistaken for a signed-in one.
        var before = (await session.CookiesAsync(_signInUrl))
            .FirstOrDefault(c => c.Name == CookieName)?.Value ?? "";
        await PressAsync(page, ContinueButton(), pass, "Continue");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _stepWaitSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var cookie = (await session.CookiesAsync(_signInUrl))
                .FirstOrDefault(c => c.Name == CookieName && c.Value.Length > 0);
            if (cookie is not null && cookie.Value != before)
            {
                var expires = DateTimeOffset.UtcNow + SessionLife;
                if (cookie.Expires > 0)
                {
                    var cookieEnd = DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires).AddDays(-1);
                    if (cookieEnd < expires) expires = cookieEnd;
                }
                _log.LogInformation("Handshake: signed in as {User}; session kept until {Until:u}", username, expires);
                return new PortalSession(Encode(cookie.Value), expires);
            }
            if (Refusal().IsMatch(await ErrorTextAsync(page)))
                throw new PortalRenewalException("Handshake refused the sign-in — " + await ComplaintAsync(page));
            await page.WaitForTimeoutAsync(2_000);
        }
        throw new PortalRenewalException(
            $"the sign-in did not finish at {HostOf(page.Url)} — " + await ComplaintAsync(page));
    }

    /// <summary>The first of these selectors that is actually on screen, or null.</summary>
    private static async Task<ILocator?> FirstPresentAsync(IPage page, params string[] selectors)
    {
        foreach (var selector in selectors)
        {
            var locator = page.Locator(selector).First;
            try
            {
                if (await locator.CountAsync() > 0 && await locator.IsVisibleAsync()) return locator;
            }
            catch (PlaywrightException) { /* mid-render; try the next */ }
        }
        return null;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>Press the named button, or fall back to Enter in the last field filled.</summary>
    private static async Task PressAsync(IPage page, Regex name, ILocator fallback, string what)
    {
        var go = page.GetByRole(AriaRole.Button, new() { NameRegex = name }).First;
        try
        {
            if (await go.CountAsync() > 0 && await go.IsVisibleAsync()) await go.ClickAsync(new() { Timeout = 5_000 });
            else await fallback.PressAsync("Enter");
        }
        catch (PlaywrightException ex)
        {
            throw new PortalRenewalException($"{what} could not be pressed: " + FirstLine(ex));
        }
    }

    private static string FirstLine(PlaywrightException ex) => ex.Message.Split('\n')[0];

    private static async Task<string> BodyTextAsync(IPage page)
    {
        try { return await page.EvaluateAsync<string>("() => (document.body.innerText || '').replace(/\\s+/g, ' ').trim()"); }
        catch (PlaywrightException) { return ""; }
    }

    private static async Task<string> ErrorTextAsync(IPage page)
    {
        try
        {
            return await page.EvaluateAsync<string>("""
                () => {
                  const shown = el => { const r = el.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
                  return [...document.querySelectorAll('[role=alert], [class*="error" i], [class*="invalid" i], [id*="error" i]')]
                    .filter(shown).map(e => (e.innerText || '').trim()).filter(t => t && t.length < 240).join('; ');
                }
                """);
        }
        catch (PlaywrightException) { return ""; }
    }

    /// <summary>What the page is saying, for the error: an alert or error line, else its first words.</summary>
    private static async Task<string> ComplaintAsync(IPage page)
    {
        var errs = await ErrorTextAsync(page);
        if (errs.Length > 0) return $"the page says: {errs}";
        var text = await BodyTextAsync(page);
        return text.Length > 0 ? $"the page says: {text[..Math.Min(200, text.Length)]}" : $"at {page.Url}";
    }
}
