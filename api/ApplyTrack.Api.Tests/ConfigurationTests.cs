// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using System.Net;

namespace ApplyTrack.Api.Tests;

[Collection(PostgresCollection.Name)]
public class ConfigurationTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;

    public ConfigurationTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public void MigrationTimeoutSeconds_default_is_60()
    {
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds(null, 60));
    }

    [Fact]
    public void MigrationTimeoutSeconds_custom_value()
    {
        Assert.Equal(120, TimeoutConfiguration.PositiveTimeoutSeconds("120", 60));
    }

    [Fact]
    public void MigrationTimeoutSeconds_invalid_falls_back_to_default()
    {
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("-1", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("0", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("abc", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("", 60));
    }

    [Fact]
    public async Task App_boots_with_custom_MigrationTimeoutSeconds()
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.UseSetting("MigrationTimeoutSeconds", "120");
        });
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/health");
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public void Forwarded_headers_keep_safe_loopback_defaults()
    {
        var configuration = new ConfigurationBuilder().Build();
        var options = ForwardedHeadersConfiguration.Create(configuration);

        Assert.Equal(1, options.ForwardLimit);
        Assert.NotEmpty(options.KnownProxies);
        Assert.DoesNotContain(IPAddress.Any, options.KnownProxies);
        Assert.DoesNotContain(IPAddress.IPv6Any, options.KnownProxies);
    }

    [Fact]
    public void Forwarded_headers_add_configured_proxy_and_network()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "192.0.2.10",
                ["ForwardedHeaders:KnownNetworks:0"] = "198.51.100.0/24",
            })
            .Build();

        var options = ForwardedHeadersConfiguration.Create(configuration);

        Assert.Contains(IPAddress.Parse("192.0.2.10"), options.KnownProxies);
        Assert.Contains(IPNetwork.Parse("198.51.100.0/24"), options.KnownIPNetworks);
    }

    [Theory]
    [InlineData("ForwardedHeaders:KnownProxies:0", "not-an-ip")]
    [InlineData("ForwardedHeaders:KnownNetworks:0", "192.0.2.1/not-a-prefix")]
    public void Forwarded_headers_reject_invalid_configuration(string key, string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();

        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            _ = ForwardedHeadersConfiguration.Create(configuration);
        });

        Assert.Contains(value, error.Message);
    }
}
