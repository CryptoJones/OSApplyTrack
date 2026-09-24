// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent.Browser;

namespace ApplyTrack.Api.Tests;

/// <summary>A native date picker takes only its ISO form: evlo's "earliest date you are
/// available" got "October 1, 2026" typed at it, and Playwright's "Malformed value" ended the run.</summary>
public class DateInputValueTests
{
    [Theory]
    [InlineData("October 1, 2026", "date", "2026-10-01")]
    [InlineData("10/1/2026", "date", "2026-10-01")]
    [InlineData("2026-10-01", "date", "2026-10-01")]
    [InlineData("October 1, 2026", "month", "2026-10")]
    [InlineData("October 1, 2026", "datetime-local", "2026-10-01T09:00")]
    [InlineData("2026-10-01T00:00", "datetime-local", "2026-10-01T00:00")]
    [InlineData("October 1, 2026 2:30 PM", "datetime-local", "2026-10-01T14:30")]
    [InlineData("Immediately", "date", null)]
    [InlineData("", "date", null)]
    public void An_answer_becomes_what_the_date_input_accepts(string answer, string type, string? expected) =>
        Assert.Equal(expected, BrowserSubmitter.DateInputValue(answer, type));
}
