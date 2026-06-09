// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Llm;

/// <summary>
/// A minimal chat-completion abstraction over an OpenAI-compatible endpoint. One
/// system + one user message in, the assistant's text out. The implementation is
/// swappable, which lets the tests inject a deterministic stub instead of hitting a
/// real model.
/// </summary>
public interface ILlmClient
{
    /// <summary>
    /// Send a single-turn completion. Throws <see cref="Data.LlmUnavailableException"/>
    /// when the endpoint is unconfigured, unreachable, errors, or returns an
    /// unparseable response.
    /// </summary>
    Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, EffectiveLlmConfig cfg, CancellationToken ct = default);
}
