// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

namespace ApplyTrack.Api.Llm;

/// <summary>
/// Instance-wide LLM defaults, bound from the <c>Llm</c> config section
/// (env <c>Llm__BaseUrl</c> / <c>Llm__Model</c> / <c>Llm__ApiKey</c>). The endpoint
/// is an OpenAI-compatible chat-completions server, so the same code points at a
/// local model (Ollama / vLLM / LM Studio) or any hosted provider. A local model
/// means zero per-draft cost and the résumé never leaves the box.
/// </summary>
public sealed class LlmOptions
{
    /// <summary>OpenAI-compatible base URL, e.g. <c>http://localhost:11434/v1</c>.</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>Model id, e.g. <c>llama3.1</c> or <c>gpt-4o-mini</c>.</summary>
    public string Model { get; set; } = "";

    /// <summary>Bearer key; blank for a keyless local endpoint.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>How long to wait on the model before giving up.</summary>
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>A tenant's stored override (key already decrypted). Blank fields inherit the instance default.</summary>
public sealed record LlmOverride(string BaseUrl, string Model, string? ApiKey);

/// <summary>
/// The endpoint settings actually used for a draft — a tenant's override merged
/// over the instance defaults, field by field (a tenant may override just the
/// model and keep the instance URL).
/// </summary>
public sealed record EffectiveLlmConfig(string BaseUrl, string Model, string? ApiKey, int TimeoutSeconds)
{
    /// <summary>True once there is somewhere to send the request and a model to ask for.</summary>
    public bool IsConfigured => BaseUrl.Length > 0 && Model.Length > 0;

    public static EffectiveLlmConfig Resolve(LlmOptions instance, LlmOverride? ovr)
    {
        string Pick(string? over, string fallback) =>
            string.IsNullOrWhiteSpace(over) ? fallback.Trim() : over.Trim();

        var apiKey = !string.IsNullOrEmpty(ovr?.ApiKey)
            ? ovr.ApiKey
            : string.IsNullOrEmpty(instance.ApiKey) ? null : instance.ApiKey;

        return new EffectiveLlmConfig(
            Pick(ovr?.BaseUrl, instance.BaseUrl),
            Pick(ovr?.Model, instance.Model),
            apiKey,
            instance.TimeoutSeconds);
    }
}
