// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Boots the whole API in-memory against the test Postgres and asserts the wire
/// contract the verbatim SPA depends on: exact URLs, snake_case JSON keys, the
/// <c>?expected_version=</c> 409 flow, FastAPI-style <c>{"detail"}</c> errors, and
/// the v1 501s. Runs on the bootstrap tenant, wiped before each test.
/// </summary>
[Collection(PostgresCollection.Name)]
public class EndpointContractTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public EndpointContractTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(_pg.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                """
                DELETE FROM applications WHERE tenant_id = @t;
                DELETE FROM blacklist WHERE tenant_id = @t;
                DELETE FROM search_profiles WHERE tenant_id = @t;
                """,
                new { t = Tenant.BootstrapId });
        }

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        _client = _factory.CreateClient();
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

    [Theory]
    [InlineData("POST", "/api/poll")]
    [InlineData("GET", "/api/apps/whatever.md/check-link")]
    [InlineData("POST", "/api/apps/whatever.md/draft")]
    public async Task Out_of_v1_endpoints_return_501(string method, string url)
    {
        var req = new HttpRequestMessage(new HttpMethod(method), url);
        var res = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.NotImplemented, res.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJson(res)).GetProperty("detail").GetString()));
    }
}
