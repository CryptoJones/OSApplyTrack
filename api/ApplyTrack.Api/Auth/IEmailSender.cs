// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Auth;

/// <summary>Sends the magic-link email. A real SMTP/HTTP sender swaps in behind this.</summary>
public interface IEmailSender
{
    Task SendMagicLinkAsync(string email, string link);
}

/// <summary>
/// Default sender: writes the magic link to the server console instead of mailing it,
/// so a self-hoster can try auth with zero email config (and dev/tests read the link
/// off stdout). Production swaps in a real sender behind <see cref="IEmailSender"/>.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> log) : IEmailSender
{
    public Task SendMagicLinkAsync(string email, string link)
    {
        log.LogInformation("magic-link for {Email}: {Link}", email, link);
        return Task.CompletedTask;
    }
}
