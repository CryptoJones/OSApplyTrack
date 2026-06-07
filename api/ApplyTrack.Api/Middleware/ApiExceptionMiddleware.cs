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

    public ApiExceptionMiddleware(RequestDelegate next) => _next = next;

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
        catch (AppValidationException ex)
        {
            await WriteDetail(context, StatusCodes.Status400BadRequest, ex.Message);
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
