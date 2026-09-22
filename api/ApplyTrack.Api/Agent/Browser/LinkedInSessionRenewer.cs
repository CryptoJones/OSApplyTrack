// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>The one seam the worker calls; a worker-level test stands in for the browser.</summary>
public interface ILinkedInSessionRenewer
{
    /// <inheritdoc cref="LinkedInSessionRenewer.RenewAsync"/>
    Task<PortalSession> RenewAsync(string username, string password,
        Func<DateTimeOffset, CancellationToken, Task<string?>>? readCode,
        Func<string, CancellationToken, Task>? notify, CancellationToken ct);
}

/// <summary>
/// Signs in to LinkedIn the way a person does, in the agent's browser, with the board
/// account saved for <c>linkedin.com</c>, and hands back the session the poller then
/// searches with (#233): the <c>li_at</c> cookie (a year long) and the <c>JSESSIONID</c>
/// the site's own endpoints want echoed as a CSRF token, as one JSON string sealed on
/// the account row. A sign-in from a device LinkedIn has not seen is challenged: a tap
/// in the LinkedIn app ("Check your LinkedIn app" — the person is asked for it by a moo
/// and the browser waits), or an emailed PIN (read from the tenant's mailbox and typed
/// in). Nothing here reads LinkedIn's data; the poller does that.
/// </summary>
public sealed partial class LinkedInSessionRenewer : ILinkedInSessionRenewer
{
    public const string SignInUrl = "https://www.linkedin.com/login";
    public const string CookieName = "li_at";
    public const string CsrfCookieName = "JSESSIONID";
    /// <summary>The cookie lasts a year; the kept session is renewed well inside that.</summary>
    public static readonly TimeSpan SessionLife = TimeSpan.FromDays(300);
    /// <summary>What the moo says when LinkedIn wants the tap in its app.</summary>
    public const string AppTapMessage = "🔐 LinkedIn wants you to confirm the agent's sign-in: open the LinkedIn app on your phone and tap Yes on the sign-in prompt — it waits 4 minutes. "
        + "Missed it? Press Sign in now under Settings · Agent · Board accounts when you have your phone, and the prompt comes again within a minute.";

    [GeneratedRegex(@"^\s*sign in\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SignInButton();

    [GeneratedRegex(@"linkedin app|signed.in devices|tap yes|approve", RegexOptions.IgnoreCase)]
    private static partial Regex AppChallenge();

    [GeneratedRegex(@"wrong|incorrect|not (?:the )?right|doesn.t match|couldn.t find|unable to|try again|restricted|unusual|blocked", RegexOptions.IgnoreCase)]
    private static partial Regex Refusal();

    private readonly BrowserOptions _options;
    private readonly ILogger<LinkedInSessionRenewer> _log;
    private readonly string _signInUrl;
    private readonly int _challengeWaitSeconds;

    /// <param name="signInUrl">Tests only: a loopback stand-in for LinkedIn's sign-in page.</param>
    /// <param name="challengeWaitSeconds">How long to wait for the person's tap or the mailed PIN.</param>
    public LinkedInSessionRenewer(BrowserOptions options, ILogger<LinkedInSessionRenewer> log,
        string signInUrl = SignInUrl, int challengeWaitSeconds = 240)
    {
        _options = options;
        _log = log;
        _signInUrl = signInUrl;
        _challengeWaitSeconds = challengeWaitSeconds;
    }

    /// <summary>The kept session's plaintext: the two cookies as JSON, the way the poller reads it.</summary>
    public static string Encode(string liAt, string jsessionId) =>
        JsonSerializer.Serialize(new Dictionary<string, string> { ["li_at"] = liAt, ["JSESSIONID"] = jsessionId });

    /// <summary>
    /// Open LinkedIn's sign-in, type <paramref name="username"/> and <paramref name="password"/>,
    /// press Sign in, then see the challenge through: a PIN box is answered with what
    /// <paramref name="readCode"/> finds in the mailbox; a "check your LinkedIn app" page is
    /// announced once through <paramref name="notify"/> and waited on. Returns the session
    /// once the <c>li_at</c> cookie is set. Throws <see cref="PortalRenewalException"/> with
    /// the step it stopped at.
    /// </summary>
    public async Task<PortalSession> RenewAsync(string username, string password,
        Func<DateTimeOffset, CancellationToken, Task<string?>>? readCode,
        Func<string, CancellationToken, Task>? notify, CancellationToken ct)
    {
        if (password.Length == 0)
            throw new PortalRenewalException("the LinkedIn board account has no password saved");
        await using var session = await BrowserSession.OpenAsync(_options, _signInUrl, ct);
        var page = session.Page;
        var user = page.Locator("input[autocomplete='username']:visible, input[type=email]:visible, input[type=text]:visible, input:not([type]):visible").First;
        var pass = page.Locator("input[type=password]:visible").First;
        try
        {
            await user.FillAsync(username, new() { Timeout = 10_000 });
            await pass.FillAsync(password, new() { Timeout = 5_000 });
        }
        catch (PlaywrightException ex) { throw new PortalRenewalException("LinkedIn's sign-in page shows no username and password boxes: " + ex.Message.Split('\n')[0]); }
        var since = DateTimeOffset.UtcNow.AddMinutes(-1);
        var go = page.GetByRole(AriaRole.Button, new() { NameRegex = SignInButton() }).First;
        try
        {
            if (await go.CountAsync() > 0 && await go.IsVisibleAsync()) await go.ClickAsync(new() { Timeout = 5_000 });
            else await pass.PressAsync("Enter");
        }
        catch (PlaywrightException ex) { throw new PortalRenewalException("Sign in could not be pressed: " + ex.Message.Split('\n')[0]); }

        var announced = false;
        var pinTried = false;
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(10, _challengeWaitSeconds));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var cookies = await session.CookiesAsync(_signInUrl);
            var liAt = cookies.FirstOrDefault(c => c.Name == CookieName && c.Value.Length > 0);
            if (liAt is not null)
            {
                var csrf = cookies.FirstOrDefault(c => c.Name == CsrfCookieName)?.Value ?? "";
                var expires = DateTimeOffset.UtcNow + SessionLife;
                if (liAt.Expires > 0)
                {
                    var cookieEnd = DateTimeOffset.FromUnixTimeSeconds((long)liAt.Expires).AddDays(-1);
                    if (cookieEnd < expires) expires = cookieEnd;
                }
                _log.LogInformation("LinkedIn: signed in as {User}; session kept until {Until:u}", username, expires);
                return new PortalSession(Encode(liAt.Value, csrf), expires);
            }

            // A PIN box: the mail carries the code.
            var codeBox = false;
            foreach (var frame in page.Frames)
                if (await BrowserSubmitter.HasSignInCodeBoxAsync(frame)) { codeBox = true; break; }
            if (codeBox && !pinTried)
            {
                pinTried = true;
                if (readCode is null)
                    throw new PortalRenewalException($"LinkedIn emailed a verification PIN to {username} and there is no mailbox to read it from — save one under Settings · Notifications");
                _log.LogInformation("LinkedIn: a verification PIN was sent to {User}; reading the mailbox", username);
                string? pin = null;
                while (pin is null && DateTime.UtcNow < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    pin = (await readCode(since, ct) ?? "").Trim();
                    if (pin.Length == 0) { pin = null; await Task.Delay(TimeSpan.FromSeconds(8), ct); }
                }
                if (pin is null)
                    throw new PortalRenewalException($"no verification PIN arrived in the mailbox for {username} within {_challengeWaitSeconds} s");
                if (!await BrowserSubmitter.EnterSignInCodeAsync(page, pin))
                    throw new PortalRenewalException("the verification PIN was not taken — " + await ComplaintAsync(page));
                continue;
            }

            var text = await BodyTextAsync(page);
            if (!announced && AppChallenge().IsMatch(text))
            {
                announced = true;
                _log.LogInformation("LinkedIn: the sign-in for {User} waits on a tap in the LinkedIn app", username);
                if (notify is not null) await notify(AppTapMessage, ct);
            }
            else if (!announced && !codeBox && Refusal().IsMatch(await ErrorTextAsync(page)))
                throw new PortalRenewalException("LinkedIn refused the sign-in — " + await ComplaintAsync(page));
            await page.WaitForTimeoutAsync(2_000);
        }
        throw new PortalRenewalException(announced
            ? $"LinkedIn waited {_challengeWaitSeconds} s for the tap in the LinkedIn app and none came — run again when you can tap Yes"
            : "the sign-in did not finish — " + await ComplaintAsync(page));
    }

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
