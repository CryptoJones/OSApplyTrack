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
/// so a self-hoster can try auth with zero email config. Production swaps in a real
/// sender behind <see cref="IEmailSender"/>.
///
/// The link embeds a live, login-equivalent token, so it is logged at <c>Debug</c>,
/// never <c>Information</c> — a default-Info deploy that forgot to configure a real
/// sender must not spill working login tokens into its logs. An Info breadcrumb
/// (no token) confirms the flow ran; dev reads the link by enabling Debug logging
/// (appsettings.Development.json already sets the <c>ApplyTrack.Api.Auth</c> category
/// to Debug). Tests use a capturing sender, so they don't depend on this output.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> log) : IEmailSender
{
    public Task SendMagicLinkAsync(string email, string link)
    {
        log.LogInformation(
            "magic-link issued for {Email} (enable Debug logging or configure a real "
            + "IEmailSender to reveal/deliver the link)", email);
        log.LogDebug("magic-link for {Email}: {Link}", email, link);
        return Task.CompletedTask;
    }
}
