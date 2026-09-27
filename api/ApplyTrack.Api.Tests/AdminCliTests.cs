// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Admin;
using ApplyTrack.Api.Data;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The operator's CLI (#358): list accounts and their usage, manage the auto-apply
/// allowlist, and disable an account — with the owner's connection only.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AdminCliTests(PostgresFixture pg)
{
    private async Task<(int Code, string Out, string Err)> RunAsync(params string[] args) =>
        await RunWithAsync(pg.ConnectionString, args);

    private static async Task<(int Code, string Out, string Err)> RunWithAsync(string cs, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var code = await AdminCli.RunAsync(args, cs, o, e);
        return (code, o.ToString(), e.ToString());
    }

    private async Task<(NpgsqlConnection Conn, long T, string Email)> TenantAsync()
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var email = TestAuth.UniqueEmail();
        return (conn, await TestAuth.EnsureUserAsync(conn, email), email);
    }

    private static string Line(string output, long t) =>
        output.Split('\n').Single(l => l.StartsWith($"{t} ", StringComparison.Ordinal));

    [Fact]
    public void Only_an_admin_first_argument_selects_the_cli()
    {
        Assert.True(AdminCli.IsAdmin(["admin", "users"]));
        Assert.False(AdminCli.IsAdmin([]));
        Assert.False(AdminCli.IsAdmin(["--urls", "http://+:8080"]));
    }

    [Fact]
    public async Task Users_lists_every_account_with_its_status_and_allowlist_state()
    {
        var (conn, t, email) = await TenantAsync();
        await using var _ = conn;
        await TestAuth.DisallowAgentAsync(conn, t);

        var (code, output, _) = await RunAsync("users");

        Assert.Equal(0, code);
        Assert.StartsWith("id", output);
        var cols = Line(output, t).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal([t.ToString(), email, "active", "-"], cols[..4]);
    }

    [Fact]
    public async Task Allow_and_disallow_edit_the_allowlist_by_id_or_email()
    {
        var (conn, t, email) = await TenantAsync();
        await using var _ = conn;
        await TestAuth.DisallowAgentAsync(conn, t);

        Assert.Equal(0, (await RunAsync("allow", email.ToUpperInvariant(), "the", "operator")).Code);
        Assert.Equal("the operator", await conn.ExecuteScalarAsync<string>(
            "SELECT note FROM agent_allowlist WHERE tenant_id = @t", new { t }));
        Assert.Contains("already", (await RunAsync("allow", t.ToString())).Out);
        Assert.True(await new AgentSettingsRepo(conn, t).IsAllowedAsync());

        Assert.Equal(0, (await RunAsync("disallow", t.ToString())).Code);
        Assert.False(await new AgentSettingsRepo(conn, t).IsAllowedAsync());
        Assert.Contains("was not", (await RunAsync("disallow", t.ToString())).Out);
    }

    [Fact]
    public async Task Disable_signs_the_account_out_and_enable_lets_it_back_in()
    {
        var (t, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        var sessions = new SessionRepo(conn);
        Assert.NotNull(await sessions.ResolveAsync(sid));

        var (code, output, _) = await RunAsync("disable", t.ToString());
        Assert.Equal(0, code);
        Assert.Contains("disabled", output);
        Assert.Equal("disabled", await conn.ExecuteScalarAsync<string>("SELECT status FROM users WHERE id = @t", new { t }));
        Assert.Null(await sessions.ResolveAsync(sid));

        Assert.Equal(0, (await RunAsync("enable", t.ToString())).Code);
        Assert.NotNull(await sessions.ResolveAsync(sid));
    }

    [Fact]
    public async Task Usage_counts_each_tenants_rows_and_evidence_bytes_and_shows_its_last_sign_in_and_poll()
    {
        var (conn, t, email) = await TenantAsync();
        await using var _ = conn;
        var other = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        foreach (var (tenant, name) in new[] { (t, "a.md"), (t, "b.md"), (other, "c.md") })
            await conn.ExecuteAsync(
                "INSERT INTO applications (tenant_id, name, company, role) VALUES (@tenant, @name, 'Acme', 'Eng')",
                new { tenant, name });
        await conn.ExecuteAsync(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, screenshot) VALUES (@t, 'a.md', 'dry_run', '\\x0102030405'::bytea), (@t, 'a.md', 'failed', NULL)",
            new { t });
        await conn.ExecuteAsync(
            "INSERT INTO sessions (id, user_id, expires_at, created_at, last_seen_at) "
            + "VALUES (@id, @t, now() + interval '1 day', '2026-01-02T03:04:00Z', '2026-01-05T06:07:00Z')",
            new { id = Guid.NewGuid().ToString("N"), t });
        await conn.ExecuteAsync("UPDATE users SET last_polled_at = '2026-01-06T00:00:00Z' WHERE id = @t", new { t });

        var (code, output, _) = await RunAsync("usage", email);

        Assert.Equal(0, code);
        var lines = output.TrimEnd().Split('\n');
        Assert.Equal(2, lines.Length); // the header and this one account only
        Assert.Equal(
            [t.ToString(), email, "active", "2", "0", "2", "5", "1", "2026-01-02", "03:04Z", "2026-01-05", "06:07Z", "2026-01-06", "00:00Z"],
            lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries));

        // With no account named, every account is listed; one never polled shows "-".
        var all = (await RunAsync("usage")).Out;
        Assert.EndsWith("-", Line(all, other).TrimEnd());
    }

    [Fact]
    public async Task An_unknown_account_or_command_fails_without_touching_anything()
    {
        var missing = await RunAsync("disable", "nobody-at-all@example.invalid");
        Assert.Equal(1, missing.Code);
        Assert.Contains("no account", missing.Err);

        Assert.Equal(2, (await RunAsync("frobnicate")).Code);
        Assert.Equal(2, (await RunAsync("disable")).Code);
        Assert.Equal(2, (await RunAsync()).Code);
        Assert.Equal(0, (await RunAsync("help")).Code);
        Assert.Equal(1, (await RunWithAsync("", "users")).Code);
    }

    [Fact]
    public async Task The_poller_role_can_stamp_its_last_poll_but_neither_worker_role_can_run_the_cli()
    {
        Migrator.Upgrade(pg.ConnectionString, rolePasswords: new Dictionary<string, string?>
        {
            [Migrator.AgentRole] = "agent-pw",
            [Migrator.PollerRole] = "poller-pw",
        });
        var (conn, t, _) = await TenantAsync();
        await using var _ = conn;
        var poller = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = Migrator.PollerRole, Password = "poller-pw", Pooling = false,
        }.ConnectionString;

        // What db.py's mark_polled runs.
        await using (var p = new NpgsqlConnection(poller))
        {
            await p.OpenAsync();
            await p.ExecuteAsync("UPDATE users SET last_polled_at = now() WHERE id = @t", new { t });
        }
        Assert.NotNull(await conn.ExecuteScalarAsync<DateTime?>("SELECT last_polled_at FROM users WHERE id = @t", new { t }));

        var (code, _, err) = await RunWithAsync(poller, "disable", t.ToString());
        Assert.Equal(1, code);
        Assert.Contains("owner", err);
        Assert.Equal("active", await conn.ExecuteScalarAsync<string>("SELECT status FROM users WHERE id = @t", new { t }));

        // The agent's role can SELECT every table, so even a read is refused up front.
        var agent = new NpgsqlConnectionStringBuilder(pg.ConnectionString)
        {
            Username = Migrator.AgentRole, Password = "agent-pw", Pooling = false,
        }.ConnectionString;
        var listed = await RunWithAsync(agent, "users");
        Assert.Equal(1, listed.Code);
        Assert.Equal("", listed.Out);
        Assert.Contains("applytrack_agent is not the database owner", listed.Err);
    }
}
