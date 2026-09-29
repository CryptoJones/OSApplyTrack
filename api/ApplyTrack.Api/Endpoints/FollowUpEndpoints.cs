// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Globalization;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Notifications;
using MimeKit;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Following up from the Due view (#394). <c>POST /api/apps/{name}/followup/draft</c> has
/// the tenant's model write the follow-up from what the account already holds;
/// <c>…/followup/send</c> mails the reviewed draft from the tenant's own mailbox (SMTP),
/// logs it on the application and moves or clears its follow-up date; <c>…/followup/done</c>
/// records a follow-up made some other way (LinkedIn, a call) and does the same. Drafting is
/// off with cover letters (<c>cover_letters_enabled</c>) — no model is called — and is
/// metered like any other model request (#346). Nothing is ever sent without the person
/// pressing Send.
/// </summary>
public static class FollowUpEndpoints
{
    public sealed record SendBody(string? To, string? Subject, string? Body, string? NextFollowup);
    public sealed record DoneBody(string? Channel, string? Body, string? NextFollowup);

    private const int MaxSubject = 200;

    public static void MapFollowUpEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/apps/{name}/followup/draft", async (
            string name, ApplicationRepo apps, ResumeRepo resumes, LlmSettingsRepo llm, LlmOptions instance,
            FollowUpDrafter drafter, CoverLetterRepo letters, AppNoteRepo notes, ContactRepo contacts,
            MailboxSettingsRepo mailbox, IFollowUpMail mail, LlmUsageRepo usage, ILoggerFactory loggers,
            CancellationToken ct) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            // The tenant's switch comes first: drafting off means no model call at all.
            var (_, _, _, draftingOn) = await llm.GetViewAsync();
            if (!draftingOn)
                throw new AppValidationException("drafting is turned off — enable it in Settings · AI");

            var (contactName, to) = await ContactAsync(rec, contacts);
            var cfg = EffectiveLlmConfig.Resolve(instance, await llm.GetOverrideAsync());
            await usage.ChargeAsync(cfg);

            var thread = await ThreadAsync(to, mailbox, mail, loggers.CreateLogger("ApplyTrack.Api.FollowUp"), ct);
            var context = new FollowUpContext(rec.Fields, contactName, await letters.GetBodyAsync(rec.Name) ?? "",
                await notes.ListAsync(rec.Name) ?? [], thread,
                DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            var draft = await drafter.DraftAsync(context, await resumes.GetAsync(), cfg,
                await llm.GetCoverLetterSignatureAsync(), ct);
            var view = await mailbox.GetViewAsync();
            return Results.Ok(new
            {
                subject = draft.Subject,
                body = draft.Body,
                to,
                contact = contactName,
                // Send needs somewhere to send from, and someone to send to.
                can_send = view.CanSend,
                thread_messages = thread.Count,
            });
        }).RequireRateLimiting("draft");

        app.MapPost("/api/apps/{name}/followup/send", async (
            string name, SendBody body, ApplicationRepo apps, AppNoteRepo notes, MailboxSettingsRepo mailbox,
            IFollowUpMail mail, CancellationToken ct) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var smtp = await mailbox.GetSendTargetAsync()
                ?? throw new AppValidationException(
                    "add outgoing mail (SMTP) to your mailbox in Settings · Notifications to send from here — or copy the draft and send it yourself");
            if (!MailboxAddress.TryParse(smtp.Username, out var from) || !from.Address.Contains('@'))
                throw new AppValidationException("your mailbox username is not an email address, so there is nothing to send from");
            var to = Address(body.To);
            var subject = (body.Subject ?? "").Trim();
            if (subject.Length == 0 || subject.Length > MaxSubject || subject.Any(char.IsControl))
                throw new AppValidationException($"the subject is one line of 1–{MaxSubject} characters");
            var text = (body.Body ?? "").Trim();
            if (text.Length == 0)
                throw new AppValidationException("the email is empty");
            InputLimits.Text("body", text, FollowUpDrafter.MaxBodyChars + 2000);
            var next = NextFollowup(body.NextFollowup);

            try
            {
                await mail.SendAsync(smtp, to, subject, text, ct);
            }
            catch (Exception ex) when (ex is not (AppValidationException or OperationCanceledException))
            {
                var reason = ex.Message.Split('\n')[0].Trim();
                if (reason.Length > 200) reason = reason[..200];
                throw new NotificationFailedException($"your mail server refused the follow-up ({ex.GetType().Name}: {reason})");
            }

            var note = await notes.AddAsync(rec.Name, null, $"Follow-up emailed to {to} — “{subject}”\n\n{text}");
            await apps.SetFollowupAsync(rec.Name, next);
            return Results.Ok(new { sent = true, to, followup = next, note });
        }).RequireRateLimiting("draft");

        app.MapPost("/api/apps/{name}/followup/done", async (
            string name, DoneBody body, ApplicationRepo apps, AppNoteRepo notes) =>
        {
            var rec = await apps.GetAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'");
            var channel = (body.Channel ?? "").Trim();
            if (channel.Length > 64 || channel.Any(char.IsControl))
                throw new AppValidationException("the channel is a short name, like LinkedIn or phone");
            var next = NextFollowup(body.NextFollowup);
            var text = (body.Body ?? "").Trim();
            var head = channel.Length > 0 ? $"Followed up via {channel}" : "Followed up";
            var note = await notes.AddAsync(rec.Name, null, text.Length > 0 ? $"{head}\n\n{text}" : head);
            await apps.SetFollowupAsync(rec.Name, next);
            return Results.Ok(new { done = true, followup = next, note });
        });
    }

    /// <summary>Who the follow-up goes to: the application's own contact, else the first
    /// contact linked to it (#360) that has an email address.</summary>
    private static async Task<(string Name, string Email)> ContactAsync(AppRecord rec, ContactRepo contacts)
    {
        var name = rec.Fields.Contact.Trim();
        var email = rec.Fields.ContactEmail.Trim();
        if (email.Length > 0)
            return (name, email);
        var linked = await contacts.ForApplicationAsync(rec.Name) ?? [];
        var first = linked.FirstOrDefault(l => l.Contact.Email.Length > 0) ?? linked.FirstOrDefault();
        return first is null ? (name, "") : (name.Length > 0 ? name : first.Contact.Name, first.Contact.Email);
    }

    /// <summary>The mail with the contact, when the tenant has a mailbox to read and the
    /// contact an address. Best-effort and bounded: a slow or refusing server costs the
    /// draft its thread, never the draft.</summary>
    private static async Task<IReadOnlyList<MailSnippet>> ThreadAsync(
        string address, MailboxSettingsRepo mailbox, IFollowUpMail mail, ILogger log, CancellationToken ct)
    {
        if (address.Length == 0 || await mailbox.GetTargetForTestAsync() is not { } imap)
            return [];
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            return await mail.ThreadAsync(imap, address, cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            log.LogInformation("follow-up: mail thread unavailable: {Type}", ex.GetType().Name);
            return [];
        }
    }

    /// <summary>One plain address, or the validation error.</summary>
    public static string Address(string? raw)
    {
        var to = (raw ?? "").Trim();
        if (to.Length == 0)
            throw new AppValidationException("there is no address to send to — add the contact's email, or copy the draft and send it another way");
        InputLimits.Text("to", to, InputLimits.ContactEmail);
        if (to.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is ',' or ';' or '<' or '>')
            || !MailboxAddress.TryParse(to, out var parsed) || parsed.Address != to || to.Count(c => c == '@') != 1)
            throw new AppValidationException("that doesn't look like one email address");
        return to;
    }

    /// <summary>The next follow-up date: blank clears it, else a <c>YYYY-MM-DD</c> date.</summary>
    public static string NextFollowup(string? raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0)
            return "";
        if (!DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new AppValidationException("the next follow-up is a date like 2026-10-05, or blank to clear it");
        return s;
    }
}
