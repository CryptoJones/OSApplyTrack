// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics.Metrics;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Observability;

/// <summary>
/// The app's own instruments (#359), on the <c>ApplyTrack</c> meter. Recording costs next
/// to nothing when nobody listens, so they are always on; they only leave the process when
/// the operator turns on OpenTelemetry or the <c>/metrics</c> port (<see cref="Telemetry"/>).
/// No instrument carries a tenant, an email, a company or a URL: every tag is a small,
/// fixed vocabulary (an ATS provider, an outcome kind, an exception kind), so a metric can
/// never say whose data it counted.
/// </summary>
public static class AppMetrics
{
    public const string MeterName = "ApplyTrack";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Histogram<double> LlmDuration = Meter.CreateHistogram<double>(
        "applytrack.llm.duration", "s", "Wall time of each completion call to the OpenAI-compatible endpoint.");

    private static readonly Counter<long> LlmErrors = Meter.CreateCounter<long>(
        "applytrack.llm.errors", "{call}", "Completion calls that failed, by error kind.");

    private static readonly Counter<long> AgentRuns = Meter.CreateCounter<long>(
        "applytrack.agent.runs", "{run}", "Browser runs the agent finished, by ATS provider, outcome and dry run.");

    // -1 until the agent worker has counted once: a process with no worker reports nothing.
    private static long _submitQueueDepth = -1;

    private static readonly ObservableGauge<long> SubmitQueue = Meter.CreateObservableGauge(
        "applytrack.submit_requests.pending",
        () => Interlocked.Read(ref _submitQueueDepth) is var n and >= 0
            ? [new Measurement<long>(n)]
            : Array.Empty<Measurement<long>>(),
        "{request}", "Submit requests not yet done, across the instance.");

    /// <summary>Whether anything collects the queue gauge — so the worker only counts the
    /// queue when the count goes somewhere.</summary>
    public static bool SubmitQueueObserved => SubmitQueue.Enabled;

    public static void SetSubmitQueueDepth(long pending) => Interlocked.Exchange(ref _submitQueueDepth, pending);

    /// <summary>One completion call. <paramref name="endpoint"/> is <c>operator</c> or <c>tenant</c>
    /// (whose base URL it was), never the URL itself.</summary>
    public static void RecordLlmCall(string endpoint, TimeSpan elapsed, Exception? error)
    {
        var outcome = error is null ? "ok" : "error";
        LlmDuration.Record(elapsed.TotalSeconds,
            new KeyValuePair<string, object?>("endpoint", endpoint),
            new KeyValuePair<string, object?>("outcome", outcome));
        if (error is not null)
            LlmErrors.Add(1,
                new KeyValuePair<string, object?>("endpoint", endpoint),
                new KeyValuePair<string, object?>("error.type", ErrorKind(error)));
    }

    /// <summary>A small fixed vocabulary, not the message: a message can carry a URL.</summary>
    public static string ErrorKind(Exception error) => error switch
    {
        AppValidationException => "refused",
        OperationCanceledException => "timeout",
        LlmUnavailableException => "unavailable",
        _ => "other",
    };

    /// <summary>One finished browser run: <paramref name="outcome"/> is the evidence kind
    /// (<c>submitted</c>, <c>dry_run</c>, <c>failed</c>) or <c>closed</c>, <c>captcha</c>, <c>crashed</c>.</summary>
    public static void RecordAgentRun(string provider, string outcome, bool dryRun) =>
        AgentRuns.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("dry_run", dryRun));
}
