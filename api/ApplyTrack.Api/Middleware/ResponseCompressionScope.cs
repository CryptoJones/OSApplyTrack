// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// Which responses are compressed (#352): the SPA's static files, unconditionally, and the
/// three big lists the SPA fetches and polls. Everything else under <c>/api</c> stays
/// plain — BREACH needs a compressed body that reflects attacker input next to a secret,
/// and an allow-list keeps a future endpoint from becoming one by default.
/// </summary>
public static class ResponseCompressionScope
{
    private static readonly string[] ApiLists = ["/api/apps", "/api/errors", "/api/pipeline"];

    public static bool Applies(HttpContext ctx)
    {
        var path = ctx.Request.Path;
        if (!path.StartsWithSegments("/api")) return true;
        return HttpMethods.IsGet(ctx.Request.Method)
            && Array.Exists(ApiLists, p => path.Equals(p, StringComparison.OrdinalIgnoreCase));
    }

    public static IServiceCollection AddScopedResponseCompression(this IServiceCollection services)
    {
        services.AddResponseCompression(o =>
        {
            // The deploy terminates TLS in front of Kestrel or in it; either way the scope
            // above, not the transport, is what decides.
            o.EnableForHttps = true;
            o.Providers.Add<BrotliCompressionProvider>();
            o.Providers.Add<GzipCompressionProvider>();
            o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Append("image/svg+xml");
        });
        services.Configure<BrotliCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);
        return services;
    }
}
