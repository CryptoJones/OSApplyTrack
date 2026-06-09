// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Llm;

/// <summary>
/// Calls an OpenAI-compatible <c>POST {base_url}/chat/completions</c> endpoint. The
/// same request shape is spoken by OpenAI, OpenRouter, Together, Groq, vLLM, Ollama,
/// LM Studio, and Anthropic's OpenAI-compat shim — so the operator picks the
/// provider (or a free local model) purely through config. A fresh client per call
/// lets each request honor the (possibly per-tenant) base URL and timeout.
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly IHttpClientFactory _factory;
    private readonly ILogger<OpenAiCompatibleLlmClient> _log;

    public OpenAiCompatibleLlmClient(IHttpClientFactory factory, ILogger<OpenAiCompatibleLlmClient> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task<string> CompleteAsync(
        string systemPrompt, string userPrompt, EffectiveLlmConfig cfg, CancellationToken ct = default)
    {
        if (!cfg.IsConfigured)
            throw new LlmUnavailableException(
                "no LLM endpoint is configured — set one in AI settings or ask the operator to set Llm__BaseUrl/Llm__Model");

        var url = cfg.BaseUrl.TrimEnd('/') + "/chat/completions";
        var payload = new
        {
            model = cfg.Model,
            temperature = 0.6,
            stream = false,
            messages = new[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt },
            },
        };

        var http = _factory.CreateClient("llm");
        http.Timeout = TimeSpan.FromSeconds(cfg.TimeoutSeconds);

        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(payload) };
        if (!string.IsNullOrEmpty(cfg.ApiKey))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cfg.ApiKey);

        HttpResponseMessage res;
        try
        {
            res = await http.SendAsync(req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // genuine caller cancellation, not a timeout
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _log.LogWarning(ex, "LLM request to {Url} failed", url);
            throw new LlmUnavailableException($"could not reach the LLM endpoint ({ex.Message})");
        }

        if (!res.IsSuccessStatusCode)
        {
            var detail = await SafeReadAsync(res, ct);
            _log.LogWarning("LLM endpoint {Url} returned {Status}: {Detail}", url, (int)res.StatusCode, detail);
            throw new LlmUnavailableException($"the LLM endpoint returned HTTP {(int)res.StatusCode}");
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var content = doc.RootElement
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? "";
            return content.Trim();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            _log.LogWarning(ex, "Unexpected LLM response shape from {Url}", url);
            throw new LlmUnavailableException("the LLM endpoint returned an unexpected response shape");
        }
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            return body.Length > 500 ? body[..500] : body;
        }
        catch
        {
            return "(no body)";
        }
    }
}
