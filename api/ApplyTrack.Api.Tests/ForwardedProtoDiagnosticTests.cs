// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Middleware;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Unit tests for the #229 diagnostic: warn once, and only once, when a proxy's
/// X-Forwarded-Proto=https is dropped because the peer isn't trusted (the tell that
/// Secure cookies + app HSTS are silently off). No Postgres, no host — pure middleware.
/// </summary>
public class ForwardedProtoDiagnosticTests
{
    [Fact]
    public async Task Warns_when_forwarded_https_is_not_honored()
    {
        var log = new CapturingLogger<ForwardedProtoDiagnosticMiddleware>();
        var mw = new ForwardedProtoDiagnosticMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(HttpContextWith(isHttps: false, forwardedProto: "https", peer: "172.17.0.1"));

        var warning = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("172.17.0.1", warning.Message);
        Assert.Contains("FORWARDED_HEADERS_KNOWN_PROXY", warning.Message);
    }

    [Fact]
    public async Task Warns_only_once_per_process()
    {
        var log = new CapturingLogger<ForwardedProtoDiagnosticMiddleware>();
        var mw = new ForwardedProtoDiagnosticMiddleware(_ => Task.CompletedTask, log);

        for (var i = 0; i < 5; i++)
            await mw.InvokeAsync(HttpContextWith(isHttps: false, forwardedProto: "https", peer: "172.17.0.1"));

        Assert.Single(log.Entries);
    }

    [Fact]
    public async Task Silent_when_request_is_already_https()
    {
        // A trusted proxy applied the forwarded proto: IsHttps is true, nothing to warn about.
        var log = new CapturingLogger<ForwardedProtoDiagnosticMiddleware>();
        var mw = new ForwardedProtoDiagnosticMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(HttpContextWith(isHttps: true, forwardedProto: "https", peer: "10.0.0.1"));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Silent_for_plain_http_with_no_forwarded_proto()
    {
        // Local dev / a direct HTTP caller: no forwarded proto, so this is not a misconfig.
        var log = new CapturingLogger<ForwardedProtoDiagnosticMiddleware>();
        var mw = new ForwardedProtoDiagnosticMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(HttpContextWith(isHttps: false, forwardedProto: null, peer: "127.0.0.1"));

        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Silent_when_forwarded_proto_leads_with_http()
    {
        // The leftmost value is the original client scheme; a leading http is not HTTPS.
        var log = new CapturingLogger<ForwardedProtoDiagnosticMiddleware>();
        var mw = new ForwardedProtoDiagnosticMiddleware(_ => Task.CompletedTask, log);

        await mw.InvokeAsync(HttpContextWith(isHttps: false, forwardedProto: "http,https", peer: "172.17.0.1"));

        Assert.Empty(log.Entries);
    }

    private static DefaultHttpContext HttpContextWith(bool isHttps, string? forwardedProto, string peer)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = isHttps ? "https" : "http";
        if (forwardedProto is not null)
            ctx.Request.Headers["X-Forwarded-Proto"] = forwardedProto;
        ctx.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        return ctx;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
