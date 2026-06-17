// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Tests the readiness probe's failure path. Uses the live Postgres container so the
/// app can boot (Migrator.Upgrade needs a real DB), then swaps the resolved
/// <see cref="NpgsqlDataSource"/> with one pointing at a closed port so the health
/// check itself fails and returns 503.
/// </summary>
[Collection(PostgresCollection.Name)]
public class HealthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public HealthEndpointTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            // Boot with a real database so the startup migration succeeds.
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.ConfigureTestServices(s =>
            {
                // Replace the data source with one targeting a closed port.
                // The health check will see a connection failure and return 503.
                s.RemoveAll<NpgsqlDataSource>();
                s.AddSingleton(NpgsqlDataSource.Create(
                    "Host=127.0.0.1;Port=5433;Database=test;Username=test;Password=test;Timeout=1"));
            });
        });
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Ready_returns_503_when_database_is_down()
    {
        var response = await _client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        Assert.Equal("disconnected", root.GetProperty("database").GetString());
    }
}
