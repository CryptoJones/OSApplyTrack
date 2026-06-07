// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

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
        // the link. (Rate-limiting is a follow-up; the always-200 + 15-min TTL caps
        // the blast radius in the meantime.)
        app.MapPost("/api/auth/request", async (
            LinkRequest body, HttpContext ctx,
            UserRepo users, MagicTokenRepo tokens, IEmailSender email) =>
        {
            var address = (body.Email ?? "").Trim();
            if (address.Length > 0 && address.Contains('@'))
            {
                var userId = await users.EnsureAsync(address);
                var token = Tokens.NewOpaque();
                await tokens.CreateAsync(userId, Tokens.Sha256(token), DateTimeOffset.UtcNow + TokenTtl);
                var link = $"{ctx.Request.Scheme}://{ctx.Request.Host}/api/auth/verify?token={token}";
                await email.SendMagicLinkAsync(address, link);
            }

            return Results.Ok(new { ok = true });
        });

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

    private static IResult Unauthorized() =>
        Results.Json(new { detail = "authentication required" }, statusCode: StatusCodes.Status401Unauthorized);
}
