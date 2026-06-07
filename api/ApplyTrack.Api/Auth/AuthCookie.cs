// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Auth;

/// <summary>
/// The session cookie and its hardening. <c>HttpOnly</c> keeps it out of JS;
/// <c>SameSite=Lax</c> plus JSON-only fetch mutations is the v1 CSRF defense (no
/// separate token). <c>Secure</c> tracks the request scheme so plain-HTTP local/dev
/// still works while real HTTPS deploys get the flag (behind a TLS proxy,
/// <c>Program.cs</c> honors <c>X-Forwarded-Proto</c> so <c>IsHttps</c> is true).
/// </summary>
public static class AuthCookie
{
    public const string Name = "applytrack_session";

    public static CookieOptions Options(DateTimeOffset expires, bool secure) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = expires,
        IsEssential = true,
    };

    public static CookieOptions DeleteOptions(bool secure) => new()
    {
        HttpOnly = true,
        Secure = secure,
        SameSite = SameSiteMode.Lax,
        Path = "/",
    };
}
