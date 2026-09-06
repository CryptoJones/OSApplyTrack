// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// What the agent may do for one tenant, plus the standing answers it hands to
/// application forms without consulting a model. Serializes to the snake_case
/// <c>/api/agent-settings</c> shape. A missing row means every default — and in
/// particular <see cref="Enabled"/> false.
/// </summary>
public sealed class AgentSettings
{
    public bool Enabled { get; set; }
    public bool DryRun { get; set; } = true;
    public int MinFitScore { get; set; } = 70;
    public int MaxPerRun { get; set; } = 5;
    public int MaxPerDay { get; set; } = 20;
    public string WorkAuthorization { get; set; } = "";
    public bool NeedsSponsorship { get; set; }
    public bool ClearanceOk { get; set; }
    public string SalaryExpectation { get; set; } = "";
    public string Phone { get; set; } = "";

    /// <summary>Let the browser discover and fill forms on ATSs it doesn't know. Off by
    /// default: a never-seen form is where a wrong guess is likeliest.</summary>
    public bool LongTail { get; set; }

    private const int MaxPerRunCeil = 50;
    private const int MaxPerDayCeil = 500;

    /// <summary>Build normalized settings from loose JSON, ignoring junk keys (mirrors Criteria.FromJson).</summary>
    public static AgentSettings FromJson(JsonElement data)
    {
        var s = new AgentSettings();
        if (data.ValueKind != JsonValueKind.Object)
            return s;
        s.Enabled = GetBool(data, "enabled", s.Enabled);
        s.DryRun = GetBool(data, "dry_run", s.DryRun);
        s.MinFitScore = Math.Clamp(GetInt(data, "min_fit_score", s.MinFitScore), 0, 100);
        s.MaxPerRun = Math.Clamp(GetInt(data, "max_per_run", s.MaxPerRun), 1, MaxPerRunCeil);
        s.MaxPerDay = Math.Clamp(GetInt(data, "max_per_day", s.MaxPerDay), 1, MaxPerDayCeil);
        s.WorkAuthorization = GetString(data, "work_authorization");
        s.NeedsSponsorship = GetBool(data, "needs_sponsorship", s.NeedsSponsorship);
        s.ClearanceOk = GetBool(data, "clearance_ok", s.ClearanceOk);
        s.SalaryExpectation = GetString(data, "salary_expectation");
        s.Phone = GetString(data, "phone");
        s.LongTail = GetBool(data, "long_tail", s.LongTail);
        InputLimits.Text("work_authorization", s.WorkAuthorization, InputLimits.AgentAnswer);
        InputLimits.Text("salary_expectation", s.SalaryExpectation, InputLimits.AgentAnswer);
        InputLimits.Text("phone", s.Phone, InputLimits.AgentAnswer);
        return s;
    }

    /// <summary>The tenant's standing facts as prompt context — what the model may assume
    /// about the candidate's eligibility without being able to invent any of it.</summary>
    public string ToEligibilityBrief()
    {
        var lines = new List<string>();
        if (WorkAuthorization.Length > 0) lines.Add($"Work authorization: {WorkAuthorization}");
        lines.Add(NeedsSponsorship ? "Needs visa sponsorship: yes" : "Needs visa sponsorship: no");
        lines.Add(ClearanceOk
            ? "Security clearance: holds or can obtain one"
            : "Security clearance: none, and cannot obtain one");
        if (SalaryExpectation.Length > 0) lines.Add($"Salary expectation: {SalaryExpectation}");
        return string.Join("\n", lines);
    }

    private static string GetString(JsonElement obj, string key) =>
        obj.TryGetProperty(key, out var v)
            ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ValueKind is JsonValueKind.Null ? "" : v.ToString()).Trim()
            : "";

    private static bool GetBool(JsonElement obj, string key, bool fallback) =>
        obj.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? v.GetBoolean()
            : fallback;

    private static int GetInt(JsonElement obj, string key, int fallback)
    {
        if (!obj.TryGetProperty(key, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var p)) return p;
        return fallback;
    }
}

/// <summary>Tenant-scoped read/write of the single <c>agent_settings</c> row.</summary>
public sealed class AgentSettingsRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;

    public AgentSettingsRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    private sealed record Row(
        bool Enabled, bool DryRun, int MinFitScore, int MaxPerRun, int MaxPerDay,
        string WorkAuthorization, bool NeedsSponsorship, bool ClearanceOk,
        string SalaryExpectation, string Phone, bool LongTail);

    /// <summary>The stored settings, or the defaults (agent off) when no row exists.</summary>
    public async Task<AgentSettings> GetAsync()
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row?>(
            "SELECT enabled, dry_run AS dryrun, min_fit_score AS minfitscore, "
            + "max_per_run AS maxperrun, max_per_day AS maxperday, "
            + "work_authorization AS workauthorization, needs_sponsorship AS needssponsorship, "
            + "clearance_ok AS clearanceok, salary_expectation AS salaryexpectation, phone, "
            + "long_tail AS longtail "
            + "FROM agent_settings WHERE tenant_id = @t",
            new { t = _t });
        if (row is null)
            return new AgentSettings();
        return new AgentSettings
        {
            Enabled = row.Enabled, DryRun = row.DryRun, MinFitScore = row.MinFitScore,
            MaxPerRun = row.MaxPerRun, MaxPerDay = row.MaxPerDay,
            WorkAuthorization = row.WorkAuthorization, NeedsSponsorship = row.NeedsSponsorship,
            ClearanceOk = row.ClearanceOk, SalaryExpectation = row.SalaryExpectation, Phone = row.Phone,
            LongTail = row.LongTail,
        };
    }

    public Task UpsertAsync(AgentSettings s, IDbTransaction? tx = null) =>
        _conn.ExecuteAsync(
            """
            INSERT INTO agent_settings (
                tenant_id, enabled, dry_run, min_fit_score, max_per_run, max_per_day,
                work_authorization, needs_sponsorship, clearance_ok, salary_expectation, phone,
                long_tail, updated_at)
            VALUES (@t, @Enabled, @DryRun, @MinFitScore, @MaxPerRun, @MaxPerDay,
                @WorkAuthorization, @NeedsSponsorship, @ClearanceOk, @SalaryExpectation, @Phone,
                @LongTail, now())
            ON CONFLICT (tenant_id) DO UPDATE SET
                enabled            = EXCLUDED.enabled,
                dry_run            = EXCLUDED.dry_run,
                min_fit_score      = EXCLUDED.min_fit_score,
                max_per_run        = EXCLUDED.max_per_run,
                max_per_day        = EXCLUDED.max_per_day,
                work_authorization = EXCLUDED.work_authorization,
                needs_sponsorship  = EXCLUDED.needs_sponsorship,
                clearance_ok       = EXCLUDED.clearance_ok,
                salary_expectation = EXCLUDED.salary_expectation,
                phone              = EXCLUDED.phone,
                long_tail          = EXCLUDED.long_tail,
                updated_at         = now()
            """,
            new
            {
                t = _t, s.Enabled, s.DryRun, s.MinFitScore, s.MaxPerRun, s.MaxPerDay,
                s.WorkAuthorization, s.NeedsSponsorship, s.ClearanceOk, s.SalaryExpectation, s.Phone,
                s.LongTail,
            },
            tx);
}
