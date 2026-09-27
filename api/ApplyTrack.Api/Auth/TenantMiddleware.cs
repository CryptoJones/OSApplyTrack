// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Data;
using Microsoft.Net.Http.Headers;

namespace ApplyTrack.Api.Auth;

/// <summary>
/// The tenancy choke-point. Resolves the session cookie — or, failing that, a personal API
/// token sent as <c>Authorization: Bearer</c> (#356) — to a user/tenant and stamps the
/// request's <see cref="TenantContext"/>: the one place a tenant_id is derived from a
/// credential (the calendar feed resolves its own feed-only token, #354).
/// Protected <c>/api</c> routes get a 401 <c>{"detail"}</c> when unauthenticated; the
/// auth routes (<c>/api/auth/*</c>), static files, and <c>/health</c> pass through, so
/// the SPA shell loads and login works before there is a session.
///
/// A token never outranks a session: a <c>read</c> token gets the GET routes, a
/// <c>write</c> token every method, and neither reaches the account's credentials, the
/// secret-bearing settings, or its deletion (<see cref="TokenMayReach"/>). A browser can't attach the header cross-site
/// without a CORS grant the API never gives, so bearer requests need no CSRF defense and
/// the cookie's (SameSite=Lax plus JSON-only mutations) is unchanged.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, SessionRepo sessions, IDbConnection conn)
    {
        if (context.Request.Cookies.TryGetValue(AuthCookie.Name, out var sid) && !string.IsNullOrEmpty(sid))
        {
            if (await sessions.ResolveAsync(sid) is { } session)
            {
                tenant.UserId = session.UserId;
                tenant.SessionId = session.Id;
                // The expiry slid forward (#346): re-issue the cookie so the browser keeps it
                // as long as the server does.
                if (session.RenewedUntil is { } until)
                    context.Response.Cookies.Append(AuthCookie.Name, sid, AuthCookie.Options(until, context.Request.IsHttps));
            }
        }

        var requiresAuth = RequiresAuth(context.Request.Path);
        if (!tenant.IsAuthenticated && requiresAuth && BearerToken(context.Request) is { } bearer
            && await ApiTokenRepo.ResolveBearerAsync(conn, bearer) is { } hit)
        {
            tenant.UserId = hit.TenantId;
            tenant.TokenId = hit.Id;
            tenant.TokenScope = hit.Scope;
        }

        if (requiresAuth && !tenant.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { detail = "authentication required" });
            return;
        }

        if (tenant.TokenScope is { } scope && !TokenMayReach(context.Request, scope))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                detail = scope == ApiTokenRepo.ReadScope && !IsSafe(context.Request.Method)
                    ? "this API token is read-only"
                    : "API tokens can't manage the account or its credentials; sign in",
            });
            return;
        }

        await next(context);
    }

    /// <summary>
    /// Whether a personal token of <paramref name="scope"/> may make this request. Never the
    /// account's credentials — its tokens, sessions, calendar link — nor deleting it: a leaked
    /// token must not be able to mint more, lock its owner out, or outlive its revocation.
    /// Nor the settings that hold secrets or say where data goes — the LLM endpoint and key,
    /// Telegram and mailbox credentials, job-board passwords — so it can't redirect prompts,
    /// codes or messages. Export and import stay open, being what a script is for.
    /// </summary>
    public static bool TokenMayReach(HttpRequest request, string scope)
    {
        var path = request.Path;
        if (path.StartsWithSegments("/api/calendar/feed") || path.StartsWithSegments("/api/llm-settings")
            || path.StartsWithSegments("/api/notifications") || path.StartsWithSegments("/api/board-accounts"))
            return false;
        if (path.StartsWithSegments("/api/account")
            && !path.StartsWithSegments("/api/account/export") && !path.StartsWithSegments("/api/account/import"))
            return false;
        return scope == ApiTokenRepo.WriteScope || IsSafe(request.Method);
    }

    private static bool IsSafe(string method) => HttpMethods.IsGet(method) || HttpMethods.IsHead(method);

    private static string? BearerToken(HttpRequest request)
    {
        var header = request.Headers[HeaderNames.Authorization].ToString();
        const string scheme = "Bearer ";
        return header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase) ? header[scheme.Length..].Trim() : null;
    }

    // The calendar feed (#354) carries its own feed-only token: a calendar client can't send the cookie.
    private static bool RequiresAuth(PathString path) =>
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth")
        && !path.Equals(Endpoints.RemindersEndpoints.FeedPath, StringComparison.OrdinalIgnoreCase);
}
