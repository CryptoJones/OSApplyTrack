// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using DbUp;
using DbUp.Engine;
using DbUp.ScriptProviders;
using DbUp.Support;
using Npgsql;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Runs the embedded, idempotent <c>.sql</c> migrations against Postgres via DbUp.
/// The same runner is used at API startup and by the test fixture, so local and
/// containerized deploys converge on one schema — the cross-runtime contract.
/// </summary>
public static class Migrator
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(1);

    /// <summary>The least-privilege login roles the migrating container keeps in step.</summary>
    public const string AgentRole = "applytrack_agent", PollerRole = "applytrack_poller";

    /// <param name="rolePasswords">Role name to password (<see cref="AgentRole"/>,
    /// <see cref="PollerRole"/>): each role with a non-blank password is created, or has
    /// its password reset, before the grants run — so turning a role on in an existing
    /// deploy is setting its password and restarting, no hand-run SQL (#345).</param>
    public static DatabaseUpgradeResult Upgrade(string connectionString, TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? rolePasswords = null)
    {
        var migrationTimeout = timeout ?? DefaultTimeout;

        // Everything the migrator opens is unpooled. Two reasons: a pooled gate
        // connection would return to the pool with its session — and the advisory
        // lock below — intact until its lazy reset, leaving the next starter waiting on
        // a lock nobody is using; and DbUp's EnsureDatabase leaks one pooled connection
        // per call (harmless for an app that migrates once at boot, fatal for a test
        // suite that boots a hundred hosts against one Postgres). Unpooled connections
        // close for real when they are closed.
        var unpooled = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Pooling = false,
        }.ConnectionString;

        // Create the target database if it does not exist yet, so a fresh local
        // Postgres or a fresh container is usable with no manual setup step.
        EnsureDatabase.For.PostgresqlDatabase(unpooled);

        // Two containers now boot from this image (the API and the agent worker) and
        // both migrate on startup. DbUp's journal is not a mutex, so serialize the
        // run on a session-level advisory lock; the second starter simply waits for
        // the first to finish and then finds nothing to do.
        using var gate = new NpgsqlConnection(unpooled);
        gate.Open();
        var lockTimeout = (int)Math.Max(1, migrationTimeout.TotalSeconds);
        using (var cmd = gate.CreateCommand())
        {
            cmd.CommandText = "SELECT pg_advisory_lock(hashtext('applytrack:migrate'))";
            cmd.CommandTimeout = lockTimeout;
            cmd.ExecuteNonQuery();
        }

        try
        {
            EnsureRoles(gate, rolePasswords);
            var assembly = typeof(Migrator).Assembly;
            var upgrader = DeployChanges.To
                .PostgresqlDatabase(unpooled)
                // The versioned scripts, run once each and journaled...
                .WithScriptsEmbeddedInAssembly(assembly, name => name.EndsWith(".sql") && !name.Contains(".Always."))
                // ...then Migrations/Always/*.sql on every boot (the agent role's grants),
                // after them so a table a new migration just created is covered.
                .WithScripts(new EmbeddedScriptProvider(assembly, name => name.Contains(".Always.") && name.EndsWith(".sql"),
                    Encoding.UTF8, new SqlScriptOptions { ScriptType = ScriptType.RunAlways, RunGroupOrder = DbUpDefaults.DefaultRunGroupOrder + 1 }))
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
        finally
        {
            using var unlock = gate.CreateCommand();
            unlock.CommandText = "SELECT pg_advisory_unlock(hashtext('applytrack:migrate'))";
            unlock.CommandTimeout = lockTimeout;
            unlock.ExecuteNonQuery();
        }
    }

    // CREATE/ALTER ROLE take no bind parameters, so the statement is built server-side
    // with format(%I, %L): the password is quoted by Postgres, never spliced in here. A
    // configured password that cannot be applied (an owner without CREATEROLE) fails the
    // boot: the api running while the agent or poller cannot sign in is the worse
    // outcome. Provisioning the role by hand? Leave its password unset on the api.
    private static void EnsureRoles(NpgsqlConnection conn, IReadOnlyDictionary<string, string?>? rolePasswords)
    {
        foreach (var (role, password) in rolePasswords ?? new Dictionary<string, string?>())
        {
            if (string.IsNullOrWhiteSpace(password))
                continue;
            try
            {
                using var build = conn.CreateCommand();
                build.CommandText = """
                    SELECT format(CASE WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = @r)
                                       THEN 'ALTER ROLE %I WITH LOGIN PASSWORD %L'
                                       ELSE 'CREATE ROLE %I WITH LOGIN PASSWORD %L' END, @r, @p)
                    """;
                build.Parameters.AddWithValue("r", role);
                build.Parameters.AddWithValue("p", password);
                var sql = (string)build.ExecuteScalar()!;
                using var run = conn.CreateCommand();
                run.CommandText = sql;
                run.ExecuteNonQuery();
                Console.WriteLine($"database role {role}: ready");
            }
            catch (PostgresException ex)
            {
                throw new InvalidOperationException(
                    $"cannot create or re-password database role {role} ({ex.MessageText}); grant the owner "
                    + "CREATEROLE, or create the role by hand and leave its password unset on the api", ex);
            }
        }
    }

    /// <summary>How many versioned scripts this build carries — what a current schema has journaled.</summary>
    public static int VersionedScriptCount() =>
        typeof(Migrator).Assembly.GetManifestResourceNames()
            .Count(n => n.EndsWith(".sql") && n.Contains(".Migrations.") && !n.Contains(".Always."));

    /// <summary>
    /// For a container that must NOT migrate — the agent worker running as its
    /// least-privilege role has no DDL rights — wait until the owner container has
    /// brought the schema up to this build's script count, then carry on. Throws
    /// after <paramref name="timeout"/> so a mis-paired image pair fails loudly
    /// instead of running against a schema it does not understand.
    /// </summary>
    public static void WaitUntilCurrent(string connectionString, TimeSpan timeout)
    {
        var need = VersionedScriptCount();
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var conn = new NpgsqlConnection(connectionString);
                conn.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT count(*) FROM schemaversions";
                var have = Convert.ToInt32(cmd.ExecuteScalar());
                if (have >= need)
                    return;
                Console.WriteLine($"schema has {have}/{need} migrations; waiting for the API to migrate…");
            }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
            {
                last = ex; // no table yet, no grants yet, DB still starting: all "not yet"
            }
            Thread.Sleep(2000);
        }
        throw new InvalidOperationException(
            $"the database schema did not become current within {timeout.TotalSeconds:0}s "
            + "(is the API container running and migrating?)", last);
    }
}
