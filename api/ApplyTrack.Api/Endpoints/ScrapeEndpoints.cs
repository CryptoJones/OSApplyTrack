// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Scrape;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// <c>POST /api/scrape</c> — fetch a job-posting URL server-side (the browser can't,
/// because of CORS) and extract lead fields for the editor's Autofill button. Sits
/// behind the tenancy middleware like every other <c>/api</c> route, and behind its
/// own rate limit because each call fans out an outbound request.
/// </summary>
public static class ScrapeEndpoints
{
    public static void MapScrapeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/scrape", async (
            ScrapeRequest payload, JobPageFetcher fetcher, CancellationToken ct) =>
        {
            var url = payload.Url?.Trim() ?? "";
            if (url.Length == 0)
                throw new AppValidationException("a url is required");
            var (html, finalUrl) = await fetcher.FetchAsync(url, ct);
            return Results.Ok(JobPostingParser.Parse(html, finalUrl));
        }).RequireRateLimiting("scrape").RequireRequestSizeLimit(64L * 1024);
    }

    public sealed record ScrapeRequest(string? Url);
}
