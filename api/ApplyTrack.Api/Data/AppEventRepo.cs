// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Globalization;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>One scheduled interview on an application: when (an instant) and a free-text note.</summary>
public sealed record Interview(long Id, DateTime At, string Note);

/// <summary>One interview as the account export carries it, keyed by the app's slug.</summary>
public sealed record InterviewExport(string Application, DateTime At, string Note = "");

/// <summary>An application whose follow-up date has come: due today, or overdue.</summary>
public sealed record DueFollowup(long Id, string Name, string Company, string Role, string Status, string Followup, bool Overdue);

/// <summary>An interview coming up, with the application it belongs to.</summary>
public sealed record DueInterview(long Id, string Name, string Company, string Role, DateTime At, string Note);

/// <summary>The <c>GET /api/due</c> body: what needs the person, as of <c>today</c>.</summary>
public sealed record DueList(string Today, IReadOnlyList<DueFollowup> Followups, IReadOnlyList<DueInterview> Interviews);

/// <summary>
/// Tenant-scoped reads and writes over <c>app_events</c> (#354) — the interviews on an
/// application — plus the "what is due" reads the reminder, the Due view and the calendar
/// feed share. A follow-up is the application's own <c>followup</c> date (free text; only
/// a valid <c>YYYY-MM-DD</c> counts) on an application still in play.
/// </summary>
public sealed class AppEventRepo
{
    static AppEventRepo() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    /// <summary>Statuses a follow-up still matters in: applied and waiting, or in the loop.
    /// A lead has nobody to follow up with yet; a rejection or a pass is over.</summary>
    public static readonly string[] FollowupStatuses = ["applied", "screen", "onsite", "offer"];

    /// <summary>How far ahead the Due view looks for interviews.</summary>
    public static readonly TimeSpan InterviewHorizon = TimeSpan.FromDays(7);

    public const int MaxNote = 2000;

    private readonly IDbConnection _conn;
    private readonly long _t;

    public AppEventRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    /// <summary>A strict <c>YYYY-MM-DD</c>, or null — the follow-up field is free text.</summary>
    public static DateOnly? ParseDay(string? value) =>
        DateOnly.TryParseExact((value ?? "").Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d : null;

    private Task<long?> IdOfAsync(string name) =>
        _conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n = Slug.Normalize(name) });

    /// <summary>The application's interviews, soonest first; null when there is no such app.</summary>
    public async Task<IReadOnlyList<Interview>?> ListAsync(string name)
    {
        if (await IdOfAsync(name) is not { } id)
            return null;
        return (await _conn.QueryAsync<Interview>(
            "SELECT id, at, note FROM app_events WHERE tenant_id = @t AND application_id = @id AND kind = 'interview' ORDER BY at, id",
            new { t = _t, id })).ToList();
    }

    /// <summary>Schedule an interview. Throws <see cref="AppNotFoundException"/> for an unknown app.</summary>
    public async Task<Interview> AddAsync(string name, DateTime at, string? note)
    {
        var n = (note ?? "").Trim();
        InputLimits.Text("note", n, MaxNote);
        var id = await IdOfAsync(name) ?? throw new AppNotFoundException($"application not found: '{name}'");
        return await _conn.QuerySingleAsync<Interview>(
            """
            INSERT INTO app_events (tenant_id, application_id, kind, at, note)
            VALUES (@t, @id, 'interview', @at, @n)
            RETURNING id, at, note
            """,
            new { t = _t, id, at = Utc(at), n });
    }

    /// <summary>Remove one interview from the application; false when there is no such one.</summary>
    public async Task<bool> DeleteAsync(string name, long eventId) =>
        await _conn.ExecuteAsync(
            """
            DELETE FROM app_events e USING applications a
            WHERE e.tenant_id = @t AND a.tenant_id = @t AND a.id = e.application_id
              AND a.name = @n AND e.id = @eventId
            """,
            new { t = _t, n = Slug.Normalize(name), eventId }) > 0;

    /// <summary>
    /// Follow-ups due on or before <paramref name="today"/> (the overdue ones flagged), oldest
    /// first, and interviews from <paramref name="from"/> until <paramref name="until"/>.
    /// </summary>
    public async Task<DueList> DueAsync(DateOnly today, DateTime from, DateTime until, IDbTransaction? tx = null)
    {
        var day = today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var followups = (await _conn.QueryAsync<(long Id, string Name, string Company, string Role, string Status, string Followup)>(
            """
            SELECT id, name, company, role, status, followup FROM applications
            WHERE tenant_id = @t AND status = ANY(@statuses)
              AND followup ~ '^\d{4}-\d{2}-\d{2}$' AND followup <= @day
            ORDER BY followup, id
            """,
            new { t = _t, statuses = FollowupStatuses, day }, tx))
            .Where(r => ParseDay(r.Followup) is not null)
            .Select(r => new DueFollowup(r.Id, r.Name, r.Company, r.Role, r.Status, r.Followup,
                string.CompareOrdinal(r.Followup, day) < 0))
            .ToList();
        var interviews = (await _conn.QueryAsync<DueInterview>(
            """
            SELECT e.id, a.name, a.company, a.role, e.at, e.note
            FROM app_events e JOIN applications a ON a.id = e.application_id AND a.tenant_id = e.tenant_id
            WHERE e.tenant_id = @t AND e.kind = 'interview' AND e.at >= @from AND e.at < @until
            ORDER BY e.at, e.id
            """,
            new { t = _t, from = Utc(from), until = Utc(until) }, tx)).ToList();
        return new DueList(day, followups, interviews);
    }

    /// <summary>What the calendar feed carries: every follow-up date on an application still in
    /// play (a valid date only), and every interview from <paramref name="since"/> on.</summary>
    public async Task<(IReadOnlyList<DueFollowup> Followups, IReadOnlyList<DueInterview> Interviews)> CalendarAsync(DateTime since)
    {
        var followups = (await _conn.QueryAsync<(long Id, string Name, string Company, string Role, string Status, string Followup)>(
            """
            SELECT id, name, company, role, status, followup FROM applications
            WHERE tenant_id = @t AND status = ANY(@statuses) AND followup ~ '^\d{4}-\d{2}-\d{2}$'
            ORDER BY followup, id
            """,
            new { t = _t, statuses = FollowupStatuses }))
            .Where(r => ParseDay(r.Followup) is not null)
            .Select(r => new DueFollowup(r.Id, r.Name, r.Company, r.Role, r.Status, r.Followup, false))
            .ToList();
        var interviews = (await _conn.QueryAsync<DueInterview>(
            """
            SELECT e.id, a.name, a.company, a.role, e.at, e.note
            FROM app_events e JOIN applications a ON a.id = e.application_id AND a.tenant_id = e.tenant_id
            WHERE e.tenant_id = @t AND e.kind = 'interview' AND e.at >= @since
            ORDER BY e.at, e.id
            """,
            new { t = _t, since = Utc(since) })).ToList();
        return (followups, interviews);
    }

    /// <summary>Every interview, for the account export: by slug, then soonest first.</summary>
    public async Task<IReadOnlyList<InterviewExport>> ExportAllAsync() =>
        (await _conn.QueryAsync<InterviewExport>(
            """
            SELECT a.name AS application, e.at, e.note
            FROM app_events e JOIN applications a ON a.id = e.application_id AND a.tenant_id = e.tenant_id
            WHERE e.tenant_id = @t AND e.kind = 'interview'
            ORDER BY a.name, e.at, e.id
            """,
            new { t = _t })).ToList();

    /// <summary>
    /// Replace the interviews of every application in <paramref name="applications"/> with the
    /// file's — the import's restore, in its transaction, after the upsert. As with the status
    /// history, an app the file lists with none ends up with none; one outside the set is
    /// untouched and an interview for it is dropped. Returns how many were written.
    /// </summary>
    public async Task<int> ReplaceAsync(
        IEnumerable<string> applications, IEnumerable<InterviewExport> interviews, IDbTransaction? tx = null)
    {
        var apps = applications.Select(Slug.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (apps.Length == 0)
            return 0;
        var wanted = apps.ToHashSet(StringComparer.Ordinal);
        await _conn.ExecuteAsync(
            """
            DELETE FROM app_events e USING applications a
            WHERE e.tenant_id = @t AND a.tenant_id = @t AND a.id = e.application_id AND a.name = ANY(@apps)
            """,
            new { t = _t, apps }, tx);
        var rows = interviews
            .Where(i => wanted.Contains(Slug.Normalize(i.Application)))
            .Select(i => (App: Slug.Normalize(i.Application), At: Utc(i.At), Note: Clip((i.Note ?? "").Trim(), MaxNote)))
            .ToList();
        if (rows.Count == 0)
            return 0;
        return await _conn.ExecuteAsync(
            """
            INSERT INTO app_events (tenant_id, application_id, kind, at, note)
            SELECT @t, a.id, 'interview', r.at, r.note
            FROM unnest(@names::text[], @ats::timestamptz[], @notes::text[]) AS r(name, at, note)
            JOIN applications a ON a.tenant_id = @t AND a.name = r.name
            """,
            new
            {
                t = _t,
                names = rows.Select(r => r.App).ToArray(),
                ats = rows.Select(r => r.At).ToArray(),
                notes = rows.Select(r => r.Note).ToArray(),
            },
            tx);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;

    /// <summary>An instant as UTC; an unspecified kind is taken to be UTC already.</summary>
    public static DateTime Utc(DateTime at) =>
        DateTime.SpecifyKind(at, at.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : at.Kind).ToUniversalTime();
}
