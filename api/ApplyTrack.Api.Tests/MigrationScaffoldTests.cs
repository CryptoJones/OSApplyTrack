// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Step 0 scaffold proof: a disposable Postgres comes up, the DbUp runner applies
/// the embedded migrations, and the run is journaled. Step 1 builds real schema
/// and CRUD tests on this same fixture.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MigrationScaffoldTests(PostgresFixture pg)
{
    [Fact]
    public async Task Citext_extension_is_created()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM pg_extension WHERE extname = 'citext'", conn);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Migration_is_recorded_in_the_dbup_journal()
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT count(*) FROM schemaversions WHERE scriptname LIKE '%0000_extensions.sql'",
            conn);
        var count = Convert.ToInt64(await cmd.ExecuteScalarAsync());

        Assert.True(count >= 1, "DbUp should have journaled the 0000_extensions.sql migration.");
    }
}
