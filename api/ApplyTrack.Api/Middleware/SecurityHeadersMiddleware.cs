// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// Defense-in-depth response headers on every response (SPA + API). The CSP keeps
/// scripts to same-origin only, so even if markup slipped past the SPA's DOMPurify
/// pass it could not load or run injected JS; <c>frame-ancestors</c>/<c>X-Frame-Options</c>
/// block clickjacking; <c>nosniff</c> stops MIME confusion. HSTS is emitted only once
/// the request is actually HTTPS (behind the TLS proxy, via <c>X-Forwarded-Proto</c>),
/// so plain-HTTP local dev is unaffected.
/// </summary>
public sealed class SecurityHeadersMiddleware
{
    // Same-origin scripts only (theme-init/marked/purify/app.js are all local),
    // styles allow inline for the vendored Tailwind utility layer, images allow
    // https/data for markdown content, everything else locks to 'self'.
    private const string Csp =
        "default-src 'self'; "
        + "script-src 'self'; "
        + "style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data: https:; "
        + "font-src 'self'; "
        + "connect-src 'self'; "
        + "object-src 'none'; "
        + "base-uri 'self'; "
        + "form-action 'self'; "
        + "frame-ancestors 'none'";

    private readonly RequestDelegate _next;

    public SecurityHeadersMiddleware(RequestDelegate next) => _next = next;

    public Task InvokeAsync(HttpContext context)
    {
        var https = context.Request.IsHttps;
        // OnStarting so the headers survive a downstream Response.Clear() — the API's
        // exception middleware rebuilds error responses that way.
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["Content-Security-Policy"] = Csp;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            if (https)
                h["Strict-Transport-Security"] = "max-age=31536000; includeSubDomains";
            return Task.CompletedTask;
        });
        return _next(context);
    }
}
