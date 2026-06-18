// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Auth;

/// <summary>
/// Instance-wide SMTP settings, bound from the <c>Email</c> config section
/// (env <c>Email__Host</c> / <c>Email__Port</c> / <c>Email__Username</c> /
/// <c>Email__Password</c> / <c>Email__From</c>). SMTP is the email equivalent of the
/// any-OpenAI-compatible LLM rule: the same code points at a local relay, the
/// operator's own mail provider, or any hosted SMTP submission service (Fastmail,
/// SendGrid, Mailgun, SES, …) — no vendor-specific code, no required paid key.
///
/// When <see cref="Host"/> is blank, no real sender is wired up and login links are
/// logged to the console instead (see <see cref="ConsoleEmailSender"/>), so a
/// self-hoster can try auth with zero email config.
/// </summary>
public sealed class EmailOptions
{
    /// <summary>SMTP host to relay through, e.g. <c>smtp.fastmail.com</c> or <c>localhost</c>. Blank disables real mail.</summary>
    public string Host { get; set; } = "";

    /// <summary>Submission port. 587 (STARTTLS) and 465 (implicit TLS) are the usual choices; 25 for an unauthenticated local relay.</summary>
    public int Port { get; set; } = 587;

    /// <summary>SASL username. Blank sends without auth (only sane for a trusted local relay on :25).</summary>
    public string Username { get; set; } = "";

    /// <summary>SASL password / app password. Blank sends without auth.</summary>
    public string Password { get; set; } = "";

    /// <summary>Envelope/From address shown to the recipient, e.g. <c>apply@example.com</c>. Defaults to the username when blank.</summary>
    public string From { get; set; } = "";

    /// <summary>Friendly display name on the From header.</summary>
    public string FromName { get; set; } = "OSApplyTrack";

    /// <summary>How long to wait on the SMTP server before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>True once there is a host to relay through.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Host);

    /// <summary>The address mail is sent from — explicit <see cref="From"/>, else the SASL username.</summary>
    public string EffectiveFrom =>
        !string.IsNullOrWhiteSpace(From) ? From.Trim() : Username.Trim();
}
