// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Testcontainers.PostgreSql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Spins a disposable Postgres in a container for the test run and brings the
/// schema up with the real DbUp migration runner — the same one the API uses.
/// Shared by every test class via <see cref="PostgresCollection"/> so the whole
/// suite reuses one container (tests stay isolated by using distinct tenant ids).
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

/// <summary>
/// Binds <see cref="PostgresFixture"/> to one shared xUnit collection. Classes in
/// the collection run sequentially, so they can safely share the single database.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
