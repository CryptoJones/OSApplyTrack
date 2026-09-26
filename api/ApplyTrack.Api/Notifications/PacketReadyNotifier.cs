// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// Tells the human a packet is ready for them to click Submit — and the other moments
/// that need them. At most once per packet build: the <c>notified_at</c> claim is taken
/// before the send, so two worker containers can never both fire. Fans out to every
/// channel the tenant has on (Telegram, email — #355), each gated by the per-event
/// toggles. Never throws to the caller — a failed notification is logged and recorded in
/// <c>agent_events</c> per channel, and the packet is unaffected either way.
/// </summary>
public sealed class PacketReadyNotifier
{
    public const string SentEvent = "notify_sent";
    public const string FailedEvent = "notify_failed";
    private const int MaxDetailChars = 300;

    private readonly INotifier _notifier;
    private readonly EmailNotifier? _email;
    private readonly string _publicBaseUrl;
    private readonly ILogger<PacketReadyNotifier> _log;

    public PacketReadyNotifier(INotifier notifier, IConfiguration config, ILogger<PacketReadyNotifier> log, EmailNotifier? email = null)
    {
        _notifier = notifier;
        _email = email;
        _publicBaseUrl = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        _log = log;
    }

    /// <summary>Which moment the moo marks.</summary>
    public enum Moment
    {
        /// <summary>The packet is prepared; the human submits (copy-and-open, or no browser).</summary>
        Ready,
        /// <summary>The browser filled the form in a dry run; the human clicks Apply.</summary>
        Filled,
        /// <summary>The browser submitted and saw a confirmation. Informational, not claimed.</summary>
        Submitted,
        /// <summary>The board emailed the candidate a security code and the run is parked on
        /// it — the human has minutes to reply with it or paste it into the app. Always news,
        /// never claimed.</summary>
        Code,
        /// <summary>The board signs the candidate in by email before it shows its form (join.com,
        /// #210): a code or a link is in their mailbox and the run is parked on it. Always
        /// news, never claimed.</summary>
        SignIn,
        /// <summary>A real (not dry-run) submission did not go through — a crash, a refusal,
        /// a captcha. The detail says why (#355). Always news, never claimed.</summary>
        SubmitFailed,
    }

    /// <summary>The per-event toggle a moment answers to (Settings · Notifications).</summary>
    public enum Kind { PacketReady, SecurityCode, SubmitFailed }

    public static Kind KindOf(Moment moment) => moment switch
    {
        Moment.Code or Moment.SignIn => Kind.SecurityCode,
        Moment.SubmitFailed => Kind.SubmitFailed,
        _ => Kind.PacketReady,
    };

    public static bool Wants(NotificationEvents events, Kind kind) => kind switch
    {
        Kind.SecurityCode => events.SecurityCode,
        Kind.SubmitFailed => events.SubmitFailed,
        _ => events.PacketReady,
    };

    public async Task NotifyAsync(
        NotificationSettingsRepo settings, AgentPacketRepo packets, AgentEventRepo events,
        string applicationName, string company, string role, CancellationToken ct,
        Moment moment = Moment.Ready, string detail = "")
    {
        var channels = await settings.GetChannelsAsync();
        if ((channels.Telegram is null && channels.Email is null) || !Wants(channels.Events, KindOf(moment)))
            return;
        // Ready and Filled share the one claim per packet build; the rest are always news.
        if (moment is (Moment.Ready or Moment.Filled) && !await packets.TryClaimNotificationAsync(applicationName))
            return;

        var link = DeepLink(_publicBaseUrl, applicationName);
        var tag = moment.ToString().ToLowerInvariant();
        foreach (var (channel, send) in Sends(channels,
            BuildMessage(company, role, link, moment, detail),
            BuildSubject(company, role, moment, detail),
            EmailBody(BuildMessage(company, role, link, moment, detail, canReply: false)), ct))
        {
            try
            {
                await send();
                await events.RecordAsync(SentEvent, applicationName, new { channel, moment = tag });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning("{Name}: {Channel} notification failed: {Reason}", applicationName, channel, ex.Message);
                await events.RecordAsync(FailedEvent, applicationName, new { channel, moment = tag, reason = ex.Message });
            }
        }
    }

    /// <summary>A plain moo to the tenant, not about any one application: true when at
    /// least one channel took it, false when none is on, the event is toggled off, or
    /// every send failed.</summary>
    public async Task<bool> SendAsync(NotificationSettingsRepo settings, string text, CancellationToken ct,
        Kind kind = Kind.SecurityCode, string subject = "OSApplyTrack needs you")
    {
        var channels = await settings.GetChannelsAsync();
        if (!Wants(channels.Events, kind)) return false;
        var sent = false;
        foreach (var (channel, send) in Sends(channels, text, subject, EmailBody(text), ct))
        {
            try { await send(); sent = true; }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                _log.LogWarning("{Channel} notification failed: {Reason}", channel, ex.Message);
            }
        }
        return sent;
    }

    /// <summary>One send per channel that is on. Email with no SMTP on this container is
    /// still attempted, so the failure (and its reason) lands in the agent's events rather
    /// than the moo vanishing — an agent container missing <c>Email__*</c> is then visible.</summary>
    private List<(string Channel, Func<Task> Send)> Sends(
        NotificationChannels channels, string text, string subject, string emailText, CancellationToken ct)
    {
        var sends = new List<(string, Func<Task>)>();
        if (channels.Telegram is { } tg)
            sends.Add(("telegram", () => _notifier.SendAsync(tg.BotToken, tg.ChatId, text, ct)));
        if (channels.Email is { } to)
            sends.Add(("email", () => _email is null
                ? throw new NotificationFailedException("no SMTP server is configured on this instance (Email__Host)")
                : _email.SendAsync(to, subject, emailText, null, ct)));
        return sends;
    }

    /// <summary>The mail's body: the moo, then why it came.</summary>
    public static string EmailBody(string text) =>
        text + "\n\n—\nYou get this because email notifications are on under Settings · Notifications in OSApplyTrack.";

    /// <summary>The mail's subject line: what happened, then to which application. Pure, for tests.</summary>
    public static string BuildSubject(string company, string role, Moment moment = Moment.Ready, string detail = "")
    {
        var who = string.Join(" · ", new[] { company.Trim(), role.Trim() }.Where(s => s.Length > 0));
        var subject = who.Length > 0 ? who : "an application";
        var what = moment switch
        {
            Moment.Filled when detail.Trim().Length > 0 => "Filled in, needs you",
            Moment.Filled => "Filled in, ready to click Apply",
            Moment.Submitted => "Submitted",
            Moment.Code => "Security code needed",
            Moment.SignIn => "Sign-in code needed",
            Moment.SubmitFailed => "Submission failed",
            _ => "Ready to submit",
        };
        return $"{what}: {subject}";
    }

    /// <summary>The 🐮. Pure, for tests. <paramref name="canReply"/> is false for a channel
    /// nobody reads a reply from (email): there the code goes into the app, not "here".</summary>
    public static string BuildMessage(string company, string role, string? link, Moment moment = Moment.Ready, string detail = "",
        bool canReply = true)
    {
        var who = string.Join(" · ", new[] { company.Trim(), role.Trim() }.Where(s => s.Length > 0));
        var subject = who.Length > 0 ? who : "an application";
        // For Filled, the detail is what still needs the person: the required questions the
        // agent could not answer or map, comma-separated. A dry run that stopped on two of
        // them used to moo the same "ready for you to click Apply" as a clean one, so the
        // person opened the packet not knowing whether it was a click or a form to finish (#190).
        // Semicolon-separated: a question's label may itself carry a comma.
        var needs = moment == Moment.Filled && detail.Length > 0
            ? detail.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        var to = detail.Length > 0 ? detail : "you";
        var why = detail.Trim();
        if (why.Length > MaxDetailChars) why = why[..MaxDetailChars] + "…";
        var text = moment switch
        {
            Moment.Filled when needs.Length > 0 =>
                $"🐮 moo — {subject} is filled in — {needs.Length} question{(needs.Length == 1 ? "" : "s")} need{(needs.Length == 1 ? "s" : "")} you: {string.Join(", ", needs)}",
            Moment.Filled => $"🐮 moo — {subject} is filled in and ready for you to click Apply",
            Moment.Submitted => $"✅ {subject} was submitted",
            Moment.Code when canReply => $"🔐 {subject}: the board emailed a security code to {to} — reply here with the code (or paste it in the app) within a few minutes to finish",
            Moment.Code => $"🔐 {subject}: the board emailed a security code to {to} — paste it in the app within a few minutes to finish",
            Moment.SignIn when canReply => $"🔐 {subject}: the board signs you in by email first — it sent a code or a link to {to}; reply here with the code, or paste the link (or do either in the app), within a few minutes and the agent finishes the application",
            Moment.SignIn => $"🔐 {subject}: the board signs you in by email first — it sent a code or a link to {to}; paste the code or the link in the app within a few minutes and the agent finishes the application",
            Moment.SubmitFailed => $"⚠️ {subject}: the agent's submission did not go through{(why.Length > 0 ? " — " + why : "")}",
            _ => $"🐮 moo — {subject} is ready to submit",
        };
        return link is null ? text : text + "\n" + link;
    }

    /// <summary>A link that opens the application on load (<c>/#app=name</c>), or null
    /// when the operator hasn't set App__PublicBaseUrl — the hash keeps the slug out
    /// of server logs.</summary>
    public static string? DeepLink(string publicBaseUrl, string applicationName) =>
        publicBaseUrl.Length == 0 ? null : $"{publicBaseUrl}/#app={Uri.EscapeDataString(applicationName)}";
}
