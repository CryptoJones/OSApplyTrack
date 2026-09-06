// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using Microsoft.Playwright;

namespace ApplyTrack.Api.Agent.Browser;

/// <summary>What one browser run produced.</summary>
/// <param name="Filled">The form was reached and the mapped answers were typed in.</param>
/// <param name="Submitted">Submit was clicked AND a confirmation was recognised.</param>
/// <param name="Unmapped">Required questions with an answer that no field could be found for — any of these refuses the click.</param>
public sealed record SubmitOutcome(
    bool Filled, bool Submitted, string Url, string Confirmation, byte[]? Screenshot,
    List<string> Unmapped, List<string> Mapped, string Error);

/// <summary>
/// Drives the browser container at a posting: fill every mapped answer, attach the
/// résumé from memory, screenshot — and click Submit only when asked (<c>dry_run</c>
/// false) and only when every required question with an answer found its field.
/// Fields are found by accessible name first, then by the ATS field name — the
/// generic adapter — so Greenhouse, Lever, Ashby and (with the opt-in) the long tail
/// all go through the same code. Containment lives in <see cref="BrowserSession"/>.
/// </summary>
public sealed partial class BrowserSubmitter
{
    [GeneratedRegex(@"thank you|thanks for applying|application (?:has been |was )?(?:submitted|received|sent)|we(?:'ve| have) received your application|successfully (?:submitted|applied)|your application is in", RegexOptions.IgnoreCase)]
    private static partial Regex Confirmation();

    private readonly BrowserOptions _options;
    private readonly ILogger<BrowserSubmitter> _log;

    public BrowserSubmitter(BrowserOptions options, ILogger<BrowserSubmitter> log)
    {
        _options = options;
        _log = log;
    }

    public async Task<SubmitOutcome> RunAsync(
        string link, AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default)
    {
        if (!_options.IsConfigured)
            throw new AppValidationException("browser submission isn't configured on this instance");

        await using var session = await BrowserSession.OpenAsync(_options, AtsProvider.ApplyUrl(link, packet.Provider), ct);
        var page = session.Page;
        var mapped = new List<string>();
        var unmapped = new List<string>();
        byte[]? screenshot = null;
        try
        {
            foreach (var q in packet.Questions)
            {
                ct.ThrowIfCancellationRequested();
                if (q.Kind == PacketQuestion.Eeo)
                    continue;
                if (q.Type == PacketQuestion.File)
                {
                    if (q.Id.Contains("resume", StringComparison.OrdinalIgnoreCase) && resumePdf is { } pdf)
                    {
                        if (await AttachResumeAsync(page, q, pdf)) mapped.Add(q.Id);
                        else if (q.Required) unmapped.Add(q.Id);
                    }
                    else if (q.Required)
                        unmapped.Add(q.Id);
                    continue;
                }
                if (!packet.Answers.TryGetValue(q.Id, out var answer) || string.IsNullOrWhiteSpace(answer))
                    continue;
                if (await FillAsync(page, q, answer)) mapped.Add(q.Id);
                else if (q.Required) unmapped.Add(q.Id);
            }

            screenshot = await session.ScreenshotAsync();
            if (dryRun)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "");
            if (unmapped.Count > 0)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "refused to submit: required fields could not be mapped (" + string.Join(", ", unmapped) + ")");

            var submit = await FindSubmitAsync(page);
            if (submit is null)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped, "no Submit button found");
            await submit.ClickAsync();
            try { await page.WaitForLoadStateAsync(LoadState.NetworkIdle, new() { Timeout = 15_000 }); }
            catch (TimeoutException) { /* single-page confirmations never go idle; read what's there */ }
            await page.WaitForTimeoutAsync(1500);

            var text = await page.InnerTextAsync("body");
            var m = Confirmation().Match(text);
            screenshot = await session.ScreenshotAsync();
            if (!m.Success)
                return new SubmitOutcome(true, false, page.Url, "", screenshot, unmapped, mapped,
                    "Submit was clicked but no confirmation text was recognised — check the screenshot");
            var start = Math.Max(0, m.Index - 80);
            var snippet = text.Substring(start, Math.Min(text.Length - start, 240)).Trim();
            return new SubmitOutcome(true, true, page.Url, snippet, screenshot, unmapped, mapped, "");
        }
        catch (PlaywrightException ex)
        {
            _log.LogWarning("browser run failed at {Url}: {Reason}", page.Url, ex.Message);
            screenshot ??= await session.ScreenshotAsync();
            var refused = session.Refused;
            var why = refused.Count > 0 ? $"navigation to {refused[0]} was refused" : ex.Message.Split('\n')[0];
            return new SubmitOutcome(mapped.Count > 0, false, page.Url, "", screenshot, unmapped, mapped, why);
        }
    }

    /// <summary>Find the control for a question — by accessible name first, then by the
    /// ATS field name — and set it. False when nothing visible matched.</summary>
    private static async Task<bool> FillAsync(IPage page, PacketQuestion q, string answer)
    {
        var control = await LocateAsync(page, q);
        if (control is null) return false;
        var tag = (await control.EvaluateAsync<string>("el => el.tagName")).ToLowerInvariant();
        if (tag == "select")
        {
            try { await control.SelectOptionAsync(new SelectOptionValue { Label = answer }); return true; }
            catch (PlaywrightException) { return false; }
        }
        if (q.Type is PacketQuestion.Select or PacketQuestion.MultiSelect)
        {
            // A custom combobox: type the option and confirm it.
            await control.ClickAsync();
            await control.FillAsync(answer);
            await page.Keyboard.PressAsync("Enter");
            return true;
        }
        await control.FillAsync(answer);
        return true;
    }

    private static async Task<ILocator?> LocateAsync(IPage page, PacketQuestion q)
    {
        var id = q.Id.StartsWith("std:", StringComparison.Ordinal) ? q.Id[4..] : q.Id;
        var candidates = new List<ILocator>();
        if (q.Label.Length > 0)
            candidates.Add(page.GetByLabel(q.Label, new() { Exact = false }).First);
        candidates.Add(page.Locator($"#{CssEscape(id)}").First);
        candidates.Add(page.Locator($"[name='{id}']").First);
        candidates.Add(page.Locator($"[name$='[{id}]']").First);
        candidates.Add(page.Locator($"[name*='{id}']").First);
        foreach (var c in candidates)
        {
            try
            {
                if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEditableAsync())
                    return c;
            }
            catch (PlaywrightException) { /* try the next */ }
        }
        return null;
    }

    private static async Task<bool> AttachResumeAsync(IPage page, PacketQuestion q, (byte[] Bytes, string Name) pdf)
    {
        var id = q.Id.StartsWith("std:", StringComparison.Ordinal) ? q.Id[4..] : q.Id;
        var candidates = new[]
        {
            page.Locator($"input[type=file]#{CssEscape(id)}").First,
            page.Locator($"input[type=file][name*='{id}']").First,
            page.Locator("input[type=file][name*='resume'], input[type=file][id*='resume']").First,
            page.Locator("input[type=file]").First,
        };
        foreach (var c in candidates)
        {
            try
            {
                if (await c.CountAsync() == 0) continue;
                // Never touches disk: the bytes go straight into the input.
                await c.SetInputFilesAsync(new FilePayload { Name = pdf.Name, MimeType = "application/pdf", Buffer = pdf.Bytes });
                return true;
            }
            catch (PlaywrightException) { /* try the next */ }
        }
        return false;
    }

    private static async Task<ILocator?> FindSubmitAsync(IPage page)
    {
        var candidates = new[]
        {
            page.Locator("#submit_app").First,
            page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(@"submit (?:application|your application)|^submit$|^apply$", RegexOptions.IgnoreCase) }).First,
            page.Locator("button[type=submit], input[type=submit]").First,
        };
        foreach (var c in candidates)
        {
            try { if (await c.CountAsync() > 0 && await c.IsVisibleAsync() && await c.IsEnabledAsync()) return c; }
            catch (PlaywrightException) { /* next */ }
        }
        return null;
    }

    private static string CssEscape(string id) => Regex.Replace(id, @"([^a-zA-Z0-9_-])", "\\$1");
}
