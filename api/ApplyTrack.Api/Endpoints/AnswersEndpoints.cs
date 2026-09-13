// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The answer bank: every screening question the agent has met across application
/// forms, with the answer it gave, in one place. <c>GET /api/answers</c> lists them;
/// <c>PUT /api/answers/{key}</c> makes an answer the person's own (the drafter uses it
/// verbatim from then on) or, blank, hands the question back to the drafter;
/// <c>POST /api/answers/{key}/apply</c> writes the person's answer into every Ready
/// packet that asks the same question, no model; <c>DELETE</c> forgets the question
/// until a form asks it again.
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

        // Saving an answer changed what the agent says from now on but left every built
        // packet as it was, and re-preparing costs a model call per packet. This refills
        // the one answer into every Ready packet asking the same question (#189).
        app.MapPost("/api/answers/{key}/apply", async (string key, AnswerBankRepo bank, AgentPacketRepo packets) =>
        {
            var entry = await bank.GetAsync(key)
                ?? throw new AppNotFoundException("no such question in the answer bank");
            if (entry.Source != AnswerBankEntry.Human || entry.Answer.Length == 0)
                throw new AppValidationException("save the answer as yours first — only your own answer is written into packets");
            var updated = new List<string>();
            foreach (var packet in await packets.ListReadyAsync())
            {
                var changes = new Dictionary<string, string>();
                foreach (var q in packet.Questions)
                {
                    if (!AnswerBankRepo.Bankable(q) || AnswerBankRepo.KeyFor(q) != entry.Key)
                        continue;
                    var (fitted, _) = AnswerDrafter.FitToOptions(q, entry.Answer);
                    if (fitted is null)
                        continue; // not one of this form's options — the person picks on the packet
                    if (packet.Answers.TryGetValue(q.Id, out var current) && current == fitted)
                        continue;
                    changes[q.Id] = fitted;
                }
                if (changes.Count == 0)
                    continue;
                await packets.UpdateAnswersAsync(packet.ApplicationName, changes, null);
                updated.Add(packet.ApplicationName);
            }
            return Results.Ok(new { updated = updated.Count, packets = updated });
        });

        app.MapDelete("/api/answers/{key}", async (string key, AnswerBankRepo bank) =>
            await bank.DeleteAsync(key) ? Results.NoContent() : throw new AppNotFoundException("no such question in the answer bank"));
    }
}
