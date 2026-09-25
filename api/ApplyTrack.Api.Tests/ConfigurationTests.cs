// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Endpoints;
using Microsoft.AspNetCore.Http;
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

/// <summary>
/// Pure unit tests for <see cref="EmailOptions.Validate"/> — the boot-time guard that
/// turns the #228 magic-link outage (Email:Host set with no usable From address, which
/// throws an opaque 500 on every sign-in) into a loud failure at startup. No Postgres
/// fixture: these never touch the database, so they run without Docker.
/// </summary>
public class EmailOptionsTests
{
    [Fact]
    public void Validate_noop_when_no_host_configured()
    {
        // No host -> console sender stands in; a blank From must not fail boot.
        new EmailOptions().Validate();
    }

    [Fact]
    public void Validate_passes_with_explicit_from()
    {
        new EmailOptions { Host = "smtp.example.com", From = "apply@example.com" }.Validate();
    }

    [Fact]
    public void Validate_falls_back_to_username_for_from()
    {
        new EmailOptions { Host = "smtp.example.com", Username = "apply@example.com" }.Validate();
    }

    [Fact]
    public void Validate_rejects_host_without_from_or_username()
    {
        // The exact #228 misconfiguration: Host set, From/Username both blank -> empty
        // EffectiveFrom -> MailKit throws per-request. Boot must reject it instead.
        var error = Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { Host = "smtp.example.com" }.Validate());
        Assert.Contains("Email:From", error.Message);
    }

    [Fact]
    public void Validate_rejects_unparseable_from()
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { Host = "smtp.example.com", From = "not an address" }.Validate());
        Assert.Contains("not a valid email address", error.Message);
    }

    [Fact]
    public void RequirePublicBaseUrl_noop_for_the_console_sender()
    {
        new EmailOptions().RequirePublicBaseUrl(null);
    }

    [Fact]
    public void RequirePublicBaseUrl_passes_with_an_absolute_https_origin()
    {
        new EmailOptions { Host = "smtp.example.com", From = "apply@example.com" }
            .RequirePublicBaseUrl("https://apply.example.com");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("apply.example.com")]
    [InlineData("ftp://apply.example.com")]
    [InlineData("https://apply.example.com/?x=1")]
    [InlineData("https://apply.example.com/#top")]
    [InlineData("https://user:pw@apply.example.com")]
    public void RequirePublicBaseUrl_rejects_smtp_without_a_pinned_origin(string? baseUrl)
    {
        // #337: real mail + no pinned origin = links built from the attacker's Host header.
        var error = Assert.Throws<InvalidOperationException>(
            () => new EmailOptions { Host = "smtp.example.com", From = "apply@example.com" }
                .RequirePublicBaseUrl(baseUrl));
        Assert.Contains("App__PublicBaseUrl", error.Message);
    }
}

/// <summary>
/// <see cref="AuthEndpoints.LinkOrigin"/> — where emailed sign-in links point (#337):
/// the configured origin always; the request Host only when it is loopback.
/// </summary>
public class LinkOriginTests
{
    private static HttpRequest Request(string host)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Scheme = "http";
        ctx.Request.Host = new HostString(host);
        return ctx.Request;
    }

    [Fact]
    public void Configured_base_url_wins_and_is_trimmed()
    {
        Assert.Equal("https://apply.example",
            AuthEndpoints.LinkOrigin(" https://apply.example/ ", Request("evil.example")));
    }

    [Theory]
    [InlineData("localhost")]
    [InlineData("localhost:8080")]
    [InlineData("app.localhost:8080")]
    [InlineData("127.0.0.1:8080")]
    [InlineData("[::1]:8080")]
    public void Unset_falls_back_to_a_loopback_host(string host)
    {
        Assert.Equal($"http://{host}", AuthEndpoints.LinkOrigin(null, Request(host)));
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("localhost.evil.example")]
    [InlineData("192.168.1.10:8080")]
    [InlineData("10.0.0.1")]
    public void Unset_with_a_non_loopback_host_yields_no_origin(string host)
    {
        Assert.Null(AuthEndpoints.LinkOrigin("", Request(host)));
    }
}
