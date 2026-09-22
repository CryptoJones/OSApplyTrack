// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The candidate's own accounts on ATSs that only take applications from a signed-in
/// candidate — SAP SuccessFactors (#216): <c>GET /api/board-accounts</c> lists host and
/// username (never the password, only <c>has_password</c>), <c>PUT</c> saves one with a
/// write-only password, <c>DELETE /api/board-accounts/{host}</c> forgets it. The browser
/// signs in with the matching account when Apply leads to that host and the page asks
/// for a password.
/// </summary>
public static class BoardAccountEndpoints
{
    public static void MapBoardAccountEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/board-accounts", async (BoardAccountRepo repo) =>
            Results.Ok((await repo.ListAsync()).Select(View)));

        app.MapPut("/api/board-accounts", async (JsonElement payload, BoardAccountRepo repo) =>
        {
            if (payload.ValueKind != JsonValueKind.Object)
                throw new AppValidationException("expected an object with host, username and password");
            var host = payload.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String ? (h.GetString() ?? "").Trim() : "";
            var username = payload.TryGetProperty("username", out var u) && u.ValueKind == JsonValueKind.String ? (u.GetString() ?? "").Trim() : "";
            // Omitted: keep the stored password. Present (blank included): set or clear it.
            string? password = payload.TryGetProperty("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : null;
            InputLimits.Text("host", host, 253);
            InputLimits.Text("username", username, 320);
            if (password is { Length: > 0 }) InputLimits.Text("password", password, 512);
            if (username.Length == 0)
                throw new AppValidationException("the username (usually your email address on that site) is required");
            await repo.UpsertAsync(host, username, password);
            return Results.Ok((await repo.ListAsync()).Select(View));
        });

        app.MapDelete("/api/board-accounts/{host}", async (string host, BoardAccountRepo repo) =>
            await repo.DeleteAsync(host) ? Results.NoContent() : Results.NotFound());

        // "Sign in now" for a signed-in source's account: the agent renews the kept session on
        // its next tick rather than its own clock, so the LinkedIn app's tap or the mailed PIN
        // is asked for while the person has their phone in hand. 202: the worker does the
        // signing in, in its browser; the api container has none.
        app.MapPost("/api/board-accounts/{host}/renew", async (string host, BoardAccountRepo repo) =>
            await repo.RequestRenewalAsync(host)
                ? Results.Accepted(null, (await repo.ListAsync()).Select(View))
                : Results.NotFound());
    }

    private static object View(BoardAccountView v) =>
        new
        {
            host = v.Host, username = v.Username, has_password = v.HasPassword, updated_at = v.UpdatedAt,
            keeps_session = v.KeepsSession, session_expires_at = v.SessionExpiresAt, renew_requested_at = v.RenewRequestedAt,
        };
}
