// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Crypto;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One question the agent has met, and what it answers. The snake_case JSON shape of <c>/api/answers</c>.</summary>
public sealed record AnswerBankEntry(
    string Key, string Label, string Help, string Type, List<string> Options, string Answer, string Source,
    string FirstApplication, int TimesSeen, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt, DateTimeOffset UpdatedAt)
{
    public const string Agent = "agent";
    public const string Human = "human";
}

/// <summary>
/// Tenant-scoped read/write of <c>answer_bank</c>: every custom screening question the
/// agent has encountered, keyed by the question as the form asked it, with the answer
/// it gave — and, once a person has edited one, the answer it must give from then on.
/// Standard fields (name, email, résumé) and EEO questions never land here: the first
/// come from the profile, the second are never answered.
/// </summary>
public sealed partial class AnswerBankRepo
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;

    public AnswerBankRepo(IDbConnection conn, long tenantId, SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    /// <summary>
    /// The key a question is filed under: its label and helper text, lower-cased, with
    /// everything but letters and digits collapsed to single spaces. The helper text is
    /// part of the question on purpose — "Salary expectation (per month, in EUR)" is not
    /// the same question as "Salary expectation", and a pinned annual USD figure must not
    /// answer it. Public for tests.
    /// </summary>
    public static string KeyFor(PacketQuestion q) => KeyFor(q.FullPrompt);

    public static string KeyFor(string prompt)
    {
        var key = NonAlnum().Replace(prompt.ToLowerInvariant(), " ").Trim();
        return key.Length > 200 ? key[..200].TrimEnd() : key;
    }

    /// <summary>Only the questions worth banking: custom screening questions with a typed answer.</summary>
    public static bool Bankable(PacketQuestion q) =>
        q.Kind == PacketQuestion.Custom && q.Type != PacketQuestion.File && KeyFor(q).Length > 0;

    private sealed record Row(
        string Key, string Label, string Help, string Type, string Options, string AnswerCiphertext, string Source,
        string FirstApplication, int TimesSeen, DateTime FirstSeenAt, DateTime LastSeenAt, DateTime UpdatedAt);

    private const string Columns =
        "key, label, help, type, options::text AS options, answer_ciphertext AS answerciphertext, source, "
        + "first_application AS firstapplication, times_seen AS timesseen, first_seen_at AS firstseenat, "
        + "last_seen_at AS lastseenat, updated_at AS updatedat";

    public async Task<List<AnswerBankEntry>> ListAsync()
    {
        var rows = await _conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM answer_bank WHERE tenant_id = @t ORDER BY last_seen_at DESC, key", new { t = _t });
        return rows.Select(ToEntry).ToList();
    }

    public async Task<AnswerBankEntry?> GetAsync(string key)
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row?>(
            $"SELECT {Columns} FROM answer_bank WHERE tenant_id = @t AND key = @key", new { t = _t, key = KeyFor(key) });
        return row is null ? null : ToEntry(row);
    }

    /// <summary>The person's own answers, by key — what the drafter must say instead of drafting.</summary>
    public async Task<Dictionary<string, string>> PinnedAsync()
    {
        var rows = await _conn.QueryAsync<(string Key, string Cipher)>(
            "SELECT key, answer_ciphertext FROM answer_bank WHERE tenant_id = @t AND source = @human AND answer_ciphertext <> ''",
            new { t = _t, human = AnswerBankEntry.Human });
        var pinned = new Dictionary<string, string>();
        foreach (var (key, cipher) in rows)
        {
            var answer = Open(cipher);
            if (answer.Length > 0) pinned[key] = answer;
        }
        return pinned;
    }

    /// <summary>
    /// A packet was built: file each bankable question. A new question lands with the
    /// answer the drafter gave (blank when it gave none — that is the row a person fills
    /// in). One seen before is counted and dated; its answer is refreshed only while it is
    /// still the agent's, never when a person has made it theirs.
    /// </summary>
    public async Task RecordAsync(IReadOnlyList<PacketQuestion> questions, IReadOnlyDictionary<string, string> answers, string applicationName)
    {
        var seen = new HashSet<string>();
        foreach (var q in questions)
        {
            if (!Bankable(q)) continue;
            var key = KeyFor(q);
            if (!seen.Add(key)) continue;
            var answer = answers.TryGetValue(q.Id, out var a) ? a.Trim() : "";
            await _conn.ExecuteAsync(
                """
                INSERT INTO answer_bank (tenant_id, key, label, help, type, options, answer_ciphertext, source, first_application)
                VALUES (@t, @key, @label, @help, @type, @options::jsonb, @answer, @agent, @app)
                ON CONFLICT (tenant_id, key) DO UPDATE SET
                    label        = EXCLUDED.label,
                    help         = EXCLUDED.help,
                    type         = EXCLUDED.type,
                    options      = EXCLUDED.options,
                    times_seen   = answer_bank.times_seen + 1,
                    last_seen_at = now(),
                    answer_ciphertext = CASE WHEN answer_bank.source = @human THEN answer_bank.answer_ciphertext ELSE EXCLUDED.answer_ciphertext END,
                    updated_at   = CASE WHEN answer_bank.source = @human THEN answer_bank.updated_at ELSE now() END
                """,
                new
                {
                    t = _t, key, label = q.Label, help = q.Help, type = q.Type,
                    options = JsonSerializer.Serialize(q.Options, Json),
                    answer = Seal(answer), agent = AnswerBankEntry.Agent, human = AnswerBankEntry.Human,
                    app = Slug.Normalize(applicationName),
                });
        }
    }

    /// <summary>
    /// The person's answer for a question. Non-blank makes it theirs (the drafter uses it
    /// verbatim from now on); blank hands the question back to the drafter. False when
    /// no such question has been met.
    /// </summary>
    public async Task<bool> SetAsync(string key, string answer)
    {
        answer = answer.Trim();
        var affected = await _conn.ExecuteAsync(
            "UPDATE answer_bank SET answer_ciphertext = @answer, source = @source, updated_at = now() "
            + "WHERE tenant_id = @t AND key = @key",
            new
            {
                t = _t, key = KeyFor(key), answer = Seal(answer),
                source = answer.Length > 0 ? AnswerBankEntry.Human : AnswerBankEntry.Agent,
            });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string key) =>
        await _conn.ExecuteAsync("DELETE FROM answer_bank WHERE tenant_id = @t AND key = @key", new { t = _t, key = KeyFor(key) }) > 0;

    private string Seal(string answer) => answer.Length == 0 ? "" : _protector.Protect(answer);

    private string Open(string cipher)
    {
        if (cipher.Length == 0) return "";
        try { return _protector.Unprotect(cipher); }
        catch (CryptographicException) { return ""; }
    }

    private AnswerBankEntry ToEntry(Row r) => new(
        r.Key, r.Label, r.Help, r.Type,
        JsonSerializer.Deserialize<List<string>>(r.Options, Json) ?? [],
        Open(r.AnswerCiphertext), r.Source, r.FirstApplication, r.TimesSeen,
        Utc(r.FirstSeenAt), Utc(r.LastSeenAt), Utc(r.UpdatedAt));

    private static DateTimeOffset Utc(DateTime d) => new(DateTime.SpecifyKind(d, DateTimeKind.Utc));
}
