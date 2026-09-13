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
        /// it — the human has minutes to paste it into the app. Always news, never claimed.</summary>
        Code,
    }

    public async Task NotifyAsync(
        NotificationSettingsRepo settings, AgentPacketRepo packets, AgentEventRepo events,
        string applicationName, string company, string role, CancellationToken ct,
        Moment moment = Moment.Ready, string detail = "")
    {
        var target = await settings.GetTargetAsync();
        if (target is null)
            return;
        // Ready and Filled share the one claim per packet build; Submitted and Code are always news.
        if (moment is not (Moment.Submitted or Moment.Code) && !await packets.TryClaimNotificationAsync(applicationName))
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

    /// <summary>The 🐮. Pure, for tests.</summary>
    public static string BuildMessage(string company, string role, string? link, Moment moment = Moment.Ready, string detail = "")
    {
        var who = string.Join(" · ", new[] { company.Trim(), role.Trim() }.Where(s => s.Length > 0));
        var subject = who.Length > 0 ? who : "an application";
        var text = moment switch
        {
            Moment.Filled => $"🐮 moo — {subject} is filled in and ready for you to click Apply",
            Moment.Submitted => $"✅ {subject} was submitted",
            Moment.Code => $"🔐 {subject}: the board emailed a security code to {(detail.Length > 0 ? detail : "you")} — paste it in the app within a few minutes to finish",
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
