// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The Errors view (#284): the applications that are stuck. A failed browser run left its
/// application in <c>ready</c> with nothing to tell it from a packet nobody had tried —
/// 22 failures hid inside a Ready count of 33 on 2026-09-19. This lists them on their own,
/// with what went wrong and, above all, <b>what happens next</b>: an automatic retry and
/// when, or that it is the person's and why. A derived view, not a status — the status
/// list is part of the schema both runtimes share.
/// </summary>
public static class ErrorsEndpoints
{
    /// <summary>What happens to an errored application from here.</summary>
    public static class Next
    {
        /// <summary>The agent will try again by itself (#274), at <c>retry_at</c>.</summary>
        public const string Retry = "retry";
        /// <summary>Nothing more will happen until the person acts.</summary>
        public const string You = "you";
    }

    /// <summary>One stuck application, as the SPA lists it.</summary>
    public sealed record ErrorRow(
        string Name, string Company, string Role, string Link, string Provider,
        string Error, DateTimeOffset At, int Runs, bool Captcha,
        string Next, DateTimeOffset? RetryAt, string Why);

    public static void MapErrorsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/errors", async (AgentEvidenceRepo evidence, AgentSettingsRepo agentSettings) =>
        {
            var settings = await agentSettings.GetAsync();
            var rows = (await evidence.ErroredAsync(ReadyReconciler.Window))
                .Select(e => Describe(e, settings.LongTail, settings.LinkedInEasy)).ToList();
            return Results.Ok(new
            {
                count = rows.Count,
                retrying = rows.Count(r => r.Next == Next.Retry),
                needs_you = rows.Count(r => r.Next == Next.You),
                // The global JSON policy snake-cases the record members (retry_at, …).
                errors = rows,
            });
        });
    }

    /// <summary>The row for one errored application: the same rules the reconciler applies,
    /// read ahead of time so the person is told what it will do. Public for tests.</summary>
    public static ErrorRow Describe(AgentEvidenceRepo.Errored e, bool longTail, bool linkedInEasy = false)
    {
        var provider = AtsProvider.Detect(e.Link, e.Source);
        var error = Text(e.Detail, "error");
        if (error.Length == 0) error = Text(e.Detail, "reason");
        var captcha = e.Detail.ValueKind == JsonValueKind.Object
            && e.Detail.TryGetProperty("captcha", out var c) && c.ValueKind == JsonValueKind.True;

        string next = Next.You, why;
        DateTimeOffset? retryAt = null;
        if (captcha)
            why = "the form is guarded by a captcha — finish it with Copy answers and open";
        else if (error.Contains("Submit was clicked", StringComparison.Ordinal))
            why = "Submit was clicked and the board never confirmed — it may have gone through, so it is not retried; check the screenshot and your email";
        else if (e.Link.Length == 0 || !AtsProvider.BrowserCanSubmit(provider, longTail, linkedInEasy))
            why = e.Link.Length == 0 ? "no posting link" : SubmitEndpoints.CannotDrive(provider);
        else if (ReadyReconciler.WaitedOnSession(AgentEvidenceRepo.Kinds.Failed, e.Detail, "linkedin.com"))
            why = "waiting for the agent's LinkedIn sign-in — tap Yes in the LinkedIn app when the 🔐 moo arrives, or press Sign in now under Settings · Agent · Board accounts; it runs again by itself once the session is kept";
        else if (!ReadyReconciler.IsTransientFailure(AgentEvidenceRepo.Kinds.Failed, e.Detail))
            why = "the same thing will happen on another run — it needs you, or a fix";
        else if (e.RecentFailures >= ReadyReconciler.MaxFailures)
            why = $"gave up after {e.RecentFailures} failed runs in {ReadyReconciler.Window.Days} days";
        else
        {
            next = Next.Retry;
            retryAt = e.At + ReadyReconciler.RetryAfter;
            why = $"failed on something transient — retried automatically, attempt {e.RecentFailures + 1} of {ReadyReconciler.MaxFailures}";
        }

        return new ErrorRow(e.ApplicationName,
            e.Company.Length > 0 ? e.Company : Slug.NameStem(e.ApplicationName), e.Role, e.Link, provider,
            error, e.At, e.Runs, captcha, next, retryAt, why);
    }

    private static string Text(JsonElement detail, string name) =>
        detail.ValueKind == JsonValueKind.Object && detail.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
