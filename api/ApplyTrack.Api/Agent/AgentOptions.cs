// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Agent;

/// <summary>
/// Instance-wide switch for the agent worker, bound from the <c>Agent</c> config
/// section (env <c>Agent__Enabled</c> / <c>Agent__IntervalSeconds</c>). The worker is
/// registered only when <see cref="Enabled"/> is true; the intended deployment is a
/// second container from the same image with this set and no published port, so
/// request handling and multi-minute model calls never share a process.
/// </summary>
public sealed class AgentOptions
{
    public bool Enabled { get; set; }

    /// <summary>Seconds between passes over the enabled tenants.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Seconds between drains of the submit queue (the human's Submit clicks).</summary>
    public int SubmitPollSeconds { get; set; } = 15;

    /// <summary>The worker's own, small connection pool — it holds a connection across
    /// model calls and must not be able to starve the request pool.</summary>
    public int MaxPoolSize { get; set; } = 4;
}
