// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The account self-service routes over HTTP: <c>GET /api/account/export</c> produces a
/// single JSON migration snapshot, <c>POST /api/account/import</c> loads one back
/// (overwrite-by-slug, atomic), and <c>DELETE /api/account</c> rides the
/// <c>ON DELETE CASCADE</c> FKs to wipe every per-tenant table in one statement. Each
/// test runs as a fresh seeded tenant (its own user + session cookie) on the shared
/// container, so one tenant's data never leaks into another.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private long _tenantId;

    public AccountEndpointTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));

        var (tenantId, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _tenantId = tenantId;
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

    [Fact]
    public async Task Export_returns_one_json_document_of_apps_criteria_and_blacklist()
    {
        await _client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","notes":"hello"}"""));
        await _client.PostAsync("/api/blacklist", Json("""{"company":"Evil Corp"}"""));

        var res = await _client.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);

        var doc = await ReadJsonAsync(res);
        Assert.Equal("applytrack-export", doc.GetProperty("format").GetString());
        Assert.Equal(1, doc.GetProperty("version").GetInt32());

        // Each application is a flat record carrying its slug name + structured fields.
        var apps = doc.GetProperty("applications");
        Assert.Equal(1, apps.GetArrayLength());
        Assert.Equal("acme-corp-engineer.md", apps[0].GetProperty("name").GetString());
        Assert.Equal("Acme Corp", apps[0].GetProperty("company").GetString());
        Assert.Equal("hello", apps[0].GetProperty("notes").GetString());

        // The settings travel too: criteria object + the normalized blacklist key.
        Assert.True(doc.GetProperty("criteria").TryGetProperty("min_fit_score", out _));
        Assert.Equal("evil-corp", doc.GetProperty("blacklist")[0].GetString());
    }

    [Fact]
    public async Task Export_of_an_empty_account_is_valid_json_with_no_applications()
    {
        var doc = await ReadJsonAsync(await _client.GetAsync("/api/account/export"));
        Assert.Empty(doc.GetProperty("applications").EnumerateArray());
        // Criteria is always present (defaults when nothing is saved).
        Assert.True(doc.GetProperty("criteria").TryGetProperty("keywords", out _));
    }

    [Fact]
    public async Task Import_creates_applications_criteria_and_blacklist()
    {
        var body = """
        {
          "format": "applytrack-export", "version": 1,
          "applications": [
            { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer",
              "status": "applied", "link": "https://acme.example/job", "notes": "imported" }
          ],
          "criteria": { "keywords": ["rust", "go"], "min_fit_score": 70 },
          "blacklist": ["Evil Corp"]
        }
        """;
        var res = await _client.PostAsync("/api/account/import", Json(body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = await ReadJsonAsync(res);
        Assert.Equal(1, summary.GetProperty("imported_applications").GetInt32());
        Assert.Equal(1, summary.GetProperty("imported_blacklist").GetInt32());
        Assert.True(summary.GetProperty("criteria_applied").GetBoolean());

        // The app reads back through the normal API (fields nested under "fields").
        var app = await ReadJsonAsync(await _client.GetAsync("/api/apps/acme-corp-engineer.md"));
        Assert.Equal("applied", app.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("imported", app.GetProperty("fields").GetProperty("notes").GetString());

        var criteria = await ReadJsonAsync(await _client.GetAsync("/api/criteria"));
        Assert.Equal(70, criteria.GetProperty("min_fit_score").GetInt32());
        Assert.Contains("rust",
            criteria.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));

        var blacklist = await ReadJsonAsync(await _client.GetAsync("/api/blacklist"));
        Assert.Equal("evil-corp", blacklist[0].GetString());
    }

    [Fact]
    public async Task Import_overwrites_an_existing_application_by_slug()
    {
        await _client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","status":"lead","notes":"original"}"""));

        // Same slug, new status + notes -> overwrite, not a second row.
        var body = """
        { "applications": [
            { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer",
              "status": "offer", "notes": "overwritten" } ] }
        """;
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PostAsync("/api/account/import", Json(body))).StatusCode);

        var apps = await ReadJsonAsync(await _client.GetAsync("/api/apps"));
        Assert.Equal(1, apps.GetArrayLength());
        var app = await ReadJsonAsync(await _client.GetAsync("/api/apps/acme-corp-engineer.md"));
        Assert.Equal("offer", app.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("overwritten", app.GetProperty("fields").GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Export_then_import_into_another_tenant_round_trips_the_data()
    {
        // Tenant A (this _client) creates data and exports it.
        await _client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","status":"applied"}"""));
        await _client.PutAsync("/api/criteria", Json("""{"keywords":["rust"]}"""));
        await _client.PostAsync("/api/blacklist", Json("""{"company":"Evil Corp"}"""));
        var exported = await (await _client.GetAsync("/api/account/export"))
            .Content.ReadAsStringAsync();

        // Tenant B: a fresh user + session on the same container imports A's bytes.
        var (_, sidB) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sidB}");

        Assert.Equal(HttpStatusCode.OK,
            (await clientB.PostAsync("/api/account/import", Json(exported))).StatusCode);

        // B now sees A's data; isolation holds through the import path.
        var appsB = await ReadJsonAsync(await clientB.GetAsync("/api/apps"));
        Assert.Equal(1, appsB.GetArrayLength());
        var critB = await ReadJsonAsync(await clientB.GetAsync("/api/criteria"));
        Assert.Contains("rust",
            critB.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()));
        var blB = await ReadJsonAsync(await clientB.GetAsync("/api/blacklist"));
        Assert.Equal("evil-corp", blB[0].GetString());
    }

    [Fact]
    public async Task Import_rejects_a_file_with_no_importable_data()
    {
        var res = await _client.PostAsync("/api/account/import", Json("{}"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var detail = (await ReadJsonAsync(res)).GetProperty("detail").GetString();
        Assert.Contains("no importable data", detail);
    }

    [Fact]
    public async Task Delete_account_cascades_every_per_tenant_table_to_zero_rows()
    {
        // Seed a row in every cascading table: the API writes most; seen + magic_tokens
        // are poller/auth-owned, so insert those directly to prove the FK reaches them.
        await _client.PostAsync("/api/apps", Json("""{"company":"Acme Corp","role":"Engineer"}"""));
        await _client.PutAsync("/api/criteria", Json("""{"keywords":["rust"]}"""));
        await _client.PostAsync("/api/blacklist", Json("""{"company":"Evil Corp"}"""));
        await _client.PostAsync("/api/poll", Json("{}"));
        await using (var seed = new NpgsqlConnection(_pg.ConnectionString))
        {
            await seed.OpenAsync();
            await seed.ExecuteAsync(
                "INSERT INTO seen (tenant_id, kind, key) VALUES (@t, 'url', 'https://x/1')",
                new { t = _tenantId });
            await seed.ExecuteAsync(
                "INSERT INTO magic_tokens (user_id, token_sha256, expires_at) "
                + "VALUES (@t, 'deadbeef', now() + interval '1 hour')",
                new { t = _tenantId });
        }

        var del = await _client.DeleteAsync("/api/account");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        foreach (var (table, col) in new[]
                 {
                     ("applications", "tenant_id"), ("search_profiles", "tenant_id"),
                     ("blacklist", "tenant_id"), ("seen", "tenant_id"),
                     ("poll_requests", "tenant_id"), ("sessions", "user_id"),
                     ("magic_tokens", "user_id"), ("users", "id"),
                 })
        {
            var count = await conn.ExecuteScalarAsync<int>(
                $"SELECT count(*) FROM {table} WHERE {col} = @t", new { t = _tenantId });
            Assert.Equal(0, count);
        }

        // The cascaded session is gone, so the cookie now 401s — the account is unusable.
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/apps")).StatusCode);
    }
}
