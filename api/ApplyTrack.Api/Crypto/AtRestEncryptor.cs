// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Security.Cryptography;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Crypto;

/// <summary>
/// The startup sweep that makes encryption at rest true for a database that predates it,
/// and that finishes a key rotation. Runs in the migrating container, after the schema is
/// current, and is safe to re-run: every column it covers is rewritten only when its value
/// is not already sealed under the <i>current</i> key — plaintext left by an older release,
/// or ciphertext under a previous key. A row that cannot be read (written under a key this
/// instance does not have) is logged and left alone rather than failing the boot; the API
/// still comes up, and that row's owner re-enters the value.
///
/// Covered: <c>resume_profiles.source_pdf</c>, <c>cover_letters.body</c>,
/// <c>agent_packets.answers</c> / <c>posting_excerpt</c>, <c>agent_evidence.screenshot</c>,
/// and the two columns that were already encrypted (<c>llm_settings.api_key_ciphertext</c>,
/// <c>notification_settings.telegram_bot_token_ciphertext</c>) so they too move to the
/// current key. Not covered, by design: what the list and the poller query — company, role,
/// status, score, dates, application notes — and the login email.
/// </summary>
public static class AtRestEncryptor
{
    public sealed record Report(int Sealed, int Unreadable);

    /// <param name="onlyTenant">Restrict the sweep to one tenant's rows (tests; a per-tenant re-seal). Null = everyone.</param>
    public static async Task<Report> SweepAsync(
        string connectionString, SecretProtector protector, ILogger log, CancellationToken ct = default, long? onlyTenant = null)
    {
        if (!protector.Available)
            return new Report(0, 0);
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        int sealedRows = 0, unreadable = 0;
        void Tally(Report r) { sealedRows += r.Sealed; unreadable += r.Unreadable; }
        var scope = onlyTenant is null ? "" : $" AND tenant_id = {onlyTenant.Value}";

        Tally(await TextAsync(conn, protector, log, "cover_letters", "body", ["tenant_id", "application_name"], legacyIsCiphertext: false, scope));
        Tally(await TextAsync(conn, protector, log, "agent_packets", "posting_excerpt", ["tenant_id", "application_name"], legacyIsCiphertext: false, scope));
        Tally(await TextAsync(conn, protector, log, "llm_settings", "api_key_ciphertext", ["tenant_id"], legacyIsCiphertext: true, scope));
        Tally(await TextAsync(conn, protector, log, "notification_settings", "telegram_bot_token_ciphertext", ["tenant_id"], legacyIsCiphertext: true, scope));
        Tally(await AnswersAsync(conn, protector, log, scope));
        Tally(await BytesAsync(conn, protector, log, "resume_profiles", "source_pdf", ["tenant_id"], scope));
        Tally(await BytesAsync(conn, protector, log, "agent_evidence", "screenshot", ["id"], scope));

        if (sealedRows > 0 || unreadable > 0)
            log.LogInformation("encryption at rest: sealed {Sealed} value(s) under the current key; {Unreadable} could not be read", sealedRows, unreadable);
        return new Report(sealedRows, unreadable);
    }

    /// <summary>A text column. Legacy (unprefixed) values are plaintext for the newly covered
    /// columns and bare ciphertext for the two that were encrypted before the token carried a
    /// fingerprint.</summary>
    private static async Task<Report> TextAsync(
        NpgsqlConnection conn, SecretProtector p, ILogger log, string table, string column, string[] keys, bool legacyIsCiphertext, string scope)
    {
        var rows = await conn.QueryAsync(
            $"SELECT {string.Join(", ", keys)}, {column} AS value FROM {table} WHERE {column} <> '' AND {column} NOT LIKE @cur{scope}",
            new { cur = p.CurrentTextPattern });
        int sealedRows = 0, unreadable = 0;
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            var value = (string)dict["value"];
            string plain;
            try
            {
                plain = SecretProtector.IsProtected(value) || legacyIsCiphertext ? p.Unprotect(value) : value;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                unreadable++;
                log.LogWarning("encryption at rest: {Table}.{Column} for {Key} could not be read ({Reason}); left as is",
                    table, column, KeyText(dict, keys), ex.Message);
                continue;
            }
            await conn.ExecuteAsync($"UPDATE {table} SET {column} = @v WHERE {Where(keys)}", Params(dict, keys, "v", p.Protect(plain)));
            sealedRows++;
        }
        return new Report(sealedRows, unreadable);
    }

    /// <summary>agent_packets.answers is jsonb: an object while plaintext, a JSON string once sealed.</summary>
    private static async Task<Report> AnswersAsync(NpgsqlConnection conn, SecretProtector p, ILogger log, string scope)
    {
        var rows = await conn.QueryAsync(
            "SELECT tenant_id, application_name, answers::text AS value FROM agent_packets "
            + "WHERE (jsonb_typeof(answers) <> 'string' OR (answers #>> '{}') NOT LIKE @cur)" + scope,
            new { cur = p.CurrentTextPattern });
        int sealedRows = 0, unreadable = 0;
        string[] keys = ["tenant_id", "application_name"];
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            string plainJson;
            try { plainJson = ProtectedJson.Read((string)dict["value"], p); }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                unreadable++;
                log.LogWarning("encryption at rest: agent_packets.answers for {Key} could not be read ({Reason}); left as is", KeyText(dict, keys), ex.Message);
                continue;
            }
            await conn.ExecuteAsync("UPDATE agent_packets SET answers = to_jsonb(@v::text) WHERE " + Where(keys),
                Params(dict, keys, "v", p.Protect(plainJson)));
            sealedRows++;
        }
        return new Report(sealedRows, unreadable);
    }

    private static async Task<Report> BytesAsync(NpgsqlConnection conn, SecretProtector p, ILogger log, string table, string column, string[] keys, string scope)
    {
        var header = p.CurrentBytesHeader;
        var rows = await conn.QueryAsync(
            $"SELECT {string.Join(", ", keys)}, {column} AS value FROM {table} "
            + $"WHERE {column} IS NOT NULL AND (length({column}) < @n OR substring({column} from 1 for @n) <> @hdr){scope}",
            new { n = header.Length, hdr = header });
        int sealedRows = 0, unreadable = 0;
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            var value = (byte[])dict["value"];
            byte[] plain;
            try { plain = SecretProtector.IsProtected(value) ? p.UnprotectBytes(value) : value; }
            catch (CryptographicException ex)
            {
                unreadable++;
                log.LogWarning("encryption at rest: {Table}.{Column} for {Key} could not be read ({Reason}); left as is",
                    table, column, KeyText(dict, keys), ex.Message);
                continue;
            }
            await conn.ExecuteAsync($"UPDATE {table} SET {column} = @v WHERE {Where(keys)}", Params(dict, keys, "v", p.ProtectBytes(plain)));
            sealedRows++;
        }
        return new Report(sealedRows, unreadable);
    }

    private static string Where(string[] keys) => string.Join(" AND ", keys.Select(k => $"{k} = @{k}"));

    private static DynamicParameters Params(IDictionary<string, object> row, string[] keys, string valueName, object value)
    {
        var d = new DynamicParameters();
        foreach (var k in keys) d.Add(k, row[k]);
        d.Add(valueName, value);
        return d;
    }

    private static string KeyText(IDictionary<string, object> row, string[] keys) =>
        string.Join("/", keys.Select(k => row[k]?.ToString()));
}

/// <summary>How a JSON document is kept in a sealed jsonb column, shared by the packet repo and the sweep.</summary>
public static class ProtectedJson
{
    /// <summary>The plaintext JSON behind a jsonb column's text: the document itself when it is
    /// an object (legacy plaintext), else the decrypted token inside the JSON string.</summary>
    public static string Read(string columnText, SecretProtector p)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(columnText.Length > 0 ? columnText : "{}");
        if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.String)
            return doc.RootElement.GetRawText();
        var token = doc.RootElement.GetString() ?? "";
        return SecretProtector.IsProtected(token) ? p.Unprotect(token) : "{}";
    }
}
