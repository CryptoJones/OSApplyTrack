// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Llm;

/// <summary>
/// Asks the model for one JSON object and turns it into a typed record. Sits beside
/// <see cref="ILlmClient"/> rather than changing it: the client stays single-turn with
/// no tool-calling, and — the any-LLM pin — nothing here depends on
/// <c>response_format</c>, which OpenAI-compatible servers implement inconsistently.
/// Instead the prompt asks for JSON, the reply is scanned for the first balanced
/// object (tolerating prose and code fences around it), and a reply that does not
/// parse or validate is re-prompted <b>once</b> with the error. After that it gives
/// up with <see cref="LlmUnavailableException"/>: a model that cannot produce a
/// verdict must never fall through to "proceed anyway".
/// </summary>
public sealed class StructuredCompleter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly ILlmClient _llm;

    public StructuredCompleter(ILlmClient llm) => _llm = llm;

    /// <summary>
    /// Complete and parse. <paramref name="validate"/> returns null when the parsed
    /// value is acceptable, else the problem to hand back to the model on the retry.
    /// </summary>
    public async Task<T> CompleteAsync<T>(
        string systemPrompt, string userPrompt, EffectiveLlmConfig cfg,
        Func<T, string?> validate, CancellationToken ct = default)
    {
        var system = systemPrompt.TrimEnd() + "\n\n" + JsonInstruction;
        var reply = await _llm.CompleteAsync(system, userPrompt, cfg, ct);
        var (value, error) = TryParse(reply, validate);
        if (value is not null)
            return value;

        // One repair attempt, quoting the failure. The original user prompt is kept
        // so the model has everything it needs to answer from scratch.
        var repair = userPrompt.TrimEnd()
            + "\n\nYour previous reply could not be used: " + error
            + "\nReply again with ONLY the JSON object, nothing before or after it.";
        reply = await _llm.CompleteAsync(system, repair, cfg, ct);
        (value, error) = TryParse(reply, validate);
        if (value is not null)
            return value;
        throw new LlmUnavailableException("the model did not return a usable answer (" + error + ")");
    }

    private const string JsonInstruction =
        "Respond with a single JSON object and nothing else — no prose before or after "
        + "it, no Markdown code fence. Use exactly the field names given.";

    /// <summary>How many candidate objects to try before giving up on a reply — a
    /// thinking model's scratchpad can carry a few brace pairs before the answer.</summary>
    private const int MaxCandidates = 8;

    private static (T? Value, string Error) TryParse<T>(string reply, Func<T, string?> validate)
    {
        var error = "no JSON object was found in the reply";
        var from = 0;
        for (var attempt = 0; attempt < MaxCandidates; attempt++)
        {
            var json = ExtractFirstObject(reply, from);
            if (json is null)
                break;
            from = reply.IndexOf('{', from) + 1;
            T? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize<T>(json, Json);
            }
            catch (JsonException ex)
            {
                // Not JSON (a brace pair in prose, or a thinking block) — look further.
                error = "the JSON did not parse: " + ex.Message;
                continue;
            }
            if (parsed is null)
            {
                error = "the JSON object was empty";
                continue;
            }
            var problem = validate(parsed);
            if (problem is null)
                return (parsed, "");
            error = problem;
        }
        return (default, error);
    }

    /// <summary>
    /// The first balanced <c>{…}</c> in the text at or after <paramref name="from"/>,
    /// string-aware so braces inside JSON strings do not end the scan early. Null when
    /// there is none. Public for tests.
    /// </summary>
    public static string? ExtractFirstObject(string text, int from = 0)
    {
        var start = text.IndexOf('{', from);
        if (start < 0)
            return null;
        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '{': depth++; break;
                case '}':
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                    break;
            }
        }
        return null;
    }
}
