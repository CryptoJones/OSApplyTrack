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
/// Boots the whole API in-memory against the test Postgres and asserts the wire
/// contract the verbatim SPA depends on: exact URLs, snake_case JSON keys, the
/// <c>?expected_version=</c> 409 flow, FastAPI-style <c>{"detail"}</c> errors, and
/// the v1 501s. Each test runs as a fresh tenant (its own seeded user + session
/// cookie), so the tenant starts empty without a wipe and tests stay isolated on
/// the shared container.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EndpointContractTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private long _tenantId;

    public EndpointContractTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));

        // The contract tests exercise the app routes, not the login flow, so seed an
        // authenticated session straight into the DB and ride it via the cookie. A
        // unique email => a brand-new tenant => an empty slate every test.
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

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> CreateAcme()
    {
        var res = await _client.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","notes":"hello"}"""));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("filename").GetString()!;
    }

    [Fact]
    public async Task Apps_list_starts_empty()
    {
        var body = await ReadJson(await _client.GetAsync("/api/apps"));
        Assert.Equal(JsonValueKind.Array, body.ValueKind);
        Assert.Equal(0, body.GetArrayLength());
    }

    [Fact]
    public async Task Create_returns_201_and_detail_has_contract_shape()
    {
        var name = await CreateAcme();
        Assert.Equal("acme-corp-engineer.md", name);

        var detail = await ReadJson(await _client.GetAsync($"/api/apps/{name}"));
        Assert.Equal(name, detail.GetProperty("filename").GetString());
        Assert.Equal("", detail.GetProperty("material").GetString());
        Assert.Equal(JsonValueKind.String, detail.GetProperty("version").ValueKind);
        Assert.Contains("Acme Corp", detail.GetProperty("raw").GetString());

        var fields = detail.GetProperty("fields");
        Assert.Equal("Acme Corp", fields.GetProperty("company").GetString());
        // snake_case key survives the round-trip (ContactEmail <-> contact_email).
        Assert.True(fields.TryGetProperty("contact_email", out _));
        Assert.Equal("hello", fields.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task List_summary_has_the_keys_the_sidebar_reads()
    {
        await CreateAcme();
        var list = await ReadJson(await _client.GetAsync("/api/apps"));
        Assert.Equal(1, list.GetArrayLength());
        var row = list[0];
        foreach (var key in new[] { "filename", "company", "role", "lane", "status",
                     "contact", "contact_email", "applied", "followup", "score", "link", "snippet" })
            Assert.True(row.TryGetProperty(key, out _), $"missing key: {key}");
    }

    [Fact]
    public async Task Stats_has_status_and_lane_maps()
    {
        await CreateAcme();
        var stats = await ReadJson(await _client.GetAsync("/api/stats"));
        Assert.Equal(1, stats.GetProperty("status").GetProperty("lead").GetInt32());
        Assert.Equal(1, stats.GetProperty("lane").GetProperty("ai").GetInt32());
    }

    [Fact]
    public async Task Stale_version_conflicts_then_unversioned_retry_succeeds()
    {
        var name = await CreateAcme();
        var put = Json("""{"company":"Acme Corp","role":"Engineer","status":"applied"}""");

        var conflict = await _client.PutAsync($"/api/apps/{name}?expected_version=999", put);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJson(conflict)).GetProperty("detail").GetString()));

        // The SPA's overwrite-confirm path retries with no version => unconditional.
        var retry = await _client.PutAsync($"/api/apps/{name}",
            Json("""{"company":"Acme Corp","role":"Engineer","status":"applied"}"""));
        Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        Assert.Equal(name, (await ReadJson(retry)).GetProperty("filename").GetString());
    }

    [Fact]
    public async Task Delete_returns_204_then_get_is_404_with_detail()
    {
        var name = await CreateAcme();
        var del = await _client.DeleteAsync($"/api/apps/{name}");
        Assert.Equal(HttpStatusCode.NoContent, del.StatusCode);

        var missing = await _client.GetAsync($"/api/apps/{name}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJson(missing)).GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Blacklist_add_list_remove_flow()
    {
        var add = await _client.PostAsync("/api/blacklist", Json("""{"company":"Evil Corp"}"""));
        var addBody = await ReadJson(add);
        Assert.Equal("Evil Corp", addBody.GetProperty("company").GetString());
        Assert.True(addBody.GetProperty("added").GetBoolean());
        Assert.Equal(0, addBody.GetProperty("passed").GetInt32());

        var list = await ReadJson(await _client.GetAsync("/api/blacklist"));
        Assert.Equal("evil-corp", list[0].GetString());

        var del = await ReadJson(await _client.DeleteAsync("/api/blacklist/evil-corp"));
        Assert.True(del.GetProperty("removed").GetBoolean());
    }

    [Fact]
    public async Task Blacklist_requires_a_company()
    {
        var res = await _client.PostAsync("/api/blacklist", Json("""{"company":"   "}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Criteria_get_defaults_then_put_normalizes_and_round_trips()
    {
        var defaults = await ReadJson(await _client.GetAsync("/api/criteria"));
        Assert.Equal(55, defaults.GetProperty("min_fit_score").GetInt32());
        Assert.True(defaults.GetProperty("sources").TryGetProperty("remotive", out _));
        Assert.Equal(JsonValueKind.Array, defaults.GetProperty("ats_boards").ValueKind);

        var put = await ReadJson(await _client.PutAsync("/api/criteria",
            Json("""{"keywords":["rust"],"min_fit_score":150,"default_lane":"dotnet"}""")));
        Assert.Equal(100, put.GetProperty("min_fit_score").GetInt32()); // clamped
        Assert.Equal("rust", put.GetProperty("keywords")[0].GetString());
        Assert.Equal("dotnet", put.GetProperty("default_lane").GetString());
    }

    [Fact]
    public async Task Poll_enqueues_a_request_and_answers_count_zero()
    {
        var res = await _client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        // The SPA reads r.count; the decoupled worker stages leads out of band, so
        // the immediate answer is always zero (its live refresh surfaces them later).
        Assert.Equal(0, (await ReadJson(res)).GetProperty("count").GetInt32());

        // The button's effect is a queued row the Python worker will drain.
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        var queued = await conn.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM poll_requests WHERE tenant_id = @t", new { t = _tenantId });
        Assert.Equal(1, queued);
    }

    [Fact]
    public async Task Poll_rate_limit_uses_forwarded_for_client_ip()
    {
        _client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.10");

        for (var i = 0; i < 15; i++)
        {
            var res = await _client.PostAsync("/api/poll", Json("{}"));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var rejected = await _client.PostAsync("/api/poll", Json("{}"));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Check_link_is_out_of_v1_and_returns_501()
    {
        // Drafting is now implemented (see MaterialsEndpointTests); check-link is the
        // last remaining v1 stub.
        var res = await _client.GetAsync("/api/apps/whatever.md/check-link");
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJson(res)).GetProperty("detail").GetString()));
    }
}
