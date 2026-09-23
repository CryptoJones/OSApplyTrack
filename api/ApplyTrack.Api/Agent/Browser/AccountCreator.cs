// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>What an attempt to create a candidate account came to.</summary>
/// <param name="Account">The account, proved by signing in with it — set only then. An unproved
/// credential is never handed back to be stored: at Allstate, "Create Account → Sign In with no
/// error" was an email that already had an account, which Workday declines without a word.</param>
/// <param name="Note">What happened, in words for the Errors view and the audit event.</param>
public sealed record AccountCreation(BoardAccount? Account, string Note, bool AlreadyExists = false);

/// <summary>The account-creation seam the worker calls, so a worker-level test needs no browser.</summary>
public interface IAccountCreator
{
    /// <summary>The ATS a sign-up recipe exists for at <paramref name="host"/>, or null.</summary>
    string? Recipe(string host);

    /// <inheritdoc cref="AccountCreator.CreateAsync"/>
    Task<AccountCreation> CreateAsync(string host, string postingLink, string email,
        Func<CodeRequest, CancellationToken, Task<string?>> relay, CancellationToken ct);
}

/// <summary>
/// Creates a candidate account on an ATS that only takes applications from one (#277):
/// register with the tenant's application email and a strong generated password, complete the
/// email verification from the tenant's mailbox, and prove the account by signing in with it
/// in a fresh browser — only then is it handed back to be sealed under Board accounts, for that
/// exact host. Each system has its own recipe, mapped against the live page before it was
/// written; a host none covers is reported by name. No captcha or bot-check is ever bypassed:
/// such a wall ends the attempt, and it is the person's.
/// </summary>
public sealed partial class AccountCreator(BrowserOptions options, ILogger<AccountCreator> log) : IAccountCreator
{
    public const string Workday = "Workday";
    public const string Haystack = "Haystack";

    public string? Recipe(string host)
    {
        host = BoardAccount.Normalize(host);
        if (host.EndsWith(".myworkdayjobs.com", StringComparison.Ordinal) || host.EndsWith(".myworkdaysite.com", StringComparison.Ordinal))
            return Workday;
        if (host == "haystack.cv") return Haystack;
        return null;
    }

    /// <summary>
    /// Create an account at <paramref name="host"/> for <paramref name="email"/>. The relay reads
    /// the verification code or link the board mails (the same one a board's email sign-in uses).
    /// </summary>
    public async Task<AccountCreation> CreateAsync(string host, string postingLink, string email,
        Func<CodeRequest, CancellationToken, Task<string?>> relay, CancellationToken ct)
    {
        host = BoardAccount.Normalize(host);
        var recipe = Recipe(host);
        if (recipe is null)
            return new(null, $"no account-creation recipe for {host} yet — create the account yourself and save it under Settings · Agent · Board accounts");
        if (email.Length == 0 || !email.Contains('@'))
            return new(null, "no application email to register with — set one in Settings · Agent");

        var password = NewPassword();
        var account = new BoardAccount(host, email, password);
        // The session may go to the host being registered on and nowhere else it has not been sent.
        var start = recipe == Haystack ? "https://haystack.cv/auth?mode=signup" : postingLink;
        await using (var session = await BrowserSession.OpenAsync(options, start, ct, [account], reveal: false))
        {
            try
            {
                var registered = recipe switch
                {
                    Workday => await RegisterWorkdayAsync(session.Page, email, password),
                    _ => await RegisterHaystackAsync(session.Page, email, password),
                };
                if (registered is not null) return registered;
                if (await VerifyEmailAsync(session, host, email, relay, ct) is { } stopped) return stopped;
            }
            catch (PlaywrightException ex) { return new(null, $"creating the account at {host} failed: {ex.Message.Split('\n')[0]}"); }
            catch (TimeoutException) { return new(null, $"creating the account at {host} timed out — the page did not answer"); }
        }

        // Proved in a fresh browser, or not kept at all.
        var signIn = recipe == Workday ? WorkdaySignInUrl(postingLink) : "https://haystack.cv/auth?mode=signin";
        await using var proof = await BrowserSession.OpenAsync(options, signIn, ct, [account], reveal: false);
        try
        {
            if (recipe == Workday) await OpenWorkdaySignInAsync(proof.Page);
            if (await proof.SignInAsAsync(account))
            {
                log.LogInformation("account created and proved at {Host} for {Email}", host, email);
                return new(account, $"created a candidate account at {host} for {email}, and signed in with it");
            }
            // "Create Account" that lands on Sign In with no error is also what an address that
            // already has an account sees. The sign-in says which.
            return new(null, $"an account already exists at {host} for {email} — save its password under Settings · Agent · Board accounts, or reset it there yourself"
                + (proof.RevealNote.Length > 0 ? $" ({proof.RevealNote})" : ""), AlreadyExists: true);
        }
        catch (PlaywrightException ex) { return new(null, $"the new account at {host} could not be proved by signing in: {ex.Message.Split('\n')[0]}"); }
    }

    // Workday, mapped read-only against a live tenant (2026-09-21): the cookie notice, Apply
    // (adventureButton), Apply Manually, then Create Account — email, password, verifyPassword,
    // a terms checkbox, and createAccountSubmitButton under a click_filter overlay that is the
    // real click target. beecatcher is a honeypot and stays empty. data-automation-id is
    // stable across tenants.
    private static async Task<AccountCreation?> RegisterWorkdayAsync(IPage page, string email, string password)
    {
        static ILocator Auto(IPage p, string id) => p.Locator($"[data-automation-id='{id}']");
        await ClickIfVisibleAsync(Auto(page, "legalNoticeAcceptButton"));
        await ClickIfVisibleAsync(Auto(page, "adventureButton"));
        await ClickIfVisibleAsync(Auto(page, "applyManually"));
        // Sign In is shown first on some tenants; its "Create Account" link turns the form over.
        if (!await WaitVisibleAsync(Auto(page, "verifyPassword"), 5_000))
            await ClickIfVisibleAsync(Auto(page, "createAccountLink"));
        if (!await WaitVisibleAsync(Auto(page, "verifyPassword"), 10_000))
            return new(null, "Workday showed no Create Account form after Apply — check the posting");
        if (await HasCaptchaAsync(page))
            return new(null, "Workday's Create Account is behind a captcha — that one is yours to create");
        await Auto(page, "email").FillAsync(email);
        await Auto(page, "password").FillAsync(password);
        await Auto(page, "verifyPassword").FillAsync(password);
        var terms = Auto(page, "createAccountCheckbox");
        if (await terms.CountAsync() > 0 && !await terms.IsCheckedAsync()) await terms.CheckAsync(new() { Force = true });
        var overlay = page.Locator("[data-automation-id='createAccountSubmitButton'] ~ [data-automation-id='click_filter'], [data-automation-id='click_filter'][aria-label*='Create Account' i]").First;
        if (await overlay.CountAsync() > 0) await overlay.ClickAsync();
        else await Auto(page, "createAccountSubmitButton").ClickAsync();
        await page.WaitForTimeoutAsync(2_500);
        return await RefusalAsync(page, "Workday");
    }

    // haystack.cv, mapped read-only (2026-09-22): "Create your candidate account" — I'm a
    // candidate / I'm an employer, #email, #password, a marketing-mail opt-in checkbox (left
    // unticked: that is the person's to give), and Sign Up. Its emailed sign-in link lands a
    // candidate with no account on this page, which is why the run could not get past it.
    private static async Task<AccountCreation?> RegisterHaystackAsync(IPage page, string email, string password)
    {
        await BrowserSession.DismissConsentAsync(page);
        await ClickIfVisibleAsync(page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^I'm a candidate$", RegexOptions.IgnoreCase) }));
        if (!await WaitVisibleAsync(page.Locator("#email"), 10_000))
            return new(null, "haystack.cv showed no sign-up form");
        if (await HasCaptchaAsync(page))
            return new(null, "haystack.cv's sign-up is behind a captcha — that one is yours to create");
        await page.Locator("#email").FillAsync(email);
        await page.Locator("#password").FillAsync(password);
        await page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex("^Sign Up$", RegexOptions.IgnoreCase) }).Last.ClickAsync();
        await page.WaitForTimeoutAsync(2_500);
        return await RefusalAsync(page, "haystack.cv");
    }

    /// <summary>The board's own complaint after the sign-up click, or null when it took it.</summary>
    private static async Task<AccountCreation?> RefusalAsync(IPage page, string board)
    {
        string text;
        try { text = await page.InnerTextAsync("body"); } catch (PlaywrightException) { return null; }
        if (AlreadyRegistered().IsMatch(text))
            return new(null, $"{board} already has an account for this email — save its password under Settings · Agent · Board accounts, or reset it there yourself", AlreadyExists: true);
        var complaint = await page.EvaluateAsync<string>("""
            () => [...document.querySelectorAll('[role=alert], [data-automation-id=errorMessage], .error, [class*="error" i]')]
              .filter(e => e.offsetParent !== null).map(e => (e.innerText || '').trim()).filter(t => t && t.length < 240).join('; ').slice(0, 240)
            """);
        return complaint.Length > 0 ? new(null, $"{board} refused the sign-up: {complaint}") : null;
    }

    /// <summary>
    /// When the board says to check the inbox, read what it sent — a link on its own host, opened
    /// in this session, or a code typed into the box it shows. Null when there was nothing to
    /// verify or the verification went through; otherwise why it stopped.
    /// </summary>
    private async Task<AccountCreation?> VerifyEmailAsync(BrowserSession session, string host, string email,
        Func<CodeRequest, CancellationToken, Task<string?>> relay, CancellationToken ct)
    {
        var page = session.Page;
        string text;
        try { text = await page.InnerTextAsync("body"); } catch (PlaywrightException) { return null; }
        if (!CheckInbox().IsMatch(text)) return null;
        var sent = await relay(new CodeRequest(email, SignIn: true), ct);
        if (string.IsNullOrWhiteSpace(sent))
            return new(null, $"{host} emailed {email} to verify the new account and nothing arrived in time — open that mail yourself, then save the account under Board accounts");
        sent = sent.Trim();
        if (Uri.TryCreate(sent, UriKind.Absolute, out var link))
        {
            var onHost = Uri.TryCreate(page.Url, UriKind.Absolute, out var here) ? here.Host : host;
            if (!BrowserSession.HostAllowed(link.Host, host) && !BrowserSession.HostAllowed(link.Host, onHost))
                return new(null, $"the verification link pointed off {host}, to {link.Host} — not followed");
            await page.GotoAsync(link.AbsoluteUri, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 8_000 }); }
            catch (TimeoutException) { /* judged by the sign-in that follows */ }
            return null;
        }
        var box = page.Locator("input[autocomplete='one-time-code']:visible, input[inputmode='numeric']:visible, input[name*='code' i]:visible, input[id*='code' i]:visible").First;
        if (await box.CountAsync() == 0)
            return new(null, $"{host} sent a code to verify the new account and showed nowhere to type it");
        await box.FillAsync(sent);
        await box.PressAsync("Enter");
        await page.WaitForTimeoutAsync(2_000);
        log.LogInformation("verification code entered at {Host}", host);
        return null;
    }

    /// <summary>Workday's sign-in for the posting's tenant and site: <c>https://t.wdN.myworkdayjobs.com/&lt;site&gt;/login</c>.</summary>
    public static string WorkdaySignInUrl(string postingLink)
    {
        if (!Uri.TryCreate(postingLink, UriKind.Absolute, out var u)) return postingLink;
        // Everything before /job/ is the tenant's site (/en-US/Careers, /External); /login sits under it.
        var path = u.AbsolutePath;
        var at = path.IndexOf("/job/", StringComparison.OrdinalIgnoreCase);
        var site = (at >= 0 ? path[..at] : path).TrimEnd('/');
        return u.GetLeftPart(UriPartial.Authority) + site + "/login";
    }

    private static async Task OpenWorkdaySignInAsync(IPage page)
    {
        await ClickIfVisibleAsync(page.Locator("[data-automation-id='legalNoticeAcceptButton']"));
        if (!await BrowserSession.SignInFormVisibleAsync(page))
            await ClickIfVisibleAsync(page.Locator("[data-automation-id='signInLink'], [data-automation-id='utilityButtonSignIn']").First);
        await WaitVisibleAsync(page.Locator("input[type='password']"), 8_000);
    }

    /// <summary>24 characters from every class a password policy asks for, from the OS's CSPRNG.</summary>
    public static string NewPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ", lower = "abcdefghijkmnopqrstuvwxyz", digits = "23456789", symbols = "!@#$%^&*-_=+?";
        const string all = upper + lower + digits + symbols;
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)], lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)], symbols[RandomNumberGenerator.GetInt32(symbols.Length)],
        };
        while (chars.Count < 24) chars.Add(all[RandomNumberGenerator.GetInt32(all.Length)]);
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string([.. chars]);
    }

    private static async Task ClickIfVisibleAsync(ILocator locator)
    {
        try { if (await locator.CountAsync() > 0 && await locator.First.IsVisibleAsync()) await locator.First.ClickAsync(new() { Timeout = 5_000 }); }
        catch (PlaywrightException) { /* not on this tenant */ }
        catch (TimeoutException) { /* not on this tenant */ }
    }

    private static async Task<bool> WaitVisibleAsync(ILocator locator, int ms)
    {
        try { await locator.First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = ms }); return true; }
        catch (TimeoutException) { return false; }
        catch (PlaywrightException) { return false; }
    }

    private static async Task<bool> HasCaptchaAsync(IPage page) =>
        await page.Locator("iframe[src*='recaptcha'], iframe[src*='hcaptcha'], iframe[src*='turnstile'], .g-recaptcha, .h-captcha").CountAsync() > 0;

    [GeneratedRegex(@"already (?:has an account|have an account|registered|exists|in use)|account (?:with this email )?already exists|email (?:address )?is already", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyRegistered();

    [GeneratedRegex(@"check your (?:e-?mail|inbox)|verify your (?:e-?mail|account)|confirm your (?:e-?mail|account)|we(?:'ve| have)? sent (?:you )?(?:an? )?(?:e-?mail|link|code)|verification (?:e-?mail|link|code)", RegexOptions.IgnoreCase)]
    private static partial Regex CheckInbox();
}
