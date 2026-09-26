// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The daily retention sweep (#350): it trims what only grows, and never what the Errors
/// view, the retry loop, the agent's skip ledger or a submission's proof still reads.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RetentionTests(PostgresFixture pg)
{
    private static readonly RetentionOptions Defaults = new();

    private async Task<(NpgsqlConnection Conn, long T)> TenantAsync()
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        return (conn, await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail()));
    }

    private static Task AppAsync(NpgsqlConnection conn, long t, string name, string status) =>
        conn.ExecuteAsync(
            "INSERT INTO applications (tenant_id, name, company, role, status) VALUES (@t, @name, 'Acme', 'Eng', @status)",
            new { t, name, status });

    private static Task<long> EvidenceAsync(NpgsqlConnection conn, long t, string app, string kind, int daysAgo) =>
        conn.ExecuteScalarAsync<long>(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, screenshot, created_at) "
            + "VALUES (@t, @app, @kind, '\\x89504e47'::bytea, now() - make_interval(days => @daysAgo)) RETURNING id",
            new { t, app, kind, daysAgo });

    private static Task<bool> HasShotAsync(NpgsqlConnection conn, long id) =>
        conn.ExecuteScalarAsync<bool>("SELECT screenshot IS NOT NULL FROM agent_evidence WHERE id = @id", new { id });

    [Fact]
    public async Task Old_screenshots_lose_their_bytes_but_the_evidence_row_stays()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        await AppAsync(conn, t, "passed.md", "passed");
        // Five old failures, newest first: the newest three keep their screenshots.
        var ids = new List<long>();
        for (var d = 100; d <= 104; d++) ids.Add(await EvidenceAsync(conn, t, "passed.md", "failed", d));
        var recent = await EvidenceAsync(conn, t, "passed.md", "dry_run", 10);

        var r = await Retention.SweepTenantAsync(conn, t, Defaults);

        // The recent dry run plus the two newest old failures fill the three kept.
        Assert.Equal(3, r.Screenshots);
        Assert.True(await HasShotAsync(conn, recent));
        Assert.True(await HasShotAsync(conn, ids[0]));
        Assert.True(await HasShotAsync(conn, ids[1]));
        Assert.False(await HasShotAsync(conn, ids[2]));
        Assert.False(await HasShotAsync(conn, ids[4]));
        Assert.Equal(6, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM agent_evidence WHERE tenant_id = @t", new { t }));
        // Idempotent.
        Assert.Equal(0, (await Retention.SweepTenantAsync(conn, t, Defaults)).Total);
    }

    [Fact]
    public async Task Ready_and_errored_applications_and_submissions_keep_every_screenshot()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        await AppAsync(conn, t, "stuck.md", "ready");
        await AppAsync(conn, t, "applied.md", "applied");
        var stuck = new List<long>();
        for (var d = 200; d < 206; d++) stuck.Add(await EvidenceAsync(conn, t, "stuck.md", "failed", d));
        var proof = await EvidenceAsync(conn, t, "applied.md", "submitted", 400);
        for (var d = 1; d <= 4; d++) await EvidenceAsync(conn, t, "applied.md", "dry_run", d);

        var r = await Retention.SweepTenantAsync(conn, t, Defaults);

        Assert.Equal(0, r.Screenshots);
        foreach (var id in stuck) Assert.True(await HasShotAsync(conn, id));
        Assert.True(await HasShotAsync(conn, proof));
    }

    [Fact]
    public async Task Expired_sessions_and_sign_in_tokens_go_live_ones_stay()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        await conn.ExecuteAsync(
            """
            INSERT INTO sessions (id, user_id, expires_at) VALUES
                (md5(random()::text), @t, now() - interval '3 days'),
                (md5(random()::text), @t, now() - interval '1 hour'),
                (md5(random()::text), @t, now() + interval '1 day');
            INSERT INTO magic_tokens (user_id, token_sha256, expires_at, used_at) VALUES
                (@t, decode(md5(random()::text), 'hex'), now() - interval '3 days', now() - interval '3 days'),
                (@t, decode(md5(random()::text), 'hex'), now() - interval '3 days', NULL),
                (@t, decode(md5(random()::text), 'hex'), now() + interval '10 minutes', NULL);
            """,
            new { t });

        var r = await Retention.SweepTenantAsync(conn, t, Defaults);

        // A day's grace: only the rows expired for longer go.
        Assert.Equal(1, r.Sessions);
        Assert.Equal(2, r.MagicTokens);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM sessions WHERE user_id = @t", new { t }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*)::int FROM magic_tokens WHERE user_id = @t", new { t }));
    }

    [Fact]
    public async Task Old_audit_events_go_except_the_ones_the_agent_reads_back()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        await AppAsync(conn, t, "lead.md", "lead");
        await AppAsync(conn, t, "ready.md", "ready");
        await AppAsync(conn, t, "passed.md", "passed");
        async Task<long> Event(string app, string kind, int daysAgo) =>
            await conn.ExecuteScalarAsync<long>(
                "INSERT INTO agent_events (tenant_id, application_name, kind, created_at) "
                + "VALUES (@t, @app, @kind, now() - make_interval(days => @daysAgo)) RETURNING id",
                new { t, app, kind, daysAgo });
        var gone = new[]
        {
            await Event("passed.md", "error", 400), await Event("", "portal_session", 400),
            await Event("passed.md", "requeued", 200),
        };
        var kept = new[]
        {
            await Event("passed.md", "verdict", 400), await Event("passed.md", "packet", 400),
            await Event("", "account_created", 400), await Event("ready.md", "error", 400),
            await Event("lead.md", "error", 400), await Event("passed.md", "error", 30),
        };

        var r = await Retention.SweepTenantAsync(conn, t, Defaults);

        Assert.Equal(gone.Length, r.AgentEvents);
        var left = (await conn.QueryAsync<long>("SELECT id FROM agent_events WHERE tenant_id = @t", new { t })).ToHashSet();
        Assert.All(kept, id => Assert.Contains(id, left));
        Assert.All(gone, id => Assert.DoesNotContain(id, left));
    }

    [Fact]
    public async Task The_seen_ledger_is_kept_unless_a_horizon_is_set()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        await conn.ExecuteAsync(
            "INSERT INTO seen (tenant_id, kind, key, created_at) VALUES "
            + "(@t, 'url', 'old', now() - interval '400 days'), (@t, 'url', 'new', now())",
            new { t });

        Assert.Equal(0, (await Retention.SweepTenantAsync(conn, t, Defaults)).Seen);
        var r = await Retention.SweepTenantAsync(conn, t, new RetentionOptions { SeenDays = 180 });

        Assert.Equal(1, r.Seen);
        Assert.Equal(new[] { "new" }, await conn.QueryAsync<string>("SELECT key FROM seen WHERE tenant_id = @t", new { t }));
    }

    [Fact]
    public async Task A_zero_day_window_switches_that_step_off_and_other_tenants_are_untouched()
    {
        var (conn, t) = await TenantAsync();
        await using var _ = conn;
        var other = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        await AppAsync(conn, t, "passed.md", "passed");
        await AppAsync(conn, other, "passed.md", "passed");
        for (var d = 100; d < 105; d++)
        {
            await EvidenceAsync(conn, t, "passed.md", "failed", d);
            await EvidenceAsync(conn, other, "passed.md", "failed", d);
        }

        Assert.Equal(0, (await Retention.SweepTenantAsync(conn, t,
            new RetentionOptions { ScreenshotDays = 0, AgentEventDays = 0 })).Screenshots);
        Assert.Equal(2, (await Retention.SweepTenantAsync(conn, t, Defaults)).Screenshots);
        Assert.Equal(5, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*)::int FROM agent_evidence WHERE tenant_id = @other AND screenshot IS NOT NULL",
            new { other }));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("wait", false)]
    public async Task Only_the_migrating_container_runs_the_sweep(string? mode, bool runs)
    {
        await using var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            if (mode is not null) b.UseSetting("Migrations:Mode", mode);
        });
        var hosted = f.Services.GetServices<IHostedService>();
        Assert.Equal(runs, hosted.OfType<RetentionWorker>().Any());
    }
}
