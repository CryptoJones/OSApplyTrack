// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Llm;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped read/write of the single <c>llm_settings</c> row — a tenant's
/// override of the instance-default LLM endpoint. The API key is encrypted at rest
/// via <see cref="SecretProtector"/> and is never returned to the client (only a
/// <c>has_api_key</c> flag). Blank base_url/model mean "inherit the instance
/// default", resolved later by <see cref="EffectiveLlmConfig.Resolve"/>.
/// </summary>
public sealed class LlmSettingsRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;

    public LlmSettingsRepo(IDbConnection conn, long tenantId, SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    private sealed record Row(string BaseUrl, string Model, string ApiKeyCiphertext, bool CoverLettersEnabled);

    /// <summary>The tenant's override with the API key decrypted, or null when no row exists.</summary>
    public async Task<LlmOverride?> GetOverrideAsync()
    {
        var row = await ReadRowAsync();
        if (row is null)
            return null;

        string? key = null;
        if (row.ApiKeyCiphertext.Length > 0 && _protector.Available)
        {
            try
            {
                key = _protector.Unprotect(row.ApiKeyCiphertext);
            }
            catch (CryptographicException)
            {
                // Master key rotated/changed since this was stored: the key is no longer
                // recoverable. Fall back to the instance default rather than hard-fail.
                key = null;
            }
        }
        return new LlmOverride(row.BaseUrl, row.Model, key);
    }

    /// <summary>The client-safe view: base_url, model, whether a key is stored (never the
    /// key itself), and the cover-letter toggle. No row means all defaults, toggle ON.</summary>
    public async Task<(string BaseUrl, string Model, bool HasApiKey, bool CoverLettersEnabled)> GetViewAsync()
    {
        var row = await ReadRowAsync();
        return row is null
            ? ("", "", false, true)
            : (row.BaseUrl, row.Model, row.ApiKeyCiphertext.Length > 0, row.CoverLettersEnabled);
    }

    /// <summary>
    /// Save the override. <paramref name="changeKey"/> distinguishes "leave the stored
    /// key alone" (false) from "set/clear it" (true): a blank <paramref name="newKeyPlaintext"/>
    /// with changeKey clears it, a non-blank one replaces it. Storing a key requires a
    /// configured master key. <paramref name="coverLettersEnabled"/> follows the same
    /// pattern: null leaves the stored toggle alone (a new row defaults to ON).
    /// </summary>
    public async Task UpsertAsync(
        string baseUrl, string model, bool changeKey, string? newKeyPlaintext,
        bool? coverLettersEnabled = null, IDbTransaction? tx = null)
    {
        var ciphertext = "";
        if (changeKey && !string.IsNullOrEmpty(newKeyPlaintext))
        {
            if (!_protector.Available)
                throw new AppValidationException(
                    "this instance can't store a per-tenant API key (operator must set APPLYTRACK_SECRETS_KEY); "
                    + "the instance-default endpoint still works");
            ciphertext = _protector.Protect(newKeyPlaintext);
        }

        // When not changing the key, leave api_key_ciphertext untouched on conflict.
        var keyUpdate = changeKey ? "api_key_ciphertext = EXCLUDED.api_key_ciphertext," : "";
        await _conn.ExecuteAsync(
            $"""
             INSERT INTO llm_settings (tenant_id, base_url, model, api_key_ciphertext, cover_letters_enabled, updated_at)
             VALUES (@t, @baseUrl, @model, @ciphertext, coalesce(@enabled, true), now())
             ON CONFLICT (tenant_id) DO UPDATE SET
                 base_url   = EXCLUDED.base_url,
                 model      = EXCLUDED.model,
                 {keyUpdate}
                 cover_letters_enabled = coalesce(@enabled, llm_settings.cover_letters_enabled),
                 updated_at = now()
             """,
            new { t = _t, baseUrl = baseUrl.Trim(), model = model.Trim(), ciphertext, enabled = coverLettersEnabled },
            tx);
    }

    private Task<Row?> ReadRowAsync() =>
        // Alias to separator-free names so the record maps without depending on
        // Dapper's global MatchNamesWithUnderscores flag.
        _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT base_url AS baseurl, model, api_key_ciphertext AS apikeyciphertext, "
            + "cover_letters_enabled AS coverlettersenabled "
            + "FROM llm_settings WHERE tenant_id = @t",
            new { t = _t });
}
