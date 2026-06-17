// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api;

public static class TimeoutConfiguration
{
    public static int PositiveTimeoutSeconds(string? raw, int defaultValue) =>
        int.TryParse(raw, out var seconds) && seconds > 0 ? seconds : defaultValue;
}
