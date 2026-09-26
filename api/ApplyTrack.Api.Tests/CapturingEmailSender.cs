// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Test double for <see cref="IEmailSender"/> that records the magic links instead
/// of sending them, so a test can read the token out of the link and drive the real
/// request -> verify flow over HTTP; notification mail lands in <see cref="Messages"/>.
/// </summary>
internal sealed class CapturingEmailSender(Exception? fail = null) : IEmailSender
{
    private readonly List<OutgoingEmail> _messages = [];

    /// <summary>Every non-magic-link mail, in order.</summary>
    public IReadOnlyList<OutgoingEmail> Messages { get { lock (_messages) return _messages.ToList(); } }

    public Task SendAsync(OutgoingEmail message, CancellationToken ct = default)
    {
        if (fail is not null) throw fail;
        lock (_messages) _messages.Add(message);
        return Task.CompletedTask;
    }

    private readonly List<(string Email, string Link)> _sent = [];

    public IReadOnlyList<(string Email, string Link)> Sent => _sent;

    public Task SendMagicLinkAsync(string email, string link)
    {
        lock (_sent) _sent.Add((email, link));
        return Task.CompletedTask;
    }

    /// <summary>The most recent link sent to <paramref name="email"/>, or null if none.</summary>
    public string? LinkFor(string email)
    {
        lock (_sent)
            return _sent.LastOrDefault(s => s.Email == email).Link;
    }
}
