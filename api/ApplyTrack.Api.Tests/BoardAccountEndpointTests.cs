// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ApplyTrack.Api.Tests;

/// <summary>The candidate's own sign-ins on account-only ATSs (#216): saved with a write-only
/// password, listed without it, kept when omitted, cleared when blank, forgotten on delete.</summary>
[Collection(PostgresCollection.Name)]
public class BoardAccountEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public BoardAccountEndpointTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories)
            await f.DisposeAsync();
    }

    private async Task<HttpClient> ClientAsync()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            b.UseSetting("Secrets:Key", TestAuth.MasterKey);
        });
        _factories.Add(f);
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task An_account_saves_with_its_password_write_only_is_kept_when_omitted_cleared_when_blank_and_forgotten_on_delete()
    {
        var client = await ClientAsync();
        Assert.Equal(0, (await ReadJson(await client.GetAsync("/api/board-accounts"))).GetArrayLength());

        var put = await client.PutAsync("/api/board-accounts", Json("""{"host":"https://Career4.SuccessFactors.com/careers?company=Kiewit","username":"ada@example.com","password":"Sekrit-9$"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var raw = await (await client.GetAsync("/api/board-accounts")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Sekrit", raw);
        var list = JsonDocument.Parse(raw).RootElement;
        var one = Assert.Single(list.EnumerateArray());
        Assert.Equal("career4.successfactors.com", one.GetProperty("host").GetString());
        Assert.Equal("ada@example.com", one.GetProperty("username").GetString());
        Assert.True(one.GetProperty("has_password").GetBoolean());

        // Omitting the password keeps it; a blank clears it.
        await client.PutAsync("/api/board-accounts", Json("""{"host":"career4.successfactors.com","username":"ada2@example.com"}"""));
        one = Assert.Single((await ReadJson(await client.GetAsync("/api/board-accounts"))).EnumerateArray());
        Assert.Equal("ada2@example.com", one.GetProperty("username").GetString());
        Assert.True(one.GetProperty("has_password").GetBoolean());
        await client.PutAsync("/api/board-accounts", Json("""{"host":"career4.successfactors.com","username":"ada2@example.com","password":""}"""));
        one = Assert.Single((await ReadJson(await client.GetAsync("/api/board-accounts"))).EnumerateArray());
        Assert.False(one.GetProperty("has_password").GetBoolean());

        // A host that is not one, or no username, is refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/board-accounts", Json("""{"host":"not a host","username":"a@b.c","password":"x"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/board-accounts", Json("""{"host":"jobs.example.com","username":"","password":"x"}"""))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/board-accounts/career4.successfactors.com")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync("/api/board-accounts/career4.successfactors.com")).StatusCode);
        Assert.Equal(0, (await ReadJson(await client.GetAsync("/api/board-accounts"))).GetArrayLength());
    }

    [Fact]
    public async Task Sign_in_now_marks_a_signed_in_sources_account_for_the_worker_and_is_refused_for_any_other()
    {
        // The agent renews a kept session on its own clock, and the LinkedIn app's tap has to
        // come within minutes of it; Sign in now lets the person pick the moment. Only a
        // LinkedIn, MyGreenhouse or Handshake account keeps a session; the rest are refused,
        // and an unsaved host is not found.
        var client = await ClientAsync();
        await client.PutAsync("/api/board-accounts", Json("""{"host":"linkedin.com","username":"ada@example.com","password":"Sekrit-9$"}"""));
        await client.PutAsync("/api/board-accounts", Json("""{"host":"career4.successfactors.com","username":"ada@example.com","password":"Sekrit-9$"}"""));
        var list = (await ReadJson(await client.GetAsync("/api/board-accounts"))).EnumerateArray().ToList();
        var linkedin = list.Single(a => a.GetProperty("host").GetString() == "linkedin.com");
        Assert.True(linkedin.GetProperty("keeps_session").GetBoolean());
        Assert.Equal(JsonValueKind.Null, linkedin.GetProperty("session_expires_at").ValueKind);
        Assert.Equal(JsonValueKind.Null, linkedin.GetProperty("renew_requested_at").ValueKind);
        Assert.False(list.Single(a => a.GetProperty("host").GetString() == "career4.successfactors.com").GetProperty("keeps_session").GetBoolean());

        var renew = await client.PostAsync("/api/board-accounts/www.linkedin.com/renew", null);
        Assert.Equal(HttpStatusCode.Accepted, renew.StatusCode);
        linkedin = (await ReadJson(renew)).EnumerateArray().Single(a => a.GetProperty("host").GetString() == "linkedin.com");
        Assert.Equal(JsonValueKind.String, linkedin.GetProperty("renew_requested_at").ValueKind);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/board-accounts/career4.successfactors.com/renew", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/api/board-accounts/app.joinhandshake.com/renew", null)).StatusCode);
    }

    [Theory]
    [InlineData("successfactors.com", "career4.successfactors.com", true)]
    [InlineData("career4.successfactors.com", "career4.successfactors.com", true)]
    [InlineData("www.career4.successfactors.com", "career4.successfactors.com", true)]
    [InlineData("career4.successfactors.com", "career5.successfactors.com", true)]   // same registrable domain
    [InlineData("successfactors.com", "kiewitcareers.kiewit.com", false)]
    [InlineData("successfactors.com", "notsuccessfactors.com", false)]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    public void An_account_covers_its_host_its_subdomains_and_its_registrable_domain_and_nothing_else(string saved, string host, bool expected) =>
        Assert.Equal(expected, new BoardAccount(saved, "ada", "pw").Covers(host));
}
