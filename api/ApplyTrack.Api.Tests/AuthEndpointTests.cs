// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Drives the magic-link auth spine over HTTP: the choke-point's 401 on protected
/// routes, the request -> confirm -> session happy path, the no-account-enumeration
/// guarantee, single-use/expired-token rejection, the browser binding that stops login
/// CSRF (#341), logout revocation, and — the load-
/// bearing one — cross-tenant isolation through the live middleware. Swaps a
/// capturing email sender in so the test can read the link the server would mail.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AuthEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private CapturingEmailSender _emails = null!;

    public AuthEndpointTests(PostgresFixture pg) => _pg = pg;

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

    // Don't auto-follow redirects so we can assert verify's 302 + Location; the cookie
    // container is still on, so a session cookie set by verify rides later requests.
    private HttpClient NewClient() =>
        _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static string VerifyPath(string link) => new Uri(link).PathAndQuery;

    private static string TokenOf(string link) =>
        System.Web.HttpUtility.ParseQueryString(new Uri(link).Query)["token"]!;

    /// <summary>What the confirm page's button does: POST the token back as a form.</summary>
    private static Task<HttpResponseMessage> ConfirmAsync(HttpClient client, string token) =>
        client.PostAsync("/api/auth/verify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = token }));

    /// <summary>Asks for a link on <paramref name="client"/> (which takes the pre-auth cookie) and returns it.</summary>
    private async Task<string> RequestLinkAsync(HttpClient client, string email)
    {
        var requested = await client.PostAsync("/api/auth/request", Json($$"""{"email":"{{email}}"}"""));
        Assert.Equal(HttpStatusCode.OK, requested.StatusCode);
        var link = _emails.LinkFor(email);
        Assert.NotNull(link);
        return link!;
    }

    /// <summary>Runs the real request -> open link -> confirm flow on <paramref name="client"/>.</summary>
    private async Task SignInAsync(HttpClient client, string email)
    {
        var link = await RequestLinkAsync(client, email);

        var page = await client.GetAsync(VerifyPath(link));
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("action=\"/api/auth/verify\"", await page.Content.ReadAsStringAsync());

        var verify = await ConfirmAsync(client, TokenOf(link));
        Assert.Equal(HttpStatusCode.Found, verify.StatusCode);
        Assert.Equal("/", verify.Headers.Location?.OriginalString);
    }

    /// <summary>Signs a fresh client in and returns it carrying the session cookie.</summary>
    private async Task<HttpClient> LoginAsync(string email)
    {
        var client = NewClient();
        await SignInAsync(client, email);
        return client;
    }

    [Fact]
    public async Task Protected_route_without_session_is_401_with_detail()
    {
        var res = await NewClient().GetAsync("/api/apps");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal("authentication required", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Me_without_session_is_401()
    {
        var res = await NewClient().GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.False(string.IsNullOrEmpty((await ReadJson(res)).GetProperty("detail").GetString()));
    }

    [Fact]
    public async Task Request_then_verify_mints_a_session_that_me_reports()
    {
        var email = TestAuth.UniqueEmail();
        var client = await LoginAsync(email);

        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Equal(email, (await ReadJson(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Request_is_always_200_and_malformed_address_sends_nothing()
    {
        // No '@' => no user, no link — but the response is the same 200 {ok:true} a real
        // address gets, so a caller can't probe which addresses exist.
        var res = await NewClient().PostAsync("/api/auth/request", Json("""{"email":"not-an-email"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());
        Assert.Null(_emails.LinkFor("not-an-email"));

        // A well-formed (here unknown) address gets the identical 200.
        var unknown = TestAuth.UniqueEmail();
        var res2 = await NewClient().PostAsync("/api/auth/request", Json($$"""{"email":"{{unknown}}"}"""));
        Assert.Equal(HttpStatusCode.OK, res2.StatusCode);
        Assert.True((await ReadJson(res2)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Verify_with_garbage_token_redirects_to_invalid_link()
    {
        var client = NewClient();
        await RequestLinkAsync(client, TestAuth.UniqueEmail());

        var res = await ConfirmAsync(client, "totally-bogus");
        Assert.Equal(HttpStatusCode.Found, res.StatusCode);
        Assert.Equal("/?error=invalid_link", res.Headers.Location?.OriginalString);

        var missing = await NewClient().GetAsync("/api/auth/verify");
        Assert.Equal("/?error=invalid_link", missing.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Token_is_single_use()
    {
        var client = NewClient();
        var token = TokenOf(await RequestLinkAsync(client, TestAuth.UniqueEmail()));

        var first = await ConfirmAsync(client, token);
        Assert.Equal("/", first.Headers.Location?.OriginalString);

        var second = await ConfirmAsync(client, token);
        Assert.Equal("/?error=invalid_link", second.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Expired_token_is_rejected()
    {
        var client = NewClient();
        var email = TestAuth.UniqueEmail();
        var token = TokenOf(await RequestLinkAsync(client, email));
        await using (var conn = new NpgsqlConnection(_pg.ConnectionString))
        {
            await conn.OpenAsync();
            await conn.ExecuteAsync(
                "UPDATE magic_tokens SET expires_at = now() - interval '1 minute' WHERE token_sha256 = @hash",
                new { hash = Tokens.Sha256(token) });
        }

        var res = await ConfirmAsync(client, token);
        Assert.Equal("/?error=invalid_link", res.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task Opening_the_link_does_not_spend_the_token()
    {
        // #341: a mail scanner pre-fetching the link (a GET, no cookies) gets the confirm
        // page and nothing else — the person's own click still signs them in.
        var client = NewClient();
        var email = TestAuth.UniqueEmail();
        var link = await RequestLinkAsync(client, email);

        for (var i = 0; i < 2; i++)
        {
            var scanned = await NewClient().GetAsync(VerifyPath(link));
            Assert.Equal(HttpStatusCode.OK, scanned.StatusCode);
            Assert.Equal("no-store", scanned.Headers.CacheControl?.ToString());
            Assert.False(scanned.Headers.Contains("Set-Cookie"));
        }

        var verify = await ConfirmAsync(client, TokenOf(link));
        Assert.Equal("/", verify.Headers.Location?.OriginalString);
        var me = await client.GetAsync("/api/auth/me");
        Assert.Equal(email, (await ReadJson(me)).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Attackers_link_cannot_sign_in_another_browser()
    {
        // #341 login CSRF: the attacker requests a link for their own address and gets a
        // victim to open it. Without the attacker's pre-auth cookie it signs nobody in.
        var attacker = NewClient();
        var attackerEmail = TestAuth.UniqueEmail();
        var token = TokenOf(await RequestLinkAsync(attacker, attackerEmail));

        // A browser that never asked for a link is told to use the one that did.
        var victim = NewClient();
        var res = await ConfirmAsync(victim, token);
        Assert.Equal("/?error=wrong_browser", res.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/me")).StatusCode);

        // A browser holding its own pre-auth cookie is refused too.
        await RequestLinkAsync(victim, TestAuth.UniqueEmail());
        res = await ConfirmAsync(victim, token);
        Assert.Equal("/?error=invalid_link", res.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await victim.GetAsync("/api/auth/me")).StatusCode);

        // Neither attempt spent the token: its own browser can still use it.
        res = await ConfirmAsync(attacker, token);
        Assert.Equal("/", res.Headers.Location?.OriginalString);
        Assert.Equal(attackerEmail, (await ReadJson(await attacker.GetAsync("/api/auth/me"))).GetProperty("email").GetString());
    }

    [Fact]
    public async Task Signing_in_again_rotates_the_browsers_session()
    {
        var email = TestAuth.UniqueEmail();
        var client = await LoginAsync(email);
        await SignInAsync(client, email);

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        var live = await conn.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM sessions s JOIN users u ON u.id = s.user_id WHERE u.email = @email",
            new { email });
        Assert.Equal(1, live);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Confirm_page_encodes_the_token()
    {
        var res = await NewClient().GetAsync("/api/auth/verify?token=" + Uri.EscapeDataString("\"><script>x</script>"));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var html = await res.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Logout_revokes_the_session_instantly()
    {
        var client = await LoginAsync(TestAuth.UniqueEmail());
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/apps")).StatusCode);

        var logout = await client.PostAsync("/api/auth/logout", null);
        Assert.Equal(HttpStatusCode.OK, logout.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/apps")).StatusCode);
    }

    [Fact]
    public async Task Tenants_are_isolated_through_the_choke_point()
    {
        var alice = await LoginAsync(TestAuth.UniqueEmail());
        var bob = await LoginAsync(TestAuth.UniqueEmail());

        var created = await alice.PostAsync("/api/apps", Json("""{"company":"Alice Co","role":"Dev"}"""));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // Bob, on his own session, sees an empty list — never Alice's app.
        var bobList = await ReadJson(await bob.GetAsync("/api/apps"));
        Assert.Equal(0, bobList.GetArrayLength());

        // Alice still sees her own.
        var aliceList = await ReadJson(await alice.GetAsync("/api/apps"));
        Assert.Equal(1, aliceList.GetArrayLength());
        Assert.Equal("Alice Co", aliceList[0].GetProperty("company").GetString());
    }

    [Fact]
    public async Task Auth_request_rate_limit_rejects_excess_requests()
    {
        var client = NewClient();
        const string body = """{"email":"rate@example.com"}""";

        for (var i = 0; i < 10; i++)
        {
            var res = await client.PostAsync("/api/auth/request", Json(body));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        var rejected = await client.PostAsync("/api/auth/request", Json(body));
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
    }

    [Fact]
    public async Task Responses_carry_the_security_headers()
    {
        var res = await NewClient().GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        Assert.True(res.Headers.TryGetValues("Content-Security-Policy", out var csp));
        Assert.Contains("script-src 'self'", string.Join(" ", csp));
        // #342: no remote images, so markdown can't beacon data off-instance.
        Assert.Contains("img-src 'self' data:;", string.Join(" ", csp));
        Assert.DoesNotContain("https:", string.Join(" ", csp));
        Assert.True(res.Headers.TryGetValues("X-Content-Type-Options", out var nosniff));
        Assert.Equal("nosniff", string.Join("", nosniff));
        Assert.True(res.Headers.Contains("X-Frame-Options"));
        Assert.True(res.Headers.Contains("Referrer-Policy"));
        // Plain-HTTP test host: HSTS must NOT be emitted.
        Assert.False(res.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task The_spa_script_is_revalidated_so_a_deploy_is_seen()
    {
        var res = await NewClient().GetAsync("/app.js");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl?.NoCache);
    }

    [Fact]
    public async Task HSTS_is_emitted_over_https()
    {
        // TestServer derives Request.IsHttps from the request URI scheme, so an https
        // BaseAddress exercises the middleware's HTTPS branch without real TLS.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("https://localhost"),
            AllowAutoRedirect = false,
        });

        var res = await client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.TryGetValues("Strict-Transport-Security", out var hsts));
        Assert.Contains("max-age=31536000", string.Join(" ", hsts));
    }

    [Fact]
    public async Task Readiness_probe_reports_database_connected()
    {
        // /health/ready actually opens the (Testcontainers) DB — distinct from the
        // static liveness probe.
        var res = await NewClient().GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("connected", (await ReadJson(res)).GetProperty("database").GetString());
    }

    [Fact]
    public async Task Spoofed_host_without_public_base_url_sends_no_link()
    {
        // #337: no App:PublicBaseUrl and an attacker-chosen Host -> the link would point at
        // evil.example. Nothing is mailed, and the response is the same 200 as ever.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://evil.example"),
        });
        var email = TestAuth.UniqueEmail();

        var res = await client.PostAsync("/api/auth/request", Json($$"""{"email":"{{email}}"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(res)).GetProperty("ok").GetBoolean());
        Assert.Null(_emails.LinkFor(email));
    }

    [Fact]
    public async Task Configured_public_base_url_wins_over_a_spoofed_host()
    {
        using var pinned = _factory.WithWebHostBuilder(b =>
            b.UseSetting("App:PublicBaseUrl", "https://apply.example/"));
        var client = pinned.CreateClient(new WebApplicationFactoryClientOptions
        {
            BaseAddress = new Uri("http://evil.example"),
        });
        var email = TestAuth.UniqueEmail();

        await client.PostAsync("/api/auth/request", Json($$"""{"email":"{{email}}"}"""));

        var link = _emails.LinkFor(email);
        Assert.NotNull(link);
        Assert.StartsWith("https://apply.example/api/auth/verify?token=", link);
    }
}
