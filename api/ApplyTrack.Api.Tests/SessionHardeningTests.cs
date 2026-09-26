// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Session hardening (#346): ids stored only as sha256, legacy raw rows hashed in place by
/// the migration without signing anyone out, <c>users.status</c> honoured, sliding expiry
/// under an absolute cap, and the "where am I signed in" list with sign-out-everywhere.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SessionHardeningTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private CapturingEmailSender _emails = null!;

    public SessionHardeningTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync()
    {
        _emails = new CapturingEmailSender();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IEmailSender>();
                s.AddSingleton<IEmailSender>(_emails);
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient Client(string sid)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestBrowser/1.0");
        return client;
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static string? SessionCookie(HttpResponseMessage res) =>
        res.Headers.TryGetValues("Set-Cookie", out var values)
            ? values.FirstOrDefault(v => v.StartsWith(AuthCookie.Name + "=", StringComparison.Ordinal))
            : null;

    [Fact]
    public async Task Signing_in_stores_only_the_hash_of_the_session_id()
    {
        var email = TestAuth.UniqueEmail();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestBrowser/1.0");
        await client.PostAsync("/api/auth/request",
            new StringContent($$"""{"email":"{{email}}"}""", Encoding.UTF8, "application/json"));
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(_emails.LinkFor(email)!).Query)["token"]!;
        var verify = await client.PostAsync("/api/auth/verify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

        var cookie = SessionCookie(verify)!;
        var sid = cookie[(AuthCookie.Name.Length + 1)..cookie.IndexOf(';')];

        await using var conn = await OpenAsync();
        var stored = (await conn.QueryAsync<(string Id, string UserAgent)>(
            "SELECT s.id, s.user_agent FROM sessions s JOIN users u ON u.id = s.user_id WHERE u.email = @email",
            new { email })).Single();
        Assert.NotEqual(sid, stored.Id);
        Assert.Equal(SessionRepo.Hash(sid), stored.Id);
        Assert.Equal("TestBrowser/1.0", stored.UserAgent);

        // The stored value is not a cookie: presenting it gets nowhere.
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(stored.Id).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(sid).GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task The_migration_hashes_legacy_raw_ids_in_place_and_is_rerunnable()
    {
        // A row as an older release wrote it: the raw cookie value as the PK.
        await using var conn = await OpenAsync();
        var userId = await TestAuth.EnsureUserAsync(conn, TestAuth.UniqueEmail());
        var raw = Tokens.NewOpaque();
        await conn.ExecuteAsync(
            "INSERT INTO sessions (id, user_id, expires_at) VALUES (@raw, @userId, now() + interval '1 day')",
            new { raw, userId });

        var script = MigrationScript("0042_session_hardening.sql");
        await conn.ExecuteAsync(script);
        await conn.ExecuteAsync(script); // a second run must not re-hash

        Assert.Equal(SessionRepo.Hash(raw), await conn.ExecuteScalarAsync<string>(
            "SELECT id FROM sessions WHERE user_id = @userId", new { userId }));
        // The browser holding the old cookie is still signed in.
        Assert.Equal(HttpStatusCode.OK, (await Client(raw).GetAsync("/api/auth/me")).StatusCode);
    }

    private static string MigrationScript(string file)
    {
        var asm = typeof(SessionRepo).Assembly;
        var name = asm.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.Ordinal));
        using var reader = new StreamReader(asm.GetManifestResourceStream(name)!);
        return reader.ReadToEnd();
    }

    [Fact]
    public async Task A_disabled_account_is_signed_out_and_gets_no_new_link()
    {
        var email = TestAuth.UniqueEmail();
        var (tenantId, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var client = Client(sid);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("UPDATE users SET status = 'disabled' WHERE id = @tenantId", new { tenantId });

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/apps")).StatusCode);

        var anon = _factory.CreateClient();
        var res = await anon.PostAsync("/api/auth/request",
            new StringContent($$"""{"email":"{{email}}"}""", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode); // still no enumeration signal
        Assert.Null(_emails.LinkFor(email));

        // Re-enabled, the same cookie works again — status is checked, not the session dropped.
        await conn.ExecuteAsync("UPDATE users SET status = 'active' WHERE id = @tenantId", new { tenantId });
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);
    }

    [Fact]
    public async Task A_link_issued_before_the_account_was_disabled_cannot_sign_in()
    {
        var email = TestAuth.UniqueEmail();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        await client.PostAsync("/api/auth/request",
            new StringContent($$"""{"email":"{{email}}"}""", Encoding.UTF8, "application/json"));
        var token = System.Web.HttpUtility.ParseQueryString(new Uri(_emails.LinkFor(email)!).Query)["token"]!;

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("UPDATE users SET status = 'disabled' WHERE email = @email", new { email });

        var verify = await client.PostAsync("/api/auth/verify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));
        Assert.Equal("/?error=account_disabled", verify.Headers.Location?.OriginalString);
        Assert.Null(SessionCookie(verify));
    }

    [Fact]
    public async Task A_stale_session_slides_forward_and_the_cookie_is_reissued()
    {
        var (tenantId, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            "UPDATE sessions SET last_seen_at = now() - interval '2 hours' WHERE user_id = @tenantId",
            new { tenantId });

        var res = await Client(sid).GetAsync("/api/apps");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(SessionCookie(res));

        var expires = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT expires_at FROM sessions WHERE user_id = @tenantId", new { tenantId });
        Assert.InRange(expires, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));

        // Fresh again: the next request neither writes nor re-issues the cookie.
        var again = await Client(sid).GetAsync("/api/apps");
        Assert.Null(SessionCookie(again));
    }

    [Fact]
    public async Task Sliding_never_passes_the_absolute_lifetime()
    {
        var (tenantId, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            """
            UPDATE sessions SET created_at = now() - interval '89 days',
                                last_seen_at = now() - interval '2 hours'
            WHERE user_id = @tenantId
            """,
            new { tenantId });

        Assert.Equal(HttpStatusCode.OK, (await Client(sid).GetAsync("/api/apps")).StatusCode);
        var expires = await conn.ExecuteScalarAsync<DateTime>(
            "SELECT expires_at FROM sessions WHERE user_id = @tenantId", new { tenantId });
        Assert.InRange(expires, DateTime.UtcNow, DateTime.UtcNow.AddDays(1.1));
    }

    [Fact]
    public async Task Sessions_are_listed_and_sign_out_everywhere_keeps_only_this_browser()
    {
        var email = TestAuth.UniqueEmail();
        var (_, here) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var (_, laptop) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var (_, phone) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var (_, stranger) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);

        var client = Client(here);
        var list = await ReadJson(await client.GetAsync("/api/account/sessions"));
        Assert.Equal(3, list.GetArrayLength());
        var current = list.EnumerateArray().Single(s => s.GetProperty("current").GetBoolean());
        Assert.Equal(SessionRepo.Hash(here), current.GetProperty("id").GetString());
        foreach (var field in new[] { "created_at", "last_seen_at", "expires_at", "user_agent" })
            Assert.True(current.TryGetProperty(field, out _), field);

        var revoke = await ReadJson(await client.DeleteAsync("/api/account/sessions"));
        Assert.Equal(2, revoke.GetProperty("revoked").GetInt32());

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(laptop).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(phone).GetAsync("/api/auth/me")).StatusCode);
        // Another account's session is untouched.
        Assert.Equal(HttpStatusCode.OK, (await Client(stranger).GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task One_session_can_be_revoked_by_id_but_never_another_accounts()
    {
        var email = TestAuth.UniqueEmail();
        var (_, here) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var (_, other) = await TestAuth.SeedSessionAsync(_pg.ConnectionString, email);
        var (_, stranger) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);

        var client = Client(here);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/account/sessions/{SessionRepo.Hash(stranger)}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client(stranger).GetAsync("/api/auth/me")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/account/sessions/{SessionRepo.Hash(other)}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(other).GetAsync("/api/auth/me")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }
}
