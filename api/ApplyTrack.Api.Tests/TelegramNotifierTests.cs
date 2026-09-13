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
    public async Task Get_updates_is_a_short_poll_for_messages_only_that_confirms_with_the_offset()
    {
        const string Updates = """
            {"ok":true,"result":[
              {"update_id":900,"message":{"message_id":1,"date":1789310400,"chat":{"id":4242,"type":"private"},"text":"IEG0pxWr"}},
              {"update_id":901,"edited_message":{"message_id":1,"date":1789310460,"chat":{"id":4242,"type":"private"},"text":"x"}},
              {"update_id":902,"message":{"message_id":2,"date":1789310500,"chat":{"id":9999,"type":"private"}}}
            ]}
            """;
        var handler = CapturingHandler.Always(HttpStatusCode.OK, Updates);

        var replies = await NewNotifier(handler).ReadRepliesAsync(Token, 900);

        var (req, _) = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, req.Method);
        Assert.Equal($"/bot{Token}/getUpdates", req.RequestUri!.AbsolutePath);
        Assert.Contains("timeout=0", req.RequestUri.Query);
        Assert.Contains("offset=900", req.RequestUri.Query);
        Assert.Contains("allowed_updates=%5B%22message%22%5D", req.RequestUri.Query);
        // An edit is not a message; a message without text is still an update to confirm past.
        Assert.Equal(2, replies.Count);
        Assert.Equal(new TelegramReply(900, "4242", DateTimeOffset.FromUnixTimeSeconds(1789310400), "IEG0pxWr"), replies[0]);
        Assert.Equal(new TelegramReply(902, "9999", DateTimeOffset.FromUnixTimeSeconds(1789310500), ""), replies[1]);

        // No offset on the first poll of a parked run.
        await NewNotifier(handler).ReadRepliesAsync(Token, null);
        Assert.DoesNotContain("offset=", handler.Requests[1].Request.RequestUri!.Query);
    }

    [Fact]
    public async Task A_webhook_conflict_on_get_updates_is_a_refusal_without_the_token()
    {
        var handler = CapturingHandler.Always(HttpStatusCode.Conflict,
            "{\"ok\":false,\"description\":\"Conflict: can't use getUpdates method while webhook is active\"}");
        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() => NewNotifier(handler).ReadRepliesAsync(Token, null));
        Assert.Contains("webhook is active", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);
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

    [Fact]
    public void The_filled_moo_says_what_still_needs_the_person()
    {
        // A clean dry run is a click; one that stopped on required questions is a form to
        // finish, and the moo names the questions (#190). Semicolons separate them because a
        // label may carry a comma of its own.
        Assert.Equal("🐮 moo — Acme · Engineer is filled in and ready for you to click Apply",
            PacketReadyNotifier.BuildMessage("Acme", "Engineer", null, PacketReadyNotifier.Moment.Filled));
        Assert.Equal("🐮 moo — Acme · Engineer is filled in — 2 questions need you: expected monthly salary, database technologies (SQL, NoSQL)",
            PacketReadyNotifier.BuildMessage("Acme", "Engineer", null, PacketReadyNotifier.Moment.Filled,
                "expected monthly salary; database technologies (SQL, NoSQL)"));
        Assert.Equal("🐮 moo — Acme is filled in — 1 question needs you: notice period",
            PacketReadyNotifier.BuildMessage("Acme", "", null, PacketReadyNotifier.Moment.Filled, "notice period"));
    }
}
