// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// The phone-in-hand path for Greenhouse's security code: a run parked on the code
/// mooed the person, and they reply to the moo with what the email says. While the run
/// is parked, this reads the bot's inbox and hands back the first reply that is a code —
/// from the configured chat only, sent after the moo, 4–16 letters and digits, the same
/// shape <c>POST /api/apps/{name}/security-code</c> accepts. One parked run per tenant at a
/// time, so there is nothing to disambiguate. Anything else in the inbox is skipped and
/// confirmed away, never acted on.
/// </summary>
public sealed partial class TelegramCodeReplies
{
    [GeneratedRegex(@"^[A-Za-z0-9]{4,16}$")]
    private static partial Regex CodeShape();

    private readonly INotifier _notifier;
    private readonly TelegramTarget _target;
    private readonly DateTimeOffset _since;
    private readonly ILogger _log;
    private long? _offset;

    public TelegramCodeReplies(INotifier notifier, TelegramTarget target, DateTimeOffset since, ILogger log)
    {
        _notifier = notifier;
        _target = target;
        _since = since;
        _log = log;
    }

    /// <summary>The code a reply carries, or null. Tolerates the person pasting it with
    /// spaces around it or a "code:" prefix; nothing else is interpreted. Public for tests.</summary>
    public static string? CodeIn(string text)
    {
        var t = (text ?? "").Trim();
        var colon = t.LastIndexOf(':');
        if (colon >= 0) t = t[(colon + 1)..].Trim();
        return CodeShape().IsMatch(t) ? t : null;
    }

    /// <summary>One poll: the first acceptable code, or null when none has arrived yet.
    /// Throws <see cref="NotificationFailedException"/> when the bot cannot be read (a
    /// webhook is set, the token is bad, Telegram is down) — the caller stops polling.</summary>
    public async Task<string?> PollAsync(CancellationToken ct)
    {
        var replies = await _notifier.ReadRepliesAsync(_target.BotToken, _offset, ct);
        string? found = null;
        foreach (var r in replies)
        {
            _offset = Math.Max(_offset ?? 0, r.UpdateId + 1);
            if (found is not null) continue;
            // Never a code from any other chat, and never one that predates the moo.
            if (!string.Equals(r.ChatId, _target.ChatId, StringComparison.Ordinal)) continue;
            if (r.SentAt < _since) continue;
            found = CodeIn(r.Text);
        }
        if (found is not null)
            _log.LogInformation("security code read from a Telegram reply");
        return found;
    }
}
