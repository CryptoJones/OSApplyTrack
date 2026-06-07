// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Testcontainers.PostgreSql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Spins a disposable Postgres in a container for the test run and brings the
/// schema up with the real DbUp migration runner — the same one the API uses.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17-alpine")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Migrator.Upgrade(ConnectionString);
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
