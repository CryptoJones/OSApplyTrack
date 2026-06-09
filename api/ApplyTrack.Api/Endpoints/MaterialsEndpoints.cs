// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// The materials-engine settings routes: the per-tenant structured résumé
/// (<c>/api/resume</c>) that feeds the cover-letter prompt, and the per-tenant LLM
/// endpoint override (<c>/api/llm-settings</c>) layered over the instance default.
/// The API key is write-only — it is stored encrypted and never returned, only a
/// <c>has_api_key</c> flag. Cover-letter generation itself lives on
/// <c>POST /api/apps/{name}/draft</c> (AppsEndpoints); the matching DELETE to discard
/// a letter lives here.
/// </summary>
public static class MaterialsEndpoints
{
    public static void MapMaterialsEndpoints(this IEndpointRouteBuilder app)
    {
        // ---- Structured résumé -------------------------------------------------
        app.MapGet("/api/resume", async (ResumeRepo repo) =>
            Results.Ok(await repo.GetAsync()));

        app.MapPut("/api/resume", async (JsonElement payload, ResumeRepo repo) =>
        {
            var resume = Resume.FromJson(payload);
            await repo.UpsertAsync(resume);
            return Results.Ok(resume);
        });

        // ---- LLM endpoint settings --------------------------------------------
        app.MapGet("/api/llm-settings", async (
            LlmSettingsRepo repo, LlmOptions instance, SecretProtector protector) =>
        {
            var (baseUrl, model, hasKey) = await repo.GetViewAsync();
            return Results.Ok(new
            {
                base_url = baseUrl,
                model,
                has_api_key = hasKey,
                // Read-only view of the operator-set fallback so the UI can show what a
                // tenant inherits when they leave a field blank.
                instance = new
                {
                    base_url = instance.BaseUrl,
                    model = instance.Model,
                    has_api_key = instance.ApiKey.Length > 0,
                },
                // Whether this instance can store a per-tenant key at all.
                secrets_available = protector.Available,
            });
        });

        app.MapPut("/api/llm-settings", async (JsonElement payload, LlmSettingsRepo repo) =>
        {
            var baseUrl = GetString(payload, "base_url");
            var model = GetString(payload, "model");

            // Distinguish "api_key omitted" (leave the stored key alone) from
            // "api_key present" (set it, or clear it when blank).
            var changeKey = payload.ValueKind == JsonValueKind.Object
                && payload.TryGetProperty("api_key", out _);
            var newKey = changeKey ? GetString(payload, "api_key") : null;

            await repo.UpsertAsync(baseUrl, model, changeKey, newKey);

            var (savedUrl, savedModel, hasKey) = await repo.GetViewAsync();
            return Results.Ok(new { base_url = savedUrl, model = savedModel, has_api_key = hasKey });
        });

        // ---- Discard a generated cover letter ---------------------------------
        app.MapDelete("/api/apps/{name}/cover-letter", async (string name, CoverLetterRepo letters) =>
        {
            var removed = await letters.DeleteAsync(name);
            return removed
                ? Results.NoContent()
                : throw new AppNotFoundException($"no cover letter for '{name}'");
        });
    }

    private static string GetString(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()?.Trim() ?? ""
            : "";
}
