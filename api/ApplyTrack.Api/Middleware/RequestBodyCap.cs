// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using Microsoft.AspNetCore.Http.Features;

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// A hard byte cap for the routes that take large bodies (account import, résumé PDF
/// upload, scrape). A declared <c>Content-Length</c> over the cap is refused before the
/// body is read; a chunked body (no <c>Content-Length</c>) is read into memory up to the
/// cap and refused the moment it passes it, so it can no longer fall through to Kestrel's
/// 30 MB default (#344). The server's own <see cref="IHttpMaxRequestBodySizeFeature"/> is
/// lowered too where the host supports it.
/// </summary>
public static class RequestBodyCap
{
    public static Func<HttpContext, RequestDelegate, Task> Enforce(long maxBytes) =>
        async (ctx, next) =>
        {
            var feature = ctx.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (feature is { IsReadOnly: false })
                feature.MaxRequestBodySize = maxBytes;

            if (ctx.Request.ContentLength is { } declared)
            {
                if (declared > maxBytes)
                {
                    await Reject(ctx, maxBytes);
                    return;
                }
                await next(ctx);
                return;
            }

            var buffered = new MemoryStream();
            ctx.Response.RegisterForDispose(buffered);
            var chunk = new byte[81920];
            try
            {
                int read;
                while ((read = await ctx.Request.Body.ReadAsync(chunk, ctx.RequestAborted)) > 0)
                {
                    if (buffered.Length + read > maxBytes)
                    {
                        await Reject(ctx, maxBytes);
                        return;
                    }
                    buffered.Write(chunk, 0, read);
                }
            }
            catch (BadHttpRequestException ex) when (
                ex.StatusCode == StatusCodes.Status413RequestEntityTooLarge)
            {
                await Reject(ctx, maxBytes);
                return;
            }

            buffered.Position = 0;
            ctx.Request.Body = buffered;
            ctx.Request.ContentLength = buffered.Length;
            await next(ctx);
        };

    private static async Task Reject(HttpContext ctx, long maxBytes)
    {
        ctx.Response.StatusCode = StatusCodes.Status413RequestEntityTooLarge;
        await ctx.Response.WriteAsJsonAsync(new
        {
            detail = $"request body exceeds the {maxBytes / 1024} KiB limit",
        });
    }
}
