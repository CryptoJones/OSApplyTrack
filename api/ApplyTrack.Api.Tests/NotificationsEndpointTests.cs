// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;
using Dapper;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>The Telegram settings round-trip: the token is write-only and encrypted,
/// omitted-means-keep, refused without a master key; the test button goes through the notifier.</summary>
[Collection(PostgresCollection.Name)]
public class NotificationsEndpointTests : IAsyncLifetime
{
    private const string Token = "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";

    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public NotificationsEndpointTests(PostgresFixture pg) => _pg = pg;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories)
            await f.DisposeAsync();
    }

    private async Task<HttpClient> ClientAsync(bool secrets = true, INotifier? notifier = null)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            if (secrets) b.UseSetting("Secrets:Key", TestAuth.MasterKey);
            else
            {
                // No operator key at all: the app must generate and keep one itself.
                b.UseSetting("Secrets:Key", "");
                b.UseSetting("Secrets:KeyFile", Path.Combine(Path.GetTempPath(), "applytrack-notif-" + Guid.NewGuid().ToString("N"), "secrets.key"));
            }
            if (notifier is not null)
                b.ConfigureTestServices(s =>
                {
                    s.RemoveAll<INotifier>();
                    s.AddSingleton(notifier);
                });
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
    public async Task Default_view_is_off_and_secrets_are_available_even_with_no_operator_key()
    {
        // Secure by default: with no APPLYTRACK_SECRETS_KEY the app generated a key file,
        // so there is no "unavailable" state for a tenant to run into any more.
        var client = await ClientAsync(secrets: false);
        var v = await ReadJson(await client.GetAsync("/api/notifications"));
        Assert.False(v.GetProperty("telegram_enabled").GetBoolean());
        Assert.False(v.GetProperty("has_bot_token").GetBoolean());
        Assert.Equal("", v.GetProperty("telegram_chat_id").GetString());
        Assert.True(v.GetProperty("secrets_available").GetBoolean());
    }

    [Fact]
    public async Task A_token_is_stored_under_the_generated_key_when_no_master_key_is_configured()
    {
        var client = await ClientAsync(secrets: false);
        var res = await client.PutAsync("/api/notifications", Json($$"""{"telegram_bot_token":"{{Token}}"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await ReadJson(await client.GetAsync("/api/notifications"))).GetProperty("has_bot_token").GetBoolean());
    }

    [Fact]
    public async Task The_token_is_stored_encrypted_never_echoed_kept_when_omitted_and_cleared_when_blank()
    {
        var client = await ClientAsync();
        var put = await client.PutAsync("/api/notifications",
            Json($$"""{"telegram_enabled":true,"telegram_chat_id":"4242","telegram_bot_token":"{{Token}}"}"""));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var raw = await put.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Token, raw);
        Assert.Contains("\"has_bot_token\":true", raw);

        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        var ciphertexts = await conn.QueryAsync<string>("SELECT telegram_bot_token_ciphertext FROM notification_settings");
        Assert.DoesNotContain(Token, ciphertexts);

        // Omitted token: kept. Chat id changes.
        var kept = await ReadJson(await client.PutAsync("/api/notifications", Json("""{"telegram_chat_id":"99"}""")));
        Assert.True(kept.GetProperty("has_bot_token").GetBoolean());
        Assert.Equal("99", kept.GetProperty("telegram_chat_id").GetString());
        Assert.True(kept.GetProperty("telegram_enabled").GetBoolean());

        // Blank token: cleared.
        var cleared = await ReadJson(await client.PutAsync("/api/notifications", Json("""{"telegram_bot_token":""}""")));
        Assert.False(cleared.GetProperty("has_bot_token").GetBoolean());
    }

    [Theory]
    [InlineData("""{"telegram_bot_token":"not-a-token"}""")]
    [InlineData("""{"telegram_chat_id":"abc"}""")]
    public async Task Malformed_values_are_400(string body)
    {
        var client = await ClientAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync("/api/notifications", Json(body))).StatusCode);
    }

    [Fact]
    public async Task Test_message_refuses_when_unconfigured_then_sends_through_the_notifier_even_while_off()
    {
        var notifier = new CapturingNotifier();
        var client = await ClientAsync(notifier: notifier);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/notifications/test", null)).StatusCode);

        await client.PutAsync("/api/notifications",
            Json($$"""{"telegram_enabled":false,"telegram_chat_id":"4242","telegram_bot_token":"{{Token}}"}"""));
        var res = await client.PostAsync("/api/notifications/test", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var sent = Assert.Single(notifier.Sent);
        Assert.Equal(Token, sent.BotToken);       // proves the decrypt path
        Assert.Equal("4242", sent.ChatId);
        Assert.Contains("moo", sent.Text);
    }

    [Fact]
    public async Task Test_message_reports_an_upstream_refusal_as_502()
    {
        var client = await ClientAsync(notifier: new CapturingNotifier(
            new NotificationFailedException("Telegram refused the message (Unauthorized)")));
        await client.PutAsync("/api/notifications",
            Json($$"""{"telegram_chat_id":"4242","telegram_bot_token":"{{Token}}"}"""));
        var res = await client.PostAsync("/api/notifications/test", null);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("Unauthorized", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Test_message_is_rate_limited()
    {
        var notifier = new CapturingNotifier();
        var client = await ClientAsync(notifier: notifier);
        await client.PutAsync("/api/notifications",
            Json($$"""{"telegram_chat_id":"4242","telegram_bot_token":"{{Token}}"}"""));
        for (var i = 0; i < 5; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/notifications/test", null)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsync("/api/notifications/test", null)).StatusCode);
    }

    [Fact]
    public async Task Settings_are_per_tenant()
    {
        var client = await ClientAsync();
        await client.PutAsync("/api/notifications", Json("""{"telegram_enabled":true,"telegram_chat_id":"1"}"""));
        var other = await ClientAsync();
        Assert.False((await ReadJson(await other.GetAsync("/api/notifications"))).GetProperty("telegram_enabled").GetBoolean());
    }
}
