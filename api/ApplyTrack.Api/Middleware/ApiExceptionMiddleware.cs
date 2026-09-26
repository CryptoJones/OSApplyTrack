// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Middleware;

/// <summary>
/// Maps the domain exceptions to the same HTTP status + <c>{"detail": "..."}</c>
/// body FastAPI produced, so the SPA's <c>api()</c> helper (which reads
/// <c>(await res.json()).detail</c>) and its 409 overwrite-confirm flow keep working
/// unchanged: validation -> 400, not-found -> 404, conflict -> 409.
/// </summary>
public sealed class ApiExceptionMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ApiExceptionMiddleware> _logger;

    public ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (AppNotFoundException ex)
        {
            await WriteDetail(context, StatusCodes.Status404NotFound, ex.Message);
        }
        catch (AppConflictException ex)
        {
            await WriteDetail(context, StatusCodes.Status409Conflict, ex.Message);
        }
        catch (AppForbiddenException ex)
        {
            await WriteDetail(context, StatusCodes.Status403Forbidden, ex.Message);
        }
        catch (AppValidationException ex)
        {
            await WriteDetail(context, StatusCodes.Status400BadRequest, ex.Message);
        }
        catch (AppRateLimitedException ex)
        {
            await WriteDetail(context, StatusCodes.Status429TooManyRequests, ex.Message);
        }
        catch (LlmUnavailableException ex)
        {
            // Upstream LLM problem, not a client error: surface the (safe) message so
            // the SPA can toast something actionable instead of a generic 500.
            await WriteDetail(context, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (ScrapeUnavailableException ex)
        {
            // Same shape for a job board the scraper couldn't read.
            await WriteDetail(context, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (NotificationFailedException ex)
        {
            // And for a notification channel that refused the message.
            await WriteDetail(context, StatusCodes.Status502BadGateway, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client hung up mid-request. There is nobody left to answer and nothing went
            // wrong on our side, so this is not an unhandled error — logging it would turn
            // every closed tab into an outage signal. The scrape path reaches here on purpose
            // (#266): a disconnect propagates as cancellation instead of being disguised as a
            // board failure.
            _logger.LogDebug("Client aborted {Method} {Path}",
                context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            // Anything not a known domain exception: log the detail server-side, but
            // hand the client a generic {"detail"} 500 so internals never leak.
            _logger.LogError(ex, "Unhandled exception for {Method} {Path}",
                context.Request.Method, context.Request.Path);
            if (context.Response.HasStarted)
                throw;
            await WriteDetail(context, StatusCodes.Status500InternalServerError, "internal error");
        }
    }

    private static async Task WriteDetail(HttpContext context, int status, string detail)
    {
        if (context.Response.HasStarted)
            throw new InvalidOperationException(
                "Cannot write error body: the response has already started.");
        context.Response.Clear();
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { detail });
    }
}
