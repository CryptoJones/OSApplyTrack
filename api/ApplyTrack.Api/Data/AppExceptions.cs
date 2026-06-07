// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Data;

/// <summary>Bad input the store rejects — maps to HTTP 400 (heir to AppError).</summary>
public sealed class AppValidationException(string message) : Exception(message);

/// <summary>Requested application does not exist — maps to HTTP 404.</summary>
public sealed class AppNotFoundException(string message) : Exception(message);

/// <summary>Row changed since the caller's base version — maps to HTTP 409.</summary>
public sealed class AppConflictException(string message) : Exception(message);
