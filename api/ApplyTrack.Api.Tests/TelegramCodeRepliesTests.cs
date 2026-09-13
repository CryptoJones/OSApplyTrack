// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

/// <summary>A reply to the 🔐 moo is the code — only from the configured chat, only after
/// the moo, only in the shape the email shows; everything else is confirmed away and ignored.</summary>
public class TelegramCodeRepliesTests
{
    private static readonly TelegramTarget Target = new("123456:ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef", "4242");
    private static readonly DateTimeOffset Mooed = new(2026, 9, 13, 14, 40, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("w6Gx6jHG", "w6Gx6jHG")]
    [InlineData("  IEG0pxWr \n", "IEG0pxWr")]
    [InlineData("code: HpEq0DVP", "HpEq0DVP")]
    [InlineData("Here it is: 1234", "1234")]
    [InlineData("no spaces here", null)]
    [InlineData("abc", null)]
    [InlineData("toolongtobeacodeatall1", null)]
    [InlineData("", null)]
    public void Only_a_bare_code_or_a_code_after_a_colon_counts(string text, string? expected) =>
        Assert.Equal(expected, TelegramCodeReplies.CodeIn(text));

    [Fact]
    public async Task The_first_code_from_the_right_chat_after_the_moo_is_taken_and_the_inbox_is_confirmed()
    {
        var notifier = new CapturingNotifier();
        notifier.Replies.AddRange([
            new TelegramReply(10, "4242", Mooed.AddMinutes(-5), "w6Gx6jHG"),   // before the moo: stale
            new TelegramReply(11, "9999", Mooed.AddSeconds(30), "w6Gx6jHG"),   // wrong chat
            new TelegramReply(12, "4242", Mooed.AddSeconds(40), "thanks!"),    // not a code
            new TelegramReply(13, "4242", Mooed.AddSeconds(50), "IEG0pxWr"),   // this one
            new TelegramReply(14, "4242", Mooed.AddSeconds(60), "HpEq0DVP"),   // too late, never read as the answer
        ]);
        var replies = new TelegramCodeReplies(notifier, Target, Mooed, NullLogger.Instance);

        Assert.Equal("IEG0pxWr", await replies.PollAsync(CancellationToken.None));

        // The next poll confirms everything it has seen: offset = last update id + 1.
        notifier.Replies.Clear();
        Assert.Null(await replies.PollAsync(CancellationToken.None));
        Assert.Equal([null, 15L], notifier.Offsets);
    }

    [Fact]
    public async Task Nothing_yet_is_null_and_the_offset_still_advances_past_the_noise()
    {
        var notifier = new CapturingNotifier();
        notifier.Replies.Add(new TelegramReply(7, "4242", Mooed.AddSeconds(5), "on it"));
        var replies = new TelegramCodeReplies(notifier, Target, Mooed, NullLogger.Instance);
        Assert.Null(await replies.PollAsync(CancellationToken.None));
        notifier.Replies.Add(new TelegramReply(8, "4242", Mooed.AddSeconds(9), "w6Gx6jHG"));
        Assert.Equal("w6Gx6jHG", await replies.PollAsync(CancellationToken.None));
        Assert.Equal([null, 8L], notifier.Offsets);
    }

    [Fact]
    public async Task An_unreadable_bot_surfaces_as_the_notification_failure_it_is()
    {
        var notifier = new CapturingNotifier(new NotificationFailedException("Telegram refused the poll (Conflict: can't use getUpdates method while webhook is active)"));
        var replies = new TelegramCodeReplies(notifier, Target, Mooed, NullLogger.Instance);
        var ex = await Assert.ThrowsAsync<NotificationFailedException>(() => replies.PollAsync(CancellationToken.None));
        Assert.Contains("webhook", ex.Message);
    }
}
