// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Exercises the Dapper repos against a real Postgres. Every test runs on its own
/// tenant id (so the shared container stays isolated) and asserts the behaviors the
/// SPA contract leans on: tenant scoping, optimistic locking, list ordering, the
/// criteria round-trip, and the blacklist's pass-open-leads side effect.
/// </summary>
[Collection(PostgresCollection.Name)]
public class RepoTests(PostgresFixture pg)
{
    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // tenant_id is a real FK to users(id) (migration 0009), so each test's tenant must
    // be a live user row. A unique email yields a fresh, empty tenant on the shared
    // container — the same isolation the old synthetic-id counter gave, now FK-valid.
    private static Task<long> NewTenantAsync(NpgsqlConnection conn) =>
        TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());

    private static AppFields Fields(string company, string role = "", string status = "lead",
        string lane = "ai", string notes = "") => new()
    {
        Company = company, Role = role, Status = status, Lane = lane, Notes = notes,
    };

    [Fact]
    public async Task Create_then_get_round_trips_fields_and_starts_at_version_1()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);

        var name = await repo.CreateAsync(Fields("Acme Corp", "Engineer", notes: "hello world"));
        Assert.Equal("acme-corp-engineer.md", name);

        var rec = await repo.GetAsync(name);
        Assert.NotNull(rec);
        Assert.Equal("Acme Corp", rec!.Fields.Company);
        Assert.Equal("Engineer", rec.Fields.Role);
        Assert.Equal("hello world", rec.Fields.Notes);
        Assert.Equal(1, rec.Version);
        // created is auto-filled when omitted.
        Assert.NotEqual("", rec.Fields.Created);
    }

    [Fact]
    public async Task Create_requires_a_company()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        await Assert.ThrowsAsync<AppValidationException>(() => repo.CreateAsync(Fields("")));
    }

    [Fact]
    public async Task Create_duplicate_name_is_rejected()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        await repo.CreateAsync(Fields("Dup Co", "Dev"));
        await Assert.ThrowsAsync<AppValidationException>(() => repo.CreateAsync(Fields("Dup Co", "Dev")));
    }

    [Fact]
    public async Task Get_missing_returns_null_and_delete_missing_throws()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        Assert.Null(await repo.GetAsync("nope.md"));
        await Assert.ThrowsAsync<AppNotFoundException>(() => repo.DeleteAsync("nope.md"));
    }

    [Fact]
    public async Task Tenants_are_isolated()
    {
        await using var conn = await OpenAsync();
        long t1 = await NewTenantAsync(conn), t2 = await NewTenantAsync(conn);
        await new ApplicationRepo(conn, t1).CreateAsync(Fields("Only In One", "Dev"));

        var other = new ApplicationRepo(conn, t2);
        Assert.Empty(await other.ListAsync());
        Assert.Null(await other.GetAsync("only-in-one-dev.md"));
    }

    [Fact]
    public async Task List_orders_by_status_pipeline_then_company()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        await repo.CreateAsync(Fields("Zeta", "X", status: "lead"));
        await repo.CreateAsync(Fields("Alpha", "X", status: "offer"));
        await repo.CreateAsync(Fields("Beta", "X", status: "lead"));

        var names = (await repo.ListAsync()).Select(s => s.Company).ToList();
        // lead (Beta, Zeta by company) comes before offer (Alpha) in pipeline order.
        Assert.Equal(new[] { "Beta", "Zeta", "Alpha" }, names);
    }

    [Fact]
    public async Task Stats_counts_by_status_and_lane()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        await repo.CreateAsync(Fields("A", "1", status: "lead", lane: "ai"));
        await repo.CreateAsync(Fields("B", "2", status: "lead", lane: "dotnet"));
        await repo.CreateAsync(Fields("C", "3", status: "applied", lane: "ai"));

        var (byStatus, byLane) = await repo.StatsAsync();
        Assert.Equal(2, byStatus["lead"]);
        Assert.Equal(1, byStatus["applied"]);
        Assert.Equal(2, byLane["ai"]);
        Assert.Equal(1, byLane["dotnet"]);
    }

    [Fact]
    public async Task Update_bumps_version_and_enforces_optimistic_lock()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        var name = await repo.CreateAsync(Fields("Lock Co", "Dev"));

        // Correct expected version succeeds and bumps to 2.
        await repo.UpdateStructuredAsync(name, Fields("Lock Co", "Dev", status: "applied"), "1");
        var afterFirst = await repo.GetAsync(name);
        Assert.Equal(2, afterFirst!.Version);
        Assert.Equal("applied", afterFirst.Fields.Status);

        // Stale expected version (1) now conflicts.
        await Assert.ThrowsAsync<AppConflictException>(() =>
            repo.UpdateStructuredAsync(name, Fields("Lock Co", "Dev", status: "screen"), "1"));

        // No expected version => unconditional overwrite (the SPA's 409 retry path).
        await repo.UpdateStructuredAsync(name, Fields("Lock Co", "Dev", status: "screen"), null);
        Assert.Equal("screen", (await repo.GetAsync(name))!.Fields.Status);
    }

    [Fact]
    public async Task Update_missing_throws_not_found()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        await Assert.ThrowsAsync<AppNotFoundException>(() =>
            repo.UpdateStructuredAsync("ghost.md", Fields("Ghost"), null));
    }

    [Fact]
    public async Task Update_preserves_created_when_payload_omits_it()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        var name = await repo.CreateAsync(new AppFields { Company = "Keep", Role = "Dev", Created = "2020-01-01" });

        await repo.UpdateStructuredAsync(name, Fields("Keep", "Dev", status: "applied"), null);
        Assert.Equal("2020-01-01", (await repo.GetAsync(name))!.Fields.Created);
    }

    [Fact]
    public async Task Raw_update_round_trips_through_the_markdown_codec()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);
        var name = await repo.CreateAsync(Fields("Raw Co", "Dev"));

        var rec = await repo.GetAsync(name);
        var edited = MarkdownCodec.Render(rec!.Fields with { Status = "offer", Notes = "raw notes" });
        await repo.UpdateRawAsync(name, edited, null);

        var after = await repo.GetAsync(name);
        Assert.Equal("offer", after!.Fields.Status);
        Assert.Equal("raw notes", after.Fields.Notes);
    }

    [Fact]
    public async Task UpsertByName_inserts_then_overwrites_bumping_version()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new ApplicationRepo(conn, t);

        // First upsert inserts at version 1, preserving the supplied slug verbatim.
        await repo.UpsertByNameAsync("acme-corp-engineer.md",
            Fields("Acme Corp", "Engineer", status: "lead", notes: "first"));
        var first = await repo.GetAsync("acme-corp-engineer.md");
        Assert.NotNull(first);
        Assert.Equal("lead", first!.Fields.Status);
        Assert.Equal(1, first.Version);

        // Second upsert on the same slug overwrites the fields and bumps the version —
        // no duplicate row. This is the importer's overwrite-by-slug contract.
        await repo.UpsertByNameAsync("acme-corp-engineer.md",
            Fields("Acme Corp", "Engineer", status: "applied", notes: "second"));
        var second = await repo.GetAsync("acme-corp-engineer.md");
        Assert.Equal("applied", second!.Fields.Status);
        Assert.Equal("second", second.Fields.Notes);
        Assert.Equal(2, second.Version);
        Assert.Single(await repo.ListAsync());
    }

    [Fact]
    public async Task Criteria_defaults_when_absent_then_round_trips_after_upsert()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new CriteriaRepo(conn, t);

        var defaults = await repo.GetAsync();
        Assert.Equal(55, defaults.MinFitScore);
        Assert.True(defaults.Sources["remotive"]);
        Assert.False(defaults.Sources["arbeitnow"]);

        var edited = new Criteria
        {
            Keywords = ["rust", "go"],
            DefaultLane = "dotnet",
            MinFitScore = 70,
            RemoteOnly = true,
            ExcludeLocations = ["India"],
            Sources = Criteria.DefaultSources(),
            AtsBoards = [new AtsBoard("greenhouse", "stripe")],
        };
        await repo.UpsertAsync(edited);

        var loaded = await repo.GetAsync();
        Assert.Equal(new[] { "rust", "go" }, loaded.Keywords);
        Assert.Equal("dotnet", loaded.DefaultLane);
        Assert.Equal(70, loaded.MinFitScore);
        Assert.True(loaded.RemoteOnly);
        Assert.Equal(new[] { "India" }, loaded.ExcludeLocations);
        Assert.Single(loaded.AtsBoards);
        Assert.Equal("stripe", loaded.AtsBoards[0].Slug);
    }

    [Fact]
    public async Task Criteria_empty_keywords_fall_back_to_defaults_on_read()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var repo = new CriteriaRepo(conn, t);
        await repo.UpsertAsync(new Criteria { Keywords = [], Sources = Criteria.DefaultSources() });
        Assert.NotEmpty((await repo.GetAsync()).Keywords);
    }

    [Fact]
    public async Task Blacklist_add_is_normalized_deduped_and_sorted()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var bl = new BlacklistRepo(conn, t);

        Assert.True(await bl.AddAsync("Foo Bar, Inc."));
        Assert.False(await bl.AddAsync("foo  bar   inc")); // same normalized key
        Assert.True(await bl.AddAsync("Acme"));

        Assert.Equal(new[] { "acme", "foo-bar-inc" }, await bl.ListAsync());
        Assert.True(await bl.RemoveAsync("ACME"));
        Assert.Equal(new[] { "foo-bar-inc" }, await bl.ListAsync());
    }

    [Fact]
    public async Task Blacklist_passes_only_open_leads_for_the_matching_company()
    {
        await using var conn = await OpenAsync();
        var t = await NewTenantAsync(conn);
        var apps = new ApplicationRepo(conn, t);
        await apps.CreateAsync(Fields("Evil Corp", "A", status: "lead"));
        await apps.CreateAsync(Fields("Evil Corp", "B", status: "ready"));
        await apps.CreateAsync(Fields("Evil Corp", "C", status: "applied")); // already acted on
        await apps.CreateAsync(Fields("Good Corp", "D", status: "lead"));     // different company

        var bl = new BlacklistRepo(conn, t);
        var passed = await bl.PassOpenLeadsAsync("evil corp");
        Assert.Equal(2, passed);

        var byStatus = (await apps.StatsAsync()).Status;
        Assert.Equal(2, byStatus["passed"]);   // A + B flipped
        Assert.Equal(1, byStatus["applied"]);  // C untouched
        Assert.Equal(1, byStatus["lead"]);     // Good Corp untouched
    }
}
