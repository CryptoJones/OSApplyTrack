// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.ComponentModel.DataAnnotations;
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
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

    public static void MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        // Always 200, whether or not the address is known/valid — no account
        // enumeration. Upsert the user, store only the sha256 of a fresh token, email
        // the link. Per-IP rate-limited ("auth" policy) so the always-200 surface
        // can't be abused for email spam or enumeration probing.
        app.MapPost("/api/auth/request", async (
            LinkRequest body, HttpContext ctx, IConfiguration config,
            UserRepo users, MagicTokenRepo tokens, IEmailSender email) =>
        {
            // Validate shape + cap length (RFC 5321 max 254) before any row is born:
            // a junk or unbounded address must never reach EnsureAsync. Still always
            // 200 below — a rejected address is silently dropped, no enumeration signal.
            var address = (body.Email ?? "").Trim();
            if (address.Length is > 0 and <= 254 && new EmailAddressAttribute().IsValid(address))
            {
                var userId = await users.EnsureAsync(address);
                var token = Tokens.NewOpaque();
                await tokens.CreateAsync(userId, Tokens.Sha256(token), DateTimeOffset.UtcNow + TokenTtl);
                // Build the link from the operator's configured origin, NOT the request's
                // Host header. The Host is attacker-suppliable (and AllowedHosts may be "*"),
                // so deriving the link from it lets an attacker request a victim's link
                // pointed at attacker.example — host-header poisoning -> token capture ->
                // account takeover. App:PublicBaseUrl pins it; we fall back to the request
                // origin only when unset, for zero-config local dev.
                var link = $"{Origin(config, ctx.Request)}/api/auth/verify?token={token}";
                await email.SendMagicLinkAsync(address, link);
            }

            return Results.Ok(new { ok = true });
        }).RequireRateLimiting("auth");

        // Consume the token (single-use, unexpired), mint a session, set the cookie,
        // and redirect to / so the token leaves the URL/history.
        app.MapGet("/api/auth/verify", async (
            [FromQuery] string? token, HttpContext ctx,
            MagicTokenRepo tokens, SessionRepo sessions) =>
        {
            if (string.IsNullOrEmpty(token))
                return Results.Redirect("/?error=invalid_link");

            var userId = await tokens.ConsumeAsync(Tokens.Sha256(token));
            if (userId is null)
                return Results.Redirect("/?error=invalid_link");

            var sid = Tokens.NewOpaque();
            var expires = DateTimeOffset.UtcNow + SessionTtl;
            await sessions.CreateAsync(sid, userId.Value, expires);
            ctx.Response.Cookies.Append(AuthCookie.Name, sid, AuthCookie.Options(expires, ctx.Request.IsHttps));
            return Results.Redirect("/");
        });

        // Drop the session row (instant revocation) and clear the cookie.
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

    // The canonical public origin for links we email out. Prefer the operator-configured
    // App:PublicBaseUrl (e.g. https://apply.example.com); fall back to the request's own
    // scheme+host only when unset, so local/dev works with no config. Never trust the
    // request Host when a configured value exists — see the host-header note above.
    private static string Origin(IConfiguration config, HttpRequest request)
    {
        var configured = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
        return configured.Length > 0 ? configured : $"{request.Scheme}://{request.Host}";
    }

    private static IResult Unauthorized() =>
        Results.Json(new { detail = "authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
}
