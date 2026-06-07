// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The Step 5 account self-service routes over HTTP: <c>GET /api/account/export</c>
/// produces a real zip of the tenant's data, and <c>DELETE /api/account</c> rides the
/// <c>ON DELETE CASCADE</c> FKs to wipe every per-tenant table in one statement. Each
/// test runs as a fresh seeded tenant (its own user + session cookie) on the shared
/// container, so deleting one never touches another.
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

    [Fact]
    public async Task Export_returns_a_zip_of_markdown_plus_a_settings_json()
    {
        await _client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","notes":"hello"}"""));
        await _client.PostAsync("/api/blacklist", Json("""{"company":"Evil Corp"}"""));

        var res = await _client.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);

        var bytes = await res.Content.ReadAsByteArrayAsync();
        using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);

        // One Markdown file per application, under applications/, rendered via the codec.
        var md = zip.GetEntry("applications/acme-corp-engineer.md");
        Assert.NotNull(md);
        using (var r = new StreamReader(md!.Open()))
            Assert.Contains("Acme Corp", await r.ReadToEndAsync());

        // settings.json carries the criteria + the normalized blacklist key.
        var settingsEntry = zip.GetEntry("settings.json");
        Assert.NotNull(settingsEntry);
        using var sr = new StreamReader(settingsEntry!.Open());
        var settings = JsonDocument.Parse(await sr.ReadToEndAsync()).RootElement;
        Assert.True(settings.GetProperty("criteria").TryGetProperty("min_fit_score", out _));
        Assert.Equal("evil-corp", settings.GetProperty("blacklist")[0].GetString());
    }

    [Fact]
    public async Task Export_of_an_empty_account_is_still_a_valid_zip_with_settings()
    {
        var res = await _client.GetAsync("/api/account/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        using var zip = new ZipArchive(
            new MemoryStream(await res.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
        Assert.NotNull(zip.GetEntry("settings.json"));
        Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("applications/"));
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
