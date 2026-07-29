// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using Microsoft.AspNetCore.Http.Features;

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// Caps ordinary JSON mutations before model binding. Large account imports and
/// multipart résumé uploads retain their purpose-built limits.
/// </summary>
public sealed class JsonBodyLimitMiddleware(RequestDelegate next)
{
    public const long MaxBytes = 1024 * 1024;

    public async Task InvokeAsync(HttpContext context)
    {
        if (!ShouldLimit(context.Request))
        {
            await next(context);
            return;
        }

        var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (feature is { IsReadOnly: false })
            feature.MaxRequestBodySize = MaxBytes;

        if (context.Request.ContentLength > MaxBytes)
        {
            await Reject(context);
            return;
        }

        try
        {
            await next(context);
        }
        catch (BadHttpRequestException ex) when (
            ex.StatusCode == StatusCodes.Status413RequestEntityTooLarge
            && !context.Response.HasStarted)
        {
            await Reject(context);
        }
    }

    private static bool ShouldLimit(HttpRequest request) =>
        request.Path.StartsWithSegments("/api")
        && request.Path != "/api/account/import"
        && request.Method is "POST" or "PUT" or "PATCH"
        && request.ContentType?.StartsWith(
            "application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static async Task Reject(HttpContext context)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
        await context.Response.WriteAsJsonAsync(new
        {
            detail = $"JSON request body exceeds the {MaxBytes / 1024} KiB limit",
        });
    }
}
