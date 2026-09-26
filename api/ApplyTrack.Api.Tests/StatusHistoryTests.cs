// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Status history (#353): the applications triggers record every status an application
/// enters, whoever writes it; <c>GET /api/apps/{name}/history</c> reads the timeline,
/// <c>GET /api/analytics</c> turns it into a funnel, and the account export carries it
/// and the import restores it. Each test is a fresh tenant on the shared container.
/// </summary>
[Collection(PostgresCollection.Name)]
public class StatusHistoryTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private long _t;

    public StatusHistoryTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        var (t, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _t = t;
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> CreateAsync(string company, string status = "lead", string source = "", string lane = "ai")
    {
        var res = await _client.PostAsync("/api/apps", Json(JsonSerializer.Serialize(
            new { company, role = "Engineer", status, source, lane })));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJsonAsync(res)).GetProperty("filename").GetString()!;
    }

    private async Task SetStatusAsync(string name, string status)
    {
        var detail = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{name}"));
        var fields = JsonSerializer.Deserialize<Dictionary<string, object>>(detail.GetProperty("fields").GetRawText())!;
        fields["status"] = status;
        var res = await _client.PutAsync(
            $"/api/apps/{name}?expected_version={detail.GetProperty("version").GetString()}",
            Json(JsonSerializer.Serialize(fields)));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // Rewrite one application's history to a known, dated sequence (status, days ago).
    private async Task BackdateAsync(string name, params (string Status, double DaysAgo)[] steps)
    {
        await using var conn = await OwnerAsync();
        var id = await conn.ExecuteScalarAsync<long>(
            "SELECT id FROM applications WHERE tenant_id = @t AND name = @name", new { t = _t, name });
        await conn.ExecuteAsync("DELETE FROM status_events WHERE application_id = @id", new { id });
        string? from = null;
        foreach (var (status, daysAgo) in steps)
        {
            await conn.ExecuteAsync(
                "INSERT INTO status_events (tenant_id, application_id, from_status, to_status, at) "
                + "VALUES (@t, @id, @from, @status, now() - make_interval(secs => @secs))",
                new { t = _t, id, from, status, secs = daysAgo * 86400 });
            from = status;
        }
    }

    [Fact]
    public async Task Create_and_each_status_change_are_recorded_in_order_and_other_edits_are_not()
    {
        var name = await CreateAsync("Acme");
        await SetStatusAsync(name, "applied");
        await SetStatusAsync(name, "applied"); // same status: an edit, not a transition
        await SetStatusAsync(name, "screen");

        var res = await _client.GetAsync($"/api/apps/{name}/history");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var history = (await ReadJsonAsync(res)).EnumerateArray().ToList();
        Assert.Equal(3, history.Count);
        Assert.All(history, e => Assert.Equal("status", e.GetProperty("kind").GetString()));
        Assert.Equal(JsonValueKind.Null, history[0].GetProperty("from_status").ValueKind);
        Assert.Equal(["lead", "applied", "screen"], history.Select(e => e.GetProperty("to_status").GetString()));
        Assert.Equal("applied", history[2].GetProperty("from_status").GetString());
        Assert.True(history[0].TryGetProperty("at", out _));
    }

    [Fact]
    public async Task History_of_a_missing_or_another_tenants_application_is_404()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/apps/nope.md/history")).StatusCode);

        await using var conn = await OwnerAsync();
        var other = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var theirs = await new ApplicationRepo(conn, other).CreateAsync(new AppFields { Company = "Theirs" });
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/apps/{theirs}/history")).StatusCode);
    }

    [Fact]
    public async Task The_bulk_status_move_and_a_raw_poller_insert_are_recorded_too()
    {
        await using var conn = await OwnerAsync();
        var repo = new ApplicationRepo(conn, _t);
        var a = await repo.CreateAsync(new AppFields { Company = "Alpha" });
        var b = await repo.CreateAsync(new AppFields { Company = "Beta" });
        await repo.SetStatusAsync([a, b], "ready");
        // What the Python poller runs: a plain INSERT, no repo involved.
        await conn.ExecuteAsync(
            "INSERT INTO applications (tenant_id, name, company, role) VALUES (@t, 'gamma-eng.md', 'Gamma', 'Eng')",
            new { t = _t });

        var history = new StatusEventRepo(conn, _t);
        Assert.Equal(["lead", "ready"], (await history.HistoryAsync(a))!.Select(e => e.ToStatus));
        Assert.Equal(["lead", "ready"], (await history.HistoryAsync(b))!.Select(e => e.ToStatus));
        Assert.Equal(["lead"], (await history.HistoryAsync("gamma-eng.md"))!.Select(e => e.ToStatus));
    }

    [Fact]
    public async Task Deleting_an_application_deletes_its_history()
    {
        var name = await CreateAsync("Acme");
        await SetStatusAsync(name, "applied");
        await _client.DeleteAsync($"/api/apps/{name}");

        await using var conn = await OwnerAsync();
        Assert.Equal(0, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM status_events WHERE tenant_id = @t", new { t = _t }));
    }

    [Fact]
    public async Task Analytics_computes_the_funnel_response_rate_median_and_breakdowns()
    {
        // Applied 10 days ago, screened 2 days later, onsite, offer.
        var offer = await CreateAsync("Offerco", source: "LinkedIn", lane: "dotnet");
        await BackdateAsync(offer, ("lead", 12), ("applied", 10), ("screen", 8), ("onsite", 5), ("offer", 1));
        // Applied, rejected 6 days later.
        var rejected = await CreateAsync("Rejectco", source: "LinkedIn", lane: "dotnet");
        await BackdateAsync(rejected, ("applied", 9), ("rejected", 3));
        // Applied, no answer.
        var ghosted = await CreateAsync("Ghostco", source: "Greenhouse");
        await BackdateAsync(ghosted, ("applied", 7));
        // Never applied: not in the funnel at all.
        await CreateAsync("Leadco");
        // Applied long ago, before the window below.
        var old = await CreateAsync("Oldco", source: "Greenhouse");
        await BackdateAsync(old, ("applied", 400), ("screen", 390));

        var all = await ReadJsonAsync(await _client.GetAsync("/api/analytics"));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("since").ValueKind);
        Assert.Equal(4, all.GetProperty("applied").GetInt32());
        Assert.Equal(3, all.GetProperty("responded").GetInt32());
        Assert.Equal(1, all.GetProperty("rejected").GetInt32());
        Assert.Equal(0.75, all.GetProperty("response_rate").GetDouble());
        // Days to first answer: 2, 6, 10 → median 6.
        Assert.Equal(6.0, all.GetProperty("median_days_to_response").GetDouble());
        Assert.Equal(
            [("applied", 4), ("screen", 2), ("onsite", 1), ("offer", 1)],
            all.GetProperty("funnel").EnumerateArray()
                .Select(s => (s.GetProperty("stage").GetString(), s.GetProperty("count").GetInt32())));

        var since = DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd");
        var recent = await ReadJsonAsync(await _client.GetAsync($"/api/analytics?since={since}"));
        Assert.Equal(3, recent.GetProperty("applied").GetInt32());
        Assert.Equal(2, recent.GetProperty("responded").GetInt32());
        Assert.Equal(4.0, recent.GetProperty("median_days_to_response").GetDouble());

        var bySource = recent.GetProperty("by_source").EnumerateArray().ToList();
        Assert.Equal("LinkedIn", bySource[0].GetProperty("key").GetString());
        Assert.Equal(2, bySource[0].GetProperty("applied").GetInt32());
        Assert.Equal(1.0, bySource[0].GetProperty("response_rate").GetDouble());
        Assert.Equal("Greenhouse", bySource[1].GetProperty("key").GetString());
        Assert.Equal(0.0, bySource[1].GetProperty("response_rate").GetDouble());
        Assert.Equal(JsonValueKind.Null, bySource[1].GetProperty("median_days_to_response").ValueKind);
        var byLane = recent.GetProperty("by_lane").EnumerateArray()
            .ToDictionary(g => g.GetProperty("key").GetString()!, g => g.GetProperty("applied").GetInt32());
        Assert.Equal(2, byLane["dotnet"]);
        Assert.Equal(1, byLane["ai"]);
    }

    [Fact]
    public async Task Analytics_of_an_empty_account_has_no_rates_and_a_bad_since_is_400()
    {
        var empty = await ReadJsonAsync(await _client.GetAsync("/api/analytics"));
        Assert.Equal(0, empty.GetProperty("applied").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("response_rate").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("median_days_to_response").ValueKind);
        Assert.Equal(4, empty.GetProperty("funnel").GetArrayLength());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.GetAsync("/api/analytics?since=yesterday")).StatusCode);
    }

    [Fact]
    public async Task An_application_created_already_answered_counts_as_a_response_but_is_not_timed()
    {
        await CreateAsync("Straightco", status: "screen");
        var a = await ReadJsonAsync(await _client.GetAsync("/api/analytics"));
        Assert.Equal(1, a.GetProperty("applied").GetInt32());
        Assert.Equal(1, a.GetProperty("responded").GetInt32());
        Assert.Equal(JsonValueKind.Null, a.GetProperty("median_days_to_response").ValueKind);
    }

    [Fact]
    public async Task Export_carries_the_history_and_import_restores_it_dated()
    {
        var name = await CreateAsync("Acme");
        await BackdateAsync(name, ("lead", 20), ("applied", 15), ("screen", 11));
        var exported = await (await _client.GetAsync("/api/account/export")).Content.ReadAsStringAsync();
        var events = JsonDocument.Parse(exported).RootElement.GetProperty("status_events").EnumerateArray().ToList();
        Assert.Equal(3, events.Count);
        Assert.All(events, e => Assert.Equal(name, e.GetProperty("application").GetString()));

        var (tB, sidB) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sidB}");
        Assert.Equal(HttpStatusCode.OK, (await clientB.PostAsync("/api/account/import", Json(exported))).StatusCode);

        var history = (await ReadJsonAsync(await clientB.GetAsync($"/api/apps/{name}/history"))).EnumerateArray().ToList();
        Assert.Equal(["lead", "applied", "screen"], history.Select(e => e.GetProperty("to_status").GetString()));
        Assert.True(history[1].GetProperty("at").GetDateTime() < DateTime.UtcNow.AddDays(-14));
        var analytics = await ReadJsonAsync(await clientB.GetAsync("/api/analytics"));
        Assert.Equal(4.0, analytics.GetProperty("median_days_to_response").GetDouble());

        // Re-importing is still idempotent: the history is replaced, not doubled.
        await clientB.PostAsync("/api/account/import", Json(exported));
        await using var conn = await OwnerAsync();
        Assert.Equal(3, await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM status_events WHERE tenant_id = @t", new { t = tB }));
    }

    [Fact]
    public async Task Import_of_a_file_without_history_records_each_app_as_entered_now()
    {
        var res = await _client.PostAsync("/api/account/import", Json(
            """{"format":"applytrack-export","applications":[{"name":"acme-eng.md","company":"Acme","role":"Eng","status":"applied"}]}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var history = (await ReadJsonAsync(await _client.GetAsync("/api/apps/acme-eng.md/history"))).EnumerateArray().ToList();
        Assert.Equal(["applied"], history.Select(e => e.GetProperty("to_status").GetString()));
    }

    [Fact]
    public async Task The_migration_is_rerunnable_and_seeds_history_from_a_valid_applied_date()
    {
        await using var conn = await OwnerAsync();
        var repo = new ApplicationRepo(conn, _t);
        var dated = await repo.CreateAsync(new AppFields { Company = "Dated", Status = "rejected", Applied = "2026-03-04" });
        var bogus = await repo.CreateAsync(new AppFields { Company = "Bogus", Status = "applied", Applied = "2026-02-30" });
        var lead = await repo.CreateAsync(new AppFields { Company = "Leadish", Applied = "2026-03-04" });
        // As if they predated the history: no events yet.
        await conn.ExecuteAsync("DELETE FROM status_events WHERE tenant_id = @t", new { t = _t });

        var assembly = typeof(StatusEventRepo).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("0049_status_events.sql"));
        using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
        var script = await reader.ReadToEndAsync();
        await conn.ExecuteAsync(script);
        await conn.ExecuteAsync(script); // idempotent: nothing seeded twice

        var history = new StatusEventRepo(conn, _t);
        var d = Assert.Single((await history.HistoryAsync(dated))!);
        Assert.Equal(("rejected", new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc)), (d.ToStatus, d.At));
        var createdAt = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT created_at FROM applications WHERE tenant_id = @t AND name = @bogus", new { t = _t, bogus });
        Assert.Equal(createdAt, Assert.Single((await history.HistoryAsync(bogus))!).At);
        Assert.NotEqual(new DateTime(2026, 3, 4, 0, 0, 0, DateTimeKind.Utc), Assert.Single((await history.HistoryAsync(lead))!).At);

        // The rerun kept the triggers working.
        await repo.SetStatusAsync([lead], "applied");
        Assert.Equal(2, (await history.HistoryAsync(lead))!.Count);
    }
}
