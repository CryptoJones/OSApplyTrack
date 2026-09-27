// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Personal API tokens (#356): made, listed and revoked under a session, shown once and
/// stored by hash; sent as <c>Authorization: Bearer</c>, a <c>read</c> token gets the GET
/// routes and a <c>write</c> token the rest, neither reaches the account's credentials, a
/// disabled account's tokens stop, each token has its own rate limit, and
/// <c>last_used_at</c> is written at most once a minute. Each test is a fresh tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ApiTokenTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _session = null!;
    private long _t;

    public ApiTokenTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        (_t, var sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _session = _factory.CreateClient();
        _session.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
    }

    public async Task DisposeAsync()
    {
        _session.Dispose();
        await _factory.DisposeAsync();
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<(long Id, string Token)> MakeAsync(string scope, HttpClient? session = null)
    {
        var res = await (session ?? _session).PostAsync("/api/account/tokens", Json(new { name = $"{scope} script", scope }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var body = await ReadJsonAsync(res);
        return (body.GetProperty("id").GetInt64(), body.GetProperty("token").GetString()!);
    }

    private HttpClient Bearer(string token)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task A_token_is_shown_once_listed_without_its_secret_and_stored_by_hash()
    {
        var made = await _session.PostAsync("/api/account/tokens", Json(new { name = "  CLI  ", scope = "WRITE" }));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var body = await ReadJsonAsync(made);
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith(ApiTokenRepo.Prefix, token);
        Assert.True(token.Length > 40);
        Assert.Equal("CLI", body.GetProperty("name").GetString());
        Assert.Equal("write", body.GetProperty("scope").GetString());

        var listed = await _session.GetStringAsync("/api/account/tokens");
        Assert.Contains("\"CLI\"", listed);
        Assert.DoesNotContain(token, listed);
        Assert.DoesNotContain("\"token\"", listed);
        Assert.DoesNotContain("hash", listed);

        await using var conn = await OwnerAsync();
        var stored = await conn.QuerySingleAsync<byte[]>(
            "SELECT token_hash FROM api_tokens WHERE tenant_id = @t AND scope = 'write'", new { t = _t });
        Assert.Equal(Tokens.Sha256(token), stored);
    }

    [Theory]
    [InlineData("", "read")]
    [InlineData("x", "admin")]
    [InlineData("x", "calendar")]
    [InlineData("x", "")]
    public async Task Making_a_token_needs_a_name_and_a_read_or_write_scope(string name, string scope) =>
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _session.PostAsync("/api/account/tokens", Json(new { name, scope }))).StatusCode);

    [Fact]
    public async Task An_account_holds_a_bounded_number_of_tokens()
    {
        await using var conn = await OwnerAsync();
        for (var i = 0; i < ApiTokenRepo.MaxPersonal; i++)
            await conn.ExecuteAsync(
                "INSERT INTO api_tokens (tenant_id, scope, name, token_hash) VALUES (@t, 'read', 'n', @h)",
                new { t = _t, h = Tokens.Sha256(Guid.NewGuid().ToString()) });
        var res = await _session.PostAsync("/api/account/tokens", Json(new { name = "one too many", scope = "read" }));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task A_read_token_reads_and_cannot_write()
    {
        await _session.PostAsync("/api/apps", Json(new { company = "Acme", role = "Engineer", status = "lead" }));
        var (_, token) = await MakeAsync("read");
        using var client = Bearer(token);

        var apps = await client.GetAsync("/api/apps");
        Assert.Equal(HttpStatusCode.OK, apps.StatusCode);
        Assert.Contains("Acme", await apps.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/account/export")).StatusCode);

        var write = await client.PostAsync("/api/apps", Json(new { company = "Globex", role = "Engineer", status = "lead" }));
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
        Assert.Equal("this API token is read-only", (await ReadJsonAsync(write)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task A_write_token_writes_its_own_tenant_only()
    {
        var (_, token) = await MakeAsync("write");
        using var client = Bearer(token);
        Assert.Equal(HttpStatusCode.Created,
            (await client.PostAsync("/api/apps", Json(new { company = "Globex", role = "Engineer", status = "lead" }))).StatusCode);
        Assert.Contains("Globex", await _session.GetStringAsync("/api/apps"));

        // Another tenant's token sees its own (empty) list, never this one's.
        var (_, otherSid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={otherSid}");
        var (_, otherToken) = await MakeAsync("read", other);
        using var otherClient = Bearer(otherToken);
        Assert.DoesNotContain("Globex", await otherClient.GetStringAsync("/api/apps"));
    }

    [Theory]
    [InlineData("GET", "/api/account/tokens")]
    [InlineData("POST", "/api/account/tokens")]
    [InlineData("DELETE", "/api/account/tokens/1")]
    [InlineData("GET", "/api/account/sessions")]
    [InlineData("DELETE", "/api/account/sessions")]
    [InlineData("DELETE", "/api/account")]
    [InlineData("GET", "/api/calendar/feed")]
    [InlineData("POST", "/api/calendar/feed")]
    [InlineData("DELETE", "/api/calendar/feed")]
    public async Task A_token_cannot_manage_the_accounts_credentials(string method, string path)
    {
        var (_, token) = await MakeAsync("write");
        using var client = Bearer(token);
        var res = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
        {
            Content = method == "POST" ? Json(new { name = "more", scope = "write" }) : null,
        });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);

        await using var conn = await OwnerAsync();
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM users WHERE id = @t", new { t = _t }));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM api_tokens WHERE tenant_id = @t", new { t = _t }));
    }

    [Fact]
    public async Task Unknown_revoked_calendar_and_disabled_account_tokens_get_401()
    {
        using (var guess = Bearer(ApiTokenRepo.Prefix + "guess"))
            Assert.Equal(HttpStatusCode.Unauthorized, (await guess.GetAsync("/api/apps")).StatusCode);

        var feed = await ReadJsonAsync(await _session.PostAsync("/api/calendar/feed", null));
        var calendarToken = feed.GetProperty("path").GetString()!.Split("token=")[1];
        using (var cal = Bearer(Uri.UnescapeDataString(calendarToken)))
            Assert.Equal(HttpStatusCode.Unauthorized, (await cal.GetAsync("/api/apps")).StatusCode);

        var (id, token) = await MakeAsync("read");
        using var client = Bearer(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);

        await using var conn = await OwnerAsync();
        await conn.ExecuteAsync("UPDATE users SET status = 'disabled' WHERE id = @t", new { t = _t });
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/apps")).StatusCode);
        await conn.ExecuteAsync("UPDATE users SET status = 'active' WHERE id = @t", new { t = _t });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _session.DeleteAsync($"/api/account/tokens/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/apps")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _session.DeleteAsync($"/api/account/tokens/{id}")).StatusCode);
    }

    [Fact]
    public async Task Another_tenant_cannot_revoke_or_list_a_token()
    {
        var (id, token) = await MakeAsync("read");
        var (_, otherSid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var other = _factory.CreateClient();
        other.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={otherSid}");

        Assert.Equal("[]", await other.GetStringAsync("/api/account/tokens"));
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/account/tokens/{id}")).StatusCode);
        using var client = Bearer(token);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);
    }

    [Fact]
    public async Task Last_used_is_stamped_at_most_once_a_minute()
    {
        var (id, token) = await MakeAsync("read");
        using var client = Bearer(token);
        await using var conn = await OwnerAsync();
        Task<DateTime?> LastUsed() => conn.ExecuteScalarAsync<DateTime?>("SELECT last_used_at FROM api_tokens WHERE id = @id", new { id });

        Assert.Null(await LastUsed());
        await client.GetAsync("/api/stats");
        var first = await LastUsed();
        Assert.NotNull(first);

        await conn.ExecuteAsync("UPDATE api_tokens SET last_used_at = now() - interval '30 seconds' WHERE id = @id", new { id });
        var recent = await LastUsed();
        await client.GetAsync("/api/stats");
        Assert.Equal(recent, await LastUsed());

        await conn.ExecuteAsync("UPDATE api_tokens SET last_used_at = now() - interval '2 minutes' WHERE id = @id", new { id });
        var stale = await LastUsed();
        await client.GetAsync("/api/stats");
        Assert.True(await LastUsed() > stale);
    }

    [Fact]
    public async Task Each_token_has_its_own_rate_limit_and_the_session_is_not_throttled_by_it()
    {
        var (_, token) = await MakeAsync("read");
        var (_, fresh) = await MakeAsync("read");
        using var client = Bearer(token);
        for (var i = 0; i < ApiTokenRepo.RequestsPerMinute; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/stats")).StatusCode);

        using var other = Bearer(fresh);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/api/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _session.GetAsync("/api/stats")).StatusCode);
    }

    [Fact]
    public async Task A_session_cookie_still_wins_and_bearer_needs_no_csrf_dance()
    {
        // A cookie request with a bogus bearer is the session's, unchanged.
        using var both = _factory.CreateClient();
        both.DefaultRequestHeaders.Add("Cookie", _session.DefaultRequestHeaders.GetValues("Cookie").Single());
        both.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiTokenRepo.Prefix + "nope");
        Assert.Equal(HttpStatusCode.OK, (await both.GetAsync("/api/account/tokens")).StatusCode);

        // The auth routes are the cookie's: a token doesn't sign anyone in there.
        var (_, token) = await MakeAsync("write");
        using var client = Bearer(token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
