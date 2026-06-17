// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using DbUp;
using DbUp.Engine;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Runs the embedded, idempotent <c>.sql</c> migrations against Postgres via DbUp.
/// The same runner is used at API startup and by the test fixture, so local and
/// containerized deploys converge on one schema — the cross-runtime contract.
/// </summary>
public static class Migrator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    public static DatabaseUpgradeResult Upgrade(string connectionString, TimeSpan? timeout = null)
    {
        var migrationTimeout = timeout ?? DefaultTimeout;

        // Create the target database if it does not exist yet, so a fresh local
        // Postgres or a fresh container is usable with no manual setup step.
        EnsureDatabase.For.PostgresqlDatabase(connectionString);

        var upgrader = DeployChanges.To
            .PostgresqlDatabase(connectionString)
            .WithScriptsEmbeddedInAssembly(typeof(Migrator).Assembly)
            .WithExecutionTimeout(migrationTimeout)
            .LogToConsole()
            .Build();

        var result = upgrader.PerformUpgrade();
        if (!result.Successful)
        {
            throw new InvalidOperationException("Database migration failed.", result.Error);
        }

        return result;
    }
}
