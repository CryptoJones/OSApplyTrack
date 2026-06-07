// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Auth;

/// <summary>
/// The tenancy choke-point. Resolves the session cookie to a user/tenant and stamps
/// the request's <see cref="TenantContext"/> — the one place a tenant_id is derived.
/// Protected <c>/api</c> routes get a 401 <c>{"detail"}</c> when unauthenticated; the
/// auth routes (<c>/api/auth/*</c>), static files, and <c>/health</c> pass through, so
/// the SPA shell loads and login works before there is a session.
/// </summary>
public sealed class TenantMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, TenantContext tenant, SessionRepo sessions)
    {
        if (context.Request.Cookies.TryGetValue(AuthCookie.Name, out var sid) && !string.IsNullOrEmpty(sid))
        {
            tenant.UserId = await sessions.ResolveUserIdAsync(sid);
        }

        if (RequiresAuth(context.Request.Path) && !tenant.IsAuthenticated)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(new { detail = "authentication required" });
            return;
        }

        await next(context);
    }

    private static bool RequiresAuth(PathString path) =>
        path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth");
}
