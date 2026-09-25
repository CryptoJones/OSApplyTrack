// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using MimeKit;

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

    /// <summary>
    /// Fail-fast check for a configured SMTP sender. When <see cref="Host"/> is set but
    /// <see cref="EffectiveFrom"/> is blank or not a real mailbox address,
    /// <c>SmtpEmailSender</c> throws the instant MailKit builds the From header
    /// (<c>new MailboxAddress(FromName, "")</c>), turning every magic-link request into an
    /// opaque 500 — the live-instance outage in #228. Called at boot so a bad
    /// <c>Email__</c> config fails loudly there, not silently on every sign-in. No-op when
    /// no host is set (the console sender stands in and needs no From address).
    /// </summary>
    public void Validate()
    {
        if (!IsConfigured)
            return;
        var from = EffectiveFrom;
        if (string.IsNullOrWhiteSpace(from))
            throw new InvalidOperationException(
                "Email:Host is set but neither Email:From nor Email:Username supplies a From "
                + "address. Set Email:From (e.g. apply@example.com), or clear Email:Host to log "
                + "sign-in links to the console instead.");
        if (!MailboxAddress.TryParse(from, out _))
            throw new InvalidOperationException(
                $"Email:From/Email:Username '{from}' is not a valid email address. Set a real "
                + "From address, or clear Email:Host to log sign-in links to the console instead.");
    }

    /// <summary>
    /// Boot-time guard for #337: with a real SMTP sender, magic links reach real inboxes,
    /// so they must be built from a pinned public origin — never the request Host. Throws
    /// when <see cref="Host"/> is set and <paramref name="publicBaseUrl"/> is not an
    /// absolute http(s) URL. No-op for the console sender (loopback fallback covers dev).
    /// </summary>
    public void RequirePublicBaseUrl(string? publicBaseUrl)
    {
        if (!IsConfigured)
            return;
        var value = (publicBaseUrl ?? "").Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new InvalidOperationException(
                "Email:Host is set but App:PublicBaseUrl is "
                + (value.Length == 0 ? "unset" : $"'{value}', not an absolute http(s) URL")
                + ". Sign-in links are built from it, never the request Host header (which an "
                + "attacker controls). Set App__PublicBaseUrl to this instance's public origin, "
                + "e.g. https://apply.example.com.");
    }
}
