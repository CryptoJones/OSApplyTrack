// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.ComponentModel.DataAnnotations;
using System.Net;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Magic-link auth: request a link, verify it into an opaque server-side session,
/// log out, and report the current user. Server-side sessions (not JWT) give instant
/// revocation. These routes are the only <c>/api</c> paths the choke-point lets
/// through unauthenticated.
/// </summary>
public static class AuthEndpoints
{
    public sealed record LinkRequest(string Email);

    private static readonly TimeSpan TokenTtl = TimeSpan.FromMinutes(15);

    // Our tokens and nonces are 43 chars; anything far longer is junk, not a link we sent.
    private const int MaxTokenLength = 128;

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Always 200, whether or not the address is known/valid — no account
        // enumeration. Upsert the user, store only the sha256 of a fresh token, email
        // the link. Per-IP rate-limited ("auth" policy) so the always-200 surface
        // can't be abused for email spam or enumeration probing.
        app.MapPost("/api/auth/request", async (
            LinkRequest body, HttpContext ctx, IConfiguration config,
            UserRepo users, MagicTokenRepo tokens, IEmailSender email, ILoggerFactory log) =>
        {
            // Bind the link to this browser (#341): a pre-auth nonce cookie, its hash stored
            // beside the token. Reuse a live one so an earlier link from this browser still
            // works; set it on every request, valid address or not, so the response carries
            // no signal about the address.
            var nonce = ctx.Request.Cookies.TryGetValue(AuthCookie.LoginName, out var existing)
                && existing is { Length: >= 32 and <= MaxTokenLength } ? existing : Tokens.NewOpaque();
            ctx.Response.Cookies.Append(AuthCookie.LoginName, nonce,
                AuthCookie.LoginOptions(DateTimeOffset.UtcNow + TokenTtl, ctx.Request.IsHttps));

            // Validate shape + cap length (RFC 5321 max 254) before any row is born:
            // a junk or unbounded address must never reach EnsureAsync. Still always
            // 200 below — a rejected address is silently dropped, no enumeration signal.
            var address = (body.Email ?? "").Trim();
            if (address.Length is > 0 and <= 254 && new EmailAddressAttribute().IsValid(address))
            {
                // Build the link from the operator's configured origin, NOT the request's
                // Host header. The Host is attacker-suppliable (and AllowedHosts may be "*"),
                // so deriving the link from it lets an attacker request a victim's link
                // pointed at attacker.example — host-header poisoning -> token capture ->
                // account takeover (#337). App:PublicBaseUrl pins it; unset, we fall back to
                // the request origin only when its Host is loopback (zero-config local dev —
                // a link to the victim's own localhost is worthless to an attacker), and
                // otherwise send nothing. Still 200: no signal either way.
                var origin = LinkOrigin(config["App:PublicBaseUrl"], ctx.Request);
                if (origin is null)
                {
                    log.CreateLogger("ApplyTrack.Auth").LogWarning(
                        "Magic link not sent: App:PublicBaseUrl is unset and the request Host '{Host}' "
                        + "is not loopback. Set App__PublicBaseUrl to this instance's public origin.",
                        ctx.Request.Host.Value);
                }
                else
                {
                    var userId = await users.EnsureAsync(address);
                    // A disabled account (users.status, #346) gets no link — silently.
                    if (await users.GetAsync(userId) is not { Status: UserRepo.Active })
                        return Results.Ok(new { ok = true });
                    var token = Tokens.NewOpaque();
                    await tokens.CreateAsync(userId, Tokens.Sha256(token), Tokens.Sha256(nonce), DateTimeOffset.UtcNow + TokenTtl);
                    await email.SendMagicLinkAsync(address, $"{origin}/api/auth/verify?token={token}");
                }
            }

            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("auth");

        // The emailed link lands here, and a GET changes nothing: it renders a one-button
        // page that POSTs the token back. Mail link-scanners (SafeLinks, Proofpoint, …) that
        // pre-fetch links no longer burn the single-use token before the person clicks (#341).
        app.MapGet("/api/auth/verify", ([FromQuery] string? token, HttpContext ctx) =>
        {
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
                return Results.Redirect("/?error=invalid_link");
            ctx.Response.Headers.CacheControl = "no-store";
            return Results.Content(ConfirmPage(token), "text/html; charset=utf-8");
        });

        // Spend the token — only in the browser that requested it (its pre-auth cookie must
        // match the hash stored with the token), so an attacker's own link can't sign a
        // victim into the attacker's account (login CSRF, #341). Then rotate: drop any
        // session this browser already had, mint a fresh one, and redirect to / so the token
        // leaves the URL/history.
        app.MapPost("/api/auth/verify", async (
            HttpContext ctx, MagicTokenRepo tokens, SessionRepo sessions, UserRepo users) =>
        {
            string? token = null;
            if (ctx.Request.HasFormContentType)
                token = (await ctx.Request.ReadFormAsync())["token"].ToString();
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength)
                return Results.Redirect("/?error=invalid_link");

            if (!ctx.Request.Cookies.TryGetValue(AuthCookie.LoginName, out var nonce) || string.IsNullOrEmpty(nonce))
                return Results.Redirect("/?error=wrong_browser");

            var userId = await tokens.ConsumeAsync(Tokens.Sha256(token), Tokens.Sha256(nonce));
            if (userId is null)
                return Results.Redirect("/?error=invalid_link");
            if (await users.GetAsync(userId.Value) is not { Status: UserRepo.Active })
                return Results.Redirect("/?error=account_disabled");

            if (ctx.Request.Cookies.TryGetValue(AuthCookie.Name, out var oldSid) && !string.IsNullOrEmpty(oldSid))
                await sessions.DeleteAsync(oldSid);

            var sid = Tokens.NewOpaque();
            var expires = await sessions.CreateAsync(sid, userId.Value, ctx.Request.Headers.UserAgent.ToString());
            ctx.Response.Cookies.Append(AuthCookie.Name, sid, AuthCookie.Options(expires, ctx.Request.IsHttps));
            return Results.Redirect("/");
        });

        // Drop the session row (instant revocation) and clear the cookie. Other browsers
        // stay signed in; DELETE /api/account/sessions signs those out.
        app.MapPost("/api/auth/logout", async (HttpContext ctx, SessionRepo sessions) =>
        {
            if (ctx.Request.Cookies.TryGetValue(AuthCookie.Name, out var sid) && !string.IsNullOrEmpty(sid))
                await sessions.DeleteAsync(sid);
            ctx.Response.Cookies.Delete(AuthCookie.Name, AuthCookie.DeleteOptions(ctx.Request.IsHttps));
            return Results.Ok(new { ok = true });
        });

        // Who am I? 401 {"detail"} when there is no valid session (the SPA's login gate).
        app.MapGet("/api/auth/me", async (TenantContext tenant, UserRepo users) =>
        {
            if (!tenant.IsAuthenticated)
                return Unauthorized();
            var user = await users.GetAsync(tenant.TenantId);
            return user is null ? Unauthorized() : Results.Ok(new { email = user.Email });
        });
    }

    /// <summary>
    /// The origin for links we email out: the operator-configured App:PublicBaseUrl
    /// (e.g. https://apply.example.com) when set; else the request's own scheme+host,
    /// but only when that Host is loopback; else null — send nothing (#337).
    /// </summary>
    public static string? LinkOrigin(string? configuredBaseUrl, HttpRequest request)
    {
        var configured = (configuredBaseUrl ?? "").Trim().TrimEnd('/');
        if (configured.Length > 0)
            return configured;
        return IsLoopbackHost(request.Host.Host) ? $"{request.Scheme}://{request.Host}" : null;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// The no-JS confirm page behind the emailed link: one form that POSTs the token to
    /// <c>/api/auth/verify</c>. The token is HTML-encoded — it came straight off the URL.
    /// </summary>
    public static string ConfirmPage(string token) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex">
        <title>Sign in to ApplyTrack</title>
        <style>
          body { font: 16px/1.5 system-ui, sans-serif; margin: 0; padding: 16px; background: #f7f7f8; color: #1d1d1f; }
          main { max-width: 26rem; margin: 15vh auto 0; padding: 1.5rem; background: #fff; border-radius: 12px; box-shadow: 0 1px 4px rgb(0 0 0 / .12); }
          h1 { font-size: 1.4rem; margin: 0 0 .5rem; }
          button { font: inherit; font-weight: 600; padding: .6rem 1.2rem; border: 0; border-radius: 8px; background: #1d4ed8; color: #fff; cursor: pointer; }
          button:focus-visible { outline: 3px solid #1d4ed8; outline-offset: 2px; }
          p.note { color: #4b5563; font-size: .9rem; }
          @media (prefers-color-scheme: dark) {
            body { background: #111214; color: #ececef; }
            main { background: #1c1d21; }
            p.note { color: #a1a1aa; }
            button { background: #3b82f6; color: #0b0b0c; }
          }
        </style>
        </head>
        <body>
        <main>
          <h1>Sign in to ApplyTrack</h1>
          <form method="post" action="/api/auth/verify">
            <input type="hidden" name="token" value="{{WebUtility.HtmlEncode(token)}}">
            <p><button type="submit" autofocus>Sign in</button></p>
          </form>
          <p class="note">This works only in the browser you requested the link from.</p>
        </main>
        </body>
        </html>
        """;

    private static IResult Unauthorized() =>
        Results.Json(new { detail = "authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
}
