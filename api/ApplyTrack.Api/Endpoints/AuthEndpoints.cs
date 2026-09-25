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
    private static readonly TimeSpan SessionTtl = TimeSpan.FromDays(30);

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
                    var token = Tokens.NewOpaque();
                    await tokens.CreateAsync(userId, Tokens.Sha256(token), DateTimeOffset.UtcNow + TokenTtl);
                    await email.SendMagicLinkAsync(address, $"{origin}/api/auth/verify?token={token}");
                }
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

    private static IResult Unauthorized() =>
        Results.Json(new { detail = "authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
}
