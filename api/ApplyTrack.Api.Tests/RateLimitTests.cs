// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using ApplyTrack.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Asserts that the rate-limit partition key uses the client IP from forwarded
/// headers (via UseForwardedHeaders → RemoteIpAddress) rather than raw header
/// parsing, and that it falls back to the direct connection IP otherwise.
/// Each test boots a fresh factory so rate-limit state is unshared.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RateLimitTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;

    public RateLimitTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    /// <summary>Seeds a fresh session and returns an authenticated client.</summary>
    private async Task<HttpClient> AuthenticatedClient()
    {
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return client;
    }

    [Fact]
    public async Task Poll_rate_limit_exhausted_with_forwarded_for_returns_429()
    {
        var client = await AuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");

        for (var i = 0; i < 15; i++)
        {
            var res = await client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var rejected = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_with_malformed_forwarded_header_falls_back_to_remote_ip()
    {
        var client = await AuthenticatedClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "");

        for (var i = 0; i < 15; i++)
        {
            var res = await client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var rejected = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Rate_limit_without_forwarded_header_uses_remote_ip()
    {
        var client = await AuthenticatedClient();

        for (var i = 0; i < 15; i++)
        {
            var res = await client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var rejected = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }
}
