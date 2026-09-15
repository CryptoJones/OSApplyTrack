// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// Surfaces the silent misconfiguration behind #229: a TLS reverse proxy forwards
/// <c>X-Forwarded-Proto: https</c>, but <see cref="UseForwardedHeadersExtensions"/> did
/// not honor it (<c>Request.IsHttps</c> is still false) because the immediate peer is not
/// a trusted proxy. Untrusted forwarded headers are left on the request rather than
/// applied, so the tell is a request seen as HTTP that still carries a forwarded proto of
/// https. When that happens the session cookie loses its <c>Secure</c> flag and the app's
/// HSTS is not emitted — invisibly, on a live HTTPS instance.
///
/// This logs one warning per process at the first such request, naming the actual peer
/// address (exactly the value to trust) and the env var to set, so the operator can fix
/// <c>ForwardedHeaders:KnownProxies</c>/<c>KnownNetworks</c> instead of chasing a missing
/// cookie flag. It only observes; it never trusts the header, so the fail-closed policy
/// in <see cref="ApplyTrack.Api.ForwardedHeadersConfiguration"/> is unchanged. A correctly
/// trusted proxy (or plain-HTTP local dev) never trips it.
/// </summary>
public sealed class ForwardedProtoDiagnosticMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ForwardedProtoDiagnosticMiddleware> _log;
    private int _warned; // 0 until the one-time warning has been emitted

    public ForwardedProtoDiagnosticMiddleware(RequestDelegate next, ILogger<ForwardedProtoDiagnosticMiddleware> log)
    {
        _next = next;
        _log = log;
    }

    public Task InvokeAsync(HttpContext context)
    {
        // Order matters: only claim the one-shot slot once the real condition holds, so a
        // plain-HTTP dev request (no forwarded proto) can't silently spend it.
        if (!context.Request.IsHttps
            && ForwardedProtoIsHttps(context.Request)
            && Interlocked.CompareExchange(ref _warned, 1, 0) == 0)
        {
            _log.LogWarning(
                "X-Forwarded-Proto says https but the request is seen as HTTP: the reverse proxy "
                + "at {Peer} is not a trusted proxy, so the session cookie's Secure flag and the "
                + "app's HSTS are disabled. Add {Peer} to ForwardedHeaders:KnownProxies "
                + "(env FORWARDED_HEADERS_KNOWN_PROXY), or its subnet to "
                + "ForwardedHeaders:KnownNetworks (FORWARDED_HEADERS_KNOWN_NETWORK). In the "
                + "docker-compose stack this is the bridge gateway/subnet in front of the container.",
                context.Connection.RemoteIpAddress, context.Connection.RemoteIpAddress);
        }

        return _next(context);
    }

    // The leftmost X-Forwarded-Proto value is the original client scheme; a proxy chain
    // appends to the right. Treat only a leading https as "the client was on HTTPS".
    private static bool ForwardedProtoIsHttps(HttpRequest request)
    {
        var proto = request.Headers["X-Forwarded-Proto"].ToString();
        if (proto.Length == 0)
            return false;
        var comma = proto.IndexOf(',');
        var first = (comma >= 0 ? proto[..comma] : proto).Trim();
        return string.Equals(first, "https", StringComparison.OrdinalIgnoreCase);
    }
}
