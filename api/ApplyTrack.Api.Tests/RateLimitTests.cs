// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using ApplyTrack.Api.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Asserts that untrusted direct requests cannot replace the rate-limit partition
/// with X-Forwarded-For, while explicitly trusted proxies retain per-client buckets.
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
        _factory = CreateFactory(trustProxy: false);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private WebApplicationFactory<Program> CreateFactory(bool trustProxy) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            if (trustProxy)
                b.UseSetting("ForwardedHeaders:KnownProxies:0", "192.0.2.10");
            b.ConfigureTestServices(services =>
                services.AddSingleton<IStartupFilter>(
                    new RemoteIpStartupFilter(IPAddress.Parse("192.0.2.10"))));
        });

    /// <summary>Seeds a fresh session and returns an authenticated client.</summary>
    private async Task<HttpClient> AuthenticatedClient(
        WebApplicationFactory<Program>? factory = null)
    {
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = (factory ?? _factory).CreateClient();
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

    [Fact]
    public async Task Distinct_spoofed_forwarded_ips_share_the_direct_connection_bucket()
    {
        var client = await AuthenticatedClient();

        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        for (var i = 0; i < 15; i++)
        {
            var res = await client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        var exhausted = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.2");
        var other = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, other.StatusCode);
    }

    [Fact]
    public async Task Distinct_forwarded_ips_from_a_configured_proxy_get_independent_buckets()
    {
        await using var trustedFactory = CreateFactory(trustProxy: true);
        var client = await AuthenticatedClient(trustedFactory);

        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.1");
        for (var i = 0; i < 15; i++)
        {
            var res = await client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }
        var exhausted = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.2");
        var other = await client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    private static MultipartFormDataContent NotAPdf()
    {
        var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("not a pdf")), "resume", "resume.pdf");
        return form;
    }

    [Fact]
    public async Task Resume_upload_is_throttled_per_tenant_across_client_ips()
    {
        await using var trustedFactory = CreateFactory(trustProxy: true);
        var client = await AuthenticatedClient(trustedFactory);

        for (var i = 0; i < 10; i++)
        {
            client.DefaultRequestHeaders.Remove("X-Forwarded-For");
            client.DefaultRequestHeaders.Add("X-Forwarded-For", $"203.0.113.{i + 1}");
            var res = await client.PostAsync("/api/resume/upload", NotAPdf());
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        client.DefaultRequestHeaders.Remove("X-Forwarded-For");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "198.51.100.99");
        var rejected = await client.PostAsync("/api/resume/upload", NotAPdf());
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Upload_routes_are_throttled_per_ip_across_tenants()
    {
        for (var i = 0; i < 10; i++)
        {
            var tenant = await AuthenticatedClient();
            var res = await tenant.PostAsync("/api/resume/upload", NotAPdf());
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        var fresh = await AuthenticatedClient();
        var rejected = await fresh.PostAsync("/api/account/import", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    private sealed class RemoteIpStartupFilter(IPAddress remoteIp) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
            app =>
            {
                app.Use(async (context, nextMiddleware) =>
                {
                    context.Connection.RemoteIpAddress = remoteIp;
                    await nextMiddleware();
                });
                next(app);
            };
    }
}
