// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// Tells the human a packet is ready for them to click Submit. At most once per
/// packet build: the <c>notified_at</c> claim is taken before the send, so two
/// worker containers can never both fire. Never throws to the caller — a failed
/// notification is logged and recorded in <c>agent_events</c>, and the packet is
/// unaffected either way.
/// </summary>
public sealed class PacketReadyNotifier
{
    public const string SentEvent = "notify_sent";
    public const string FailedEvent = "notify_failed";

    private readonly INotifier _notifier;
    private readonly string _publicBaseUrl;
    private readonly ILogger<PacketReadyNotifier> _log;

    public PacketReadyNotifier(INotifier notifier, IConfiguration config, ILogger<PacketReadyNotifier> log)
    {
        _notifier = notifier;
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
    }

    public async Task NotifyAsync(
        NotificationSettingsRepo settings, AgentPacketRepo packets, AgentEventRepo events,
        string applicationName, string company, string role, CancellationToken ct,
        Moment moment = Moment.Ready, string detail = "")
    {
        var target = await settings.GetTargetAsync();
        if (target is null)
            return;
        // Ready and Filled share the one claim per packet build; Submitted, Code and SignIn are always news.
        if (moment is not (Moment.Submitted or Moment.Code or Moment.SignIn) && !await packets.TryClaimNotificationAsync(applicationName))
            return;

        var text = BuildMessage(company, role, DeepLink(_publicBaseUrl, applicationName), moment, detail);
        try
        {
            await _notifier.SendAsync(target.BotToken, target.ChatId, text, ct);
            await events.RecordAsync(SentEvent, applicationName, new { channel = "telegram", moment = moment.ToString().ToLowerInvariant() });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("{Name}: notification failed: {Reason}", applicationName, ex.Message);
            await events.RecordAsync(FailedEvent, applicationName, new { channel = "telegram", reason = ex.Message });
        }
    }

    /// <summary>A plain moo to the tenant, not about any one application: true when it was
    /// sent, false when the tenant has no Telegram target or the send failed.</summary>
    public async Task<bool> SendAsync(NotificationSettingsRepo settings, string text, CancellationToken ct)
    {
        var target = await settings.GetTargetAsync();
        if (target is null) return false;
        try { await _notifier.SendAsync(target.BotToken, target.ChatId, text, ct); return true; }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.LogWarning("notification failed: {Reason}", ex.Message);
            return false;
        }
    }

    /// <summary>The 🐮. Pure, for tests.</summary>
    public static string BuildMessage(string company, string role, string? link, Moment moment = Moment.Ready, string detail = "")
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
        var text = moment switch
        {
            Moment.Filled when needs.Length > 0 =>
                $"🐮 moo — {subject} is filled in — {needs.Length} question{(needs.Length == 1 ? "" : "s")} need{(needs.Length == 1 ? "s" : "")} you: {string.Join(", ", needs)}",
            Moment.Filled => $"🐮 moo — {subject} is filled in and ready for you to click Apply",
            Moment.Submitted => $"✅ {subject} was submitted",
            Moment.Code => $"🔐 {subject}: the board emailed a security code to {(detail.Length > 0 ? detail : "you")} — reply here with the code (or paste it in the app) within a few minutes to finish",
            Moment.SignIn => $"🔐 {subject}: the board signs you in by email first — it sent a code or a link to {(detail.Length > 0 ? detail : "you")}; reply here with the code, or paste the link (or do either in the app), within a few minutes and the agent finishes the application",
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
