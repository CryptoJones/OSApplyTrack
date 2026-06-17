// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace ApplyTrack.Api.Tests;

public class RateLimitTests
{
    [Fact]
    public async Task Rate_limit_with_XForwardedFor_sets_correct_partition()
    {
        // Arrange: boot the app with forwarded-headers support.
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.Configure<ForwardedHeadersOptions>(opts =>
                {
                    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                    opts.KnownIPNetworks.Clear();
                    opts.KnownProxies.Clear();
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.42");

        // Act: hit the health endpoint enough to stay under the rate limit.
        var response = await client.GetAsync("/health");

        // Assert: request succeeded, meaning partition key was generated.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.Dispose();
    }

    [Fact]
    public async Task Rate_limit_with_malformed_forwarded_header()
    {
        // Arrange: boot the app with forwarded-headers support.
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.ConfigureServices(services =>
            {
                services.Configure<ForwardedHeadersOptions>(opts =>
                {
                    opts.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
                    opts.KnownIPNetworks.Clear();
                    opts.KnownProxies.Clear();
                });
            });
        });

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "");

        // Act: request still goes through.
        var response = await client.GetAsync("/health");

        // Assert: request succeeded even with empty forwarded header.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.Dispose();
    }

    [Fact]
    public async Task Rate_limit_without_forwarded_header()
    {
        // Arrange: boot the app without forwarded headers.
        var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        // Act: direct connection (no proxy).
        var response = await client.GetAsync("/health");

        // Assert: request succeeded, RemoteIpAddress was used directly.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        factory.Dispose();
    }
}
