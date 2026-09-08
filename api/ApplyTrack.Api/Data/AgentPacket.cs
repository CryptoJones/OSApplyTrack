// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One question on an application form, in the ATS-neutral shape the packet stores.</summary>
/// <param name="Id">Stable key — the ATS field name where there is one, else <c>std:*</c> for the standard set.</param>
/// <param name="Type"><c>text</c> | <c>textarea</c> | <c>select</c> | <c>multiselect</c> | <c>file</c>.</param>
/// <param name="Kind"><c>standard</c> (name/email/phone/résumé/letter/links), <c>custom</c> (screening), <c>eeo</c> (never answered).</param>
public sealed record PacketQuestion(
    string Id, string Label, bool Required, string Type, List<string> Options, string Kind)
{
    /// <summary>
    /// The field's helper text, where an ATS puts the qualifiers that change what a
    /// correct answer even is — "per month, in EUR", "only if referred by an employee".
    /// Greenhouse returns it as <c>description</c> and it was ignored until 1.22, which is
    /// how an annual USD figure got typed into a box asking for gross monthly EUR.
    ///
    /// Defaulted rather than positional so packets already stored as jsonb still
    /// deserialize; they simply carry an empty hint.
    /// </summary>
    public string Help { get; init; } = "";

    /// <summary>Label and helper text together — what a human actually reads before answering.</summary>
    public string FullPrompt => Help.Length == 0 ? Label : $"{Label} ({Help})";

    public const string Text = "text";
    public const string Textarea = "textarea";
    public const string Select = "select";
    public const string MultiSelect = "multiselect";
    public const string File = "file";

    public const string Standard = "standard";
    public const string Custom = "custom";
    public const string Eeo = "eeo";
}

/// <summary>A question the human must resolve before Submit unblocks, and why.</summary>
public sealed record ReviewItem(string Id, string Reason);

/// <summary>The prepared application packet as stored and as the SPA reads it.</summary>
public sealed class AgentPacket
{
    public string ApplicationName { get; set; } = "";
    public string Provider { get; set; } = "unknown";
    public string PostingExcerpt { get; set; } = "";
    public JsonElement Verdict { get; set; }
    public List<PacketQuestion> Questions { get; set; } = [];
    public Dictionary<string, string> Answers { get; set; } = [];
    public List<ReviewItem> NeedsReview { get; set; } = [];
    public string Model { get; set; } = "";
    public long Version { get; set; } = 1;
    public DateTimeOffset? NotifiedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Recompute the review list after an edit: a required question with a blank
    /// answer blocks, a previously flagged question stays flagged only while still blank.
    /// File and EEO questions never block — files are attached at submit and EEO is
    /// always optional.</summary>
    public void RecomputeReview()
    {
        var kept = new List<ReviewItem>();
        foreach (var q in Questions)
        {
            if (q.Kind == PacketQuestion.Eeo || q.Type == PacketQuestion.File)
                continue;
            var answered = Answers.TryGetValue(q.Id, out var a) && !string.IsNullOrWhiteSpace(a);
            if (answered)
                continue;
            var prior = NeedsReview.FirstOrDefault(r => r.Id == q.Id);
            if (prior is not null)
                kept.Add(prior);
            else if (q.Required)
                kept.Add(new ReviewItem(q.Id, "required, and no answer yet"));
        }
        NeedsReview = kept;
    }
}

/// <summary>
/// Tenant-scoped read/write of <c>agent_packets</c>. The packet carries its own
/// <c>version</c>: edits go through <see cref="UpdateAnswersAsync"/> with the version
/// the client last read and 409 on a mismatch — the same contract as applications.
/// </summary>
public sealed class AgentPacketRepo
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IDbConnection _conn;
    private readonly long _t;

    public AgentPacketRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    private sealed record Row(
        string ApplicationName, string Provider, string PostingExcerpt, string Verdict,
        string Questions, string Answers, string NeedsReview, string Model, long Version,
        DateTime? NotifiedAt, DateTime UpdatedAt);

    private const string Columns =
        "application_name AS applicationname, provider, posting_excerpt AS postingexcerpt, "
        + "verdict::text AS verdict, questions::text AS questions, answers::text AS answers, "
        + "needs_review::text AS needsreview, model, version, notified_at AS notifiedat, "
        + "updated_at AS updatedat";

    public async Task<AgentPacket?> GetAsync(string appName)
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row?>(
            $"SELECT {Columns} FROM agent_packets WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n = Slug.Normalize(appName) });
        return row is null ? null : ToPacket(row);
    }

    /// <summary>Store a freshly built packet. A rebuild bumps the version and clears the
    /// notification claim, so a genuinely new packet earns a new notification.</summary>
    public Task UpsertAsync(AgentPacket p, IDbTransaction? tx = null) =>
        _conn.ExecuteAsync(
            """
            INSERT INTO agent_packets (
                tenant_id, application_name, provider, posting_excerpt, verdict,
                questions, answers, needs_review, model, updated_at)
            VALUES (@t, @n, @provider, @excerpt, @verdict::jsonb,
                @questions::jsonb, @answers::jsonb, @review::jsonb, @model, now())
            ON CONFLICT (tenant_id, application_name) DO UPDATE SET
                provider        = EXCLUDED.provider,
                posting_excerpt = EXCLUDED.posting_excerpt,
                verdict         = EXCLUDED.verdict,
                questions       = EXCLUDED.questions,
                answers         = EXCLUDED.answers,
                needs_review    = EXCLUDED.needs_review,
                model           = EXCLUDED.model,
                version         = agent_packets.version + 1,
                notified_at     = NULL,
                updated_at      = now()
            """,
            new
            {
                t = _t, n = Slug.Normalize(p.ApplicationName), provider = p.Provider,
                excerpt = p.PostingExcerpt, verdict = p.Verdict.ValueKind == JsonValueKind.Undefined ? "{}" : p.Verdict.GetRawText(),
                questions = JsonSerializer.Serialize(p.Questions, Json),
                answers = JsonSerializer.Serialize(p.Answers, Json),
                review = JsonSerializer.Serialize(p.NeedsReview, Json),
                model = p.Model,
            },
            tx);

    /// <summary>
    /// Save edited answers. <paramref name="expectedVersion"/> is the packet version the
    /// client last read; a mismatch means someone (the agent rebuilding, another tab)
    /// changed it underneath and the caller gets a 409 to resolve. Null skips the check.
    /// </summary>
    public async Task<AgentPacket> UpdateAnswersAsync(
        string appName, Dictionary<string, string> answers, string? expectedVersion)
    {
        var n = Slug.Normalize(appName);
        var current = await GetAsync(n)
            ?? throw new AppNotFoundException($"no packet for '{n}'");
        long? ev = null;
        if (!string.IsNullOrEmpty(expectedVersion))
        {
            if (!long.TryParse(expectedVersion, out var parsed))
                throw new AppValidationException("expected_version must be an integer");
            ev = parsed;
        }

        // Merge: keys sent are updated (blank clears), keys not sent are kept, and only
        // known questions are writable — junk keys are dropped, not stored.
        var known = current.Questions.Select(q => q.Id).ToHashSet();
        foreach (var kv in answers)
        {
            if (!known.Contains(kv.Key)) continue;
            var value = (kv.Value ?? "").Trim();
            InputLimits.Text($"answer '{kv.Key}'", value, InputLimits.PacketAnswer);
            current.Answers[kv.Key] = value;
        }
        current.RecomputeReview();

        var affected = await _conn.ExecuteAsync(
            """
            UPDATE agent_packets
            SET answers = @answers::jsonb, needs_review = @review::jsonb,
                version = version + 1, updated_at = now()
            WHERE tenant_id = @t AND application_name = @n
              AND (@ev::bigint IS NULL OR version = @ev)
            """,
            new
            {
                t = _t, n, ev,
                answers = JsonSerializer.Serialize(current.Answers, Json),
                review = JsonSerializer.Serialize(current.NeedsReview, Json),
            });
        if (affected == 0)
            throw new AppConflictException($"packet for '{n}' changed since version {expectedVersion}");
        return (await GetAsync(n))!;
    }

    /// <summary>Claim the one notification this packet gets. True exactly once per build.</summary>
    public async Task<bool> TryClaimNotificationAsync(string appName)
    {
        var affected = await _conn.ExecuteAsync(
            "UPDATE agent_packets SET notified_at = now() "
            + "WHERE tenant_id = @t AND application_name = @n AND notified_at IS NULL",
            new { t = _t, n = Slug.Normalize(appName) });
        return affected > 0;
    }

    public async Task<bool> DeleteAsync(string appName)
    {
        var affected = await _conn.ExecuteAsync(
            "DELETE FROM agent_packets WHERE tenant_id = @t AND application_name = @n",
            new { t = _t, n = Slug.Normalize(appName) });
        return affected > 0;
    }

    private static AgentPacket ToPacket(Row r) => new()
    {
        ApplicationName = r.ApplicationName,
        Provider = r.Provider,
        PostingExcerpt = r.PostingExcerpt,
        Verdict = JsonDocument.Parse(r.Verdict.Length > 0 ? r.Verdict : "{}").RootElement.Clone(),
        Questions = JsonSerializer.Deserialize<List<PacketQuestion>>(r.Questions, Json) ?? [],
        Answers = JsonSerializer.Deserialize<Dictionary<string, string>>(r.Answers, Json) ?? [],
        NeedsReview = JsonSerializer.Deserialize<List<ReviewItem>>(r.NeedsReview, Json) ?? [],
        Model = r.Model,
        Version = r.Version,
        NotifiedAt = r.NotifiedAt is null ? null : new DateTimeOffset(DateTime.SpecifyKind(r.NotifiedAt.Value, DateTimeKind.Utc)),
        UpdatedAt = new DateTimeOffset(DateTime.SpecifyKind(r.UpdatedAt, DateTimeKind.Utc)),
    };
}
