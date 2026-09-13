// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The answer bank: every screening question the agent has met across application
/// forms, with the answer it gave, in one place. <c>GET /api/answers</c> lists them;
/// <c>PUT /api/answers/{key}</c> makes an answer the person's own (the drafter uses it
/// verbatim from then on) or, blank, hands the question back to the drafter;
/// <c>DELETE</c> forgets the question until a form asks it again.
/// </summary>
public static class AnswersEndpoints
{
    public static void MapAnswersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/answers", async (AnswerBankRepo bank) => Results.Ok(await bank.ListAsync()));

        app.MapPut("/api/answers/{key}", async (string key, JsonElement payload, AnswerBankRepo bank) =>
        {
            var answer = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("answer", out var a) && a.ValueKind == JsonValueKind.String
                ? (a.GetString() ?? "").Trim() : "";
            InputLimits.Text("answer", answer, InputLimits.PacketAnswer);
            if (!await bank.SetAsync(key, answer))
                throw new AppNotFoundException("no such question in the answer bank");
            return Results.Ok(await bank.GetAsync(key));
        });

        app.MapDelete("/api/answers/{key}", async (string key, AnswerBankRepo bank) =>
            await bank.DeleteAsync(key) ? Results.NoContent() : throw new AppNotFoundException("no such question in the answer bank"));
    }
}
