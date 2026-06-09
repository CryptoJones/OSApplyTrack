// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Deterministic <see cref="ILlmClient"/> for tests: returns a canned body (or one
/// computed from the prompts) without touching the network, and records the last
/// call so a test can assert what the drafter actually sent the model. Pass a custom
/// responder to exercise short/long/garbage replies.
/// </summary>
internal sealed class StubLlmClient : ILlmClient
{
    // A plausible letter body — comfortably inside the drafter's [40, 6000] gate.
    public const string DefaultBody =
        "Dear Hiring Team,\n\nThis is a generated cover letter produced for testing. "
        + "It says enough plausible things to clear the drafter's length gate without "
        + "asserting any facts.\n\nSincerely,\nThe Candidate";

    private readonly Func<string, string, EffectiveLlmConfig, string> _respond;

    public StubLlmClient(Func<string, string, EffectiveLlmConfig, string>? respond = null) =>
        _respond = respond ?? ((_, _, _) => DefaultBody);

    public string? LastSystemPrompt { get; private set; }
    public string? LastUserPrompt { get; private set; }
    public EffectiveLlmConfig? LastConfig { get; private set; }
    public int Calls { get; private set; }

    public Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, EffectiveLlmConfig cfg, CancellationToken ct = default)
    {
        Calls++;
        LastSystemPrompt = systemPrompt;
        LastUserPrompt = userPrompt;
        LastConfig = cfg;
        return Task.FromResult(_respond(systemPrompt, userPrompt, cfg));
    }
}
