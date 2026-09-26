// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Data;

/// <summary>Bad input the store rejects — maps to HTTP 400 (heir to AppError).</summary>
public sealed class AppValidationException(string message) : Exception(message);

/// <summary>Requested application does not exist — maps to HTTP 404.</summary>
public sealed class AppNotFoundException(string message) : Exception(message);

/// <summary>Row changed since the caller's base version — maps to HTTP 409.</summary>
public sealed class AppConflictException(string message) : Exception(message);

/// <summary>The account may not use this feature until the operator allows it (403).</summary>
public sealed class AppForbiddenException(string message) : Exception(message);

/// <summary>The account is over a usage budget (e.g. the daily draft cap on the
/// operator's LLM key) — maps to HTTP 429. The message is safe to surface.</summary>
public sealed class AppRateLimitedException(string message) : Exception(message);

/// <summary>The configured LLM endpoint is unreachable or returned an unusable
/// response — maps to HTTP 502. The message is safe to surface to the user.</summary>
public sealed class LlmUnavailableException(string message) : Exception(message);

/// <summary>An /api/scrape upstream fetch failed (unreachable, non-HTML, oversized,
/// or a redirect loop) — maps to HTTP 502. The message is safe to surface.</summary>
public sealed class ScrapeUnavailableException(string message) : Exception(message);

/// <summary>An outbound notification (Telegram) could not be delivered — maps to HTTP
/// 502. The message is safe to surface and never carries the bot token.</summary>
public sealed class NotificationFailedException(string message) : Exception(message);
