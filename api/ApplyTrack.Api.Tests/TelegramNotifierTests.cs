// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text.Json;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>The moo's wire shape, and that no failure path ever leaks the bot token.</summary>
public class TelegramNotifierTests
{
    private const string Token = "123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef";

    private static TelegramNotifier NewNotifier(CapturingHandler handler) =>
        new(new StubHttpClientFactory(handler, new Uri("https://api.telegram.org/")),
            NullLogger<TelegramNotifier>.Instance);

    [Fact]
    public async Task Posts_send_message_with_chat_id_and_text_and_no_parse_mode()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.OK, "{\"ok\":true,\"result\":{}}");

        await NewNotifier(handler).SendAsync(Token, "4242", "🐮 moo — hi");

        var (req, body) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.Equal($"/bot{Token}/sendMessage", req.RequestUri!.AbsolutePath);
        Assert.Equal("api.telegram.org", req.RequestUri.Host);
        using var json = JsonDocument.Parse(body);
        Assert.Equal("4242", json.RootElement.GetProperty("chat_id").GetString());
        Assert.Equal("🐮 moo — hi", json.RootElement.GetProperty("text").GetString());
        Assert.True(json.RootElement.GetProperty("disable_web_page_preview").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("parse_mode", out _));
    }

    [Fact]
    public async Task A_refusal_surfaces_telegrams_description_without_the_token()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.Unauthorized, "{\"ok\":false,\"description\":\"Unauthorized\"}");

        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() =>
            NewNotifier(handler).SendAsync(Token, "4242", "x"));

        Assert.Contains("Unauthorized", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
    }

    [Fact]
    public async Task A_200_that_is_not_ok_is_still_a_failure()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.OK, "{\"ok\":false,\"description\":\"chat not found\"}");
        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() =>
            NewNotifier(handler).SendAsync(Token, "4242", "x"));
        Assert.Contains("chat not found", ex.Message);
    }

    [Fact]
    public async Task A_network_failure_is_wrapped()
    {
        var handler = new CapturingHandler(_ => throw new HttpRequestException("boom " + Token));
        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() =>
            NewNotifier(handler).SendAsync(Token, "4242", "x"));
        Assert.Equal("could not reach Telegram", ex.Message);
    }

    [Fact]
    public async Task An_oversized_response_is_rejected()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.OK, "{\"ok\":true,\"pad\":\"" + new string('x', 70 * 1024) + "\"}");
        await Assert.ThrowsAsync<NotificationFailedException>(() =>
            NewNotifier(handler).SendAsync(Token, "4242", "x"));
    }

    [Fact]
    public void The_message_names_the_role_and_links_when_a_public_url_is_set()
    {
        Assert.Equal("🐮 moo — Acme · Engineer is ready to submit\nhttps://apply.example/#app=acme-engineer.md",
            PacketReadyNotifier.BuildMessage("Acme", "Engineer",
                PacketReadyNotifier.DeepLink("https://apply.example", "acme-engineer.md")));
        Assert.Equal("🐮 moo — Acme is ready to submit",
            PacketReadyNotifier.BuildMessage("Acme", "", PacketReadyNotifier.DeepLink("", "acme.md")));
        Assert.Equal("https://apply.example/#app=a%20b.md", PacketReadyNotifier.DeepLink("https://apply.example", "a b.md"));
    }
}
