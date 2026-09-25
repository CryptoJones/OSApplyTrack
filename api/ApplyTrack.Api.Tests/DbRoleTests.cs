// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The least-privilege roles (#345): the migrating container creates them from their
/// passwords, and the run-always grants give each exactly what its runtime executes —
/// proved by connecting as the role and running those statements, and the ones it must
/// not.
/// </summary>
[Collection(PostgresCollection.Name)]
public class DbRoleTests(PostgresFixture pg)
{
    private const string Denied = "42501"; // insufficient_privilege

    private void UpgradeWith(string agentPw, string pollerPw) =>
        Migrator.Upgrade(pg.ConnectionString, rolePasswords: new Dictionary<string, string?>
        {
            [Migrator.AgentRole] = agentPw,
            [Migrator.PollerRole] = pollerPw,
        });

    private async Task<NpgsqlConnection> OpenAsAsync(string role, string password)
    {
        var cs = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = role, Password = password, Pooling = false,
        }.ConnectionString;
        var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task AssertDeniedAsync(NpgsqlConnection conn, string sql, object? args = null)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.ExecuteAsync(sql, args));
        Assert.Equal(Denied, ex.SqlState);
    }

    [Fact]
    public async Task The_poller_role_runs_the_poller_statements_and_nothing_else()
    {
        UpgradeWith("agent-pw", "poller-pw");
        await using var owner = new NpgsqlConnection(pg.ConnectionString);
        await owner.OpenAsync();
        var t = await TestAuth.EnsureUserAsync(owner, TestAuth.UniqueEmail());
        await owner.ExecuteAsync(
            "INSERT INTO board_accounts (tenant_id, host, username, password_ciphertext) VALUES (@t, 'linkedin.com', 'ada', 'sealed-pw')",
            new { t });

        await using var poller = await OpenAsAsync(Migrator.PollerRole, "poller-pw");
        // What worker.py and db.py run.
        Assert.Contains(t, await poller.QueryAsync<long>("SELECT id FROM users WHERE status = 'active'"));
        await poller.QueryAsync("SELECT keywords FROM search_profiles WHERE tenant_id = @t", new { t });
        await poller.QueryAsync("SELECT company FROM blacklist WHERE tenant_id = @t", new { t });
        await poller.QueryAsync("SELECT to_jsonb(s) ->> 'linkedin_easy' FROM agent_settings s WHERE tenant_id = @t", new { t });
        await poller.ExecuteAsync(
            "INSERT INTO applications (tenant_id, name, company, role) VALUES (@t, 'acme-eng.md', 'Acme', 'Eng')", new { t });
        await poller.QueryAsync("SELECT link, company, role FROM applications WHERE tenant_id = @t", new { t });
        await poller.ExecuteAsync("INSERT INTO seen (tenant_id, kind, key) VALUES (@t, 'url', 'x') ON CONFLICT DO NOTHING", new { t });
        await poller.QueryAsync("SELECT kind, key FROM seen WHERE tenant_id = @t", new { t });
        Assert.Equal("ada", await poller.ExecuteScalarAsync<string>(
            "SELECT username FROM board_accounts WHERE tenant_id = @t AND host = ANY(@h)", new { t, h = new[] { "linkedin.com" } }));
        await poller.ExecuteAsync(
            "UPDATE board_accounts SET session_ciphertext = 's', session_expires_at = now(), updated_at = now() WHERE tenant_id = @t AND host = ANY(@h)",
            new { t, h = new[] { "linkedin.com" } });
        await poller.ExecuteAsync("INSERT INTO poll_requests (tenant_id) VALUES (@t)", new { t });
        Assert.NotEmpty(await poller.QueryAsync<long>("DELETE FROM poll_requests RETURNING tenant_id"));

        // And what it must not.
        await AssertDeniedAsync(poller, "SELECT * FROM sessions");
        await AssertDeniedAsync(poller, "SELECT * FROM magic_tokens");
        await AssertDeniedAsync(poller, "SELECT email FROM users");
        await AssertDeniedAsync(poller, "SELECT * FROM resume_profiles");
        await AssertDeniedAsync(poller, "SELECT password_ciphertext FROM board_accounts");
        await AssertDeniedAsync(poller, "UPDATE applications SET status = 'applied' WHERE tenant_id = @t", new { t });
        await AssertDeniedAsync(poller, "DELETE FROM applications WHERE tenant_id = @t", new { t });
        await AssertDeniedAsync(poller, "CREATE TABLE poller_was_here (x int)");
    }

    [Fact]
    public async Task The_agent_role_updates_application_fields_but_never_tenant_or_name()
    {
        UpgradeWith("agent-pw", "poller-pw");
        await using var owner = new NpgsqlConnection(pg.ConnectionString);
        await owner.OpenAsync();
        // What an older release granted: the REVOKE on the next boot must take it back.
        await owner.ExecuteAsync("GRANT UPDATE ON applications TO applytrack_agent");
        UpgradeWith("agent-pw", "poller-pw");
        long t = await TestAuth.EnsureUserAsync(owner, TestAuth.UniqueEmail()),
            other = await TestAuth.EnsureUserAsync(owner, TestAuth.UniqueEmail());
        var name = await new ApplicationRepo(owner, t).CreateAsync(new AppFields { Company = "Acme", Role = "Eng" });

        await using var agent = await OpenAsAsync(Migrator.AgentRole, "agent-pw");
        var apps = new ApplicationRepo(agent, t);
        var rec = (await apps.GetAsync(name))!;
        await apps.UpdateStructuredAsync(name, rec.Fields with { Status = "applied" }, rec.Version.ToString());
        Assert.Equal("applied", (await apps.GetAsync(name))!.Fields.Status);

        await AssertDeniedAsync(agent, "UPDATE applications SET tenant_id = @other WHERE tenant_id = @t", new { t, other });
        await AssertDeniedAsync(agent, "UPDATE applications SET name = 'stolen.md' WHERE tenant_id = @t", new { t });
        await AssertDeniedAsync(agent, "DELETE FROM applications WHERE tenant_id = @t", new { t });
        await AssertDeniedAsync(agent, "SELECT * FROM sessions");
    }

    [Fact]
    public async Task A_changed_password_is_applied_on_the_next_boot_and_a_blank_one_is_skipped()
    {
        UpgradeWith("agent-pw", "old-poller-pw");
        UpgradeWith("agent-pw", "new-poller-pw");
        await Assert.ThrowsAsync<PostgresException>(() => OpenAsAsync(Migrator.PollerRole, "old-poller-pw"));
        await using (await OpenAsAsync(Migrator.PollerRole, "new-poller-pw")) { }

        UpgradeWith("agent-pw", "");
        await using (await OpenAsAsync(Migrator.PollerRole, "new-poller-pw")) { }
        UpgradeWith("agent-pw", "poller-pw");
    }
}
