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

    /// <summary>Seconds between heartbeats (<c>agent_workers.seen_at</c>). Its own timer,
    /// independent of both lanes, so a worker without a browser is not read as absent for
    /// two minutes out of every five (#184). Must stay well inside
    /// <see cref="Data.AgentWorkerRegistry.Freshness"/>.</summary>
    public int HeartbeatSeconds { get; set; } = 30;

    /// <summary>How long a real run stays parked on the board's emailed security code before
    /// giving up — the code's own validity is about ten minutes. Short in tests.</summary>
    public int SecurityCodeWaitSeconds { get; set; } = 480;

    /// <summary>The worker's own, small connection pool — it holds a connection across
    /// model calls and must not be able to starve the request pool.</summary>
    public int MaxPoolSize { get; set; } = 4;
}
