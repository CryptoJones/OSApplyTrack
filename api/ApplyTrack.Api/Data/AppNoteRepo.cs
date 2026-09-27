// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using Dapper;
using ApplyTrack.Api.Crypto;

namespace ApplyTrack.Api.Data;

/// <summary>One entry of an application's interaction log: when, and what happened.</summary>
public sealed record AppNote(long Id, DateTime At, string Body);

/// <summary>One note as the account export carries it, keyed by the app's slug.</summary>
public sealed record AppNoteExport(string Application, DateTime At, string Body);

/// <summary>
/// Tenant-scoped reads and writes over <c>app_notes</c> (#360): the dated log of calls,
/// emails and conversations on an application. Append-only — a note is never edited,
/// though one can be removed. The body is sealed at rest; one sealed under a key this
/// instance lost is left out of reads rather than failing them.
/// </summary>
public sealed class AppNoteRepo
{
    static AppNoteRepo() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;

    public AppNoteRepo(IDbConnection conn, long tenantId, SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    private Task<long?> IdOfAsync(string name) =>
        _conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n = Slug.Normalize(name) });

    private string? Open(string body)
    {
        if (!SecretProtector.IsProtected(body))
            return body;
        try { return _protector.Unprotect(body); }
        catch (CryptographicException) { return null; }
    }

    /// <summary>The application's notes, oldest first; null when there is no such app.</summary>
    public async Task<IReadOnlyList<AppNote>?> ListAsync(string name)
    {
        if (await IdOfAsync(name) is not { } id)
            return null;
        var rows = await _conn.QueryAsync<(long Id, DateTime At, string Body)>(
            "SELECT id, at, body FROM app_notes WHERE tenant_id = @t AND application_id = @id ORDER BY at, id",
            new { t = _t, id });
        return rows.Select(r => Open(r.Body) is { } body ? new AppNote(r.Id, r.At, body) : null)
            .OfType<AppNote>().ToList();
    }

    /// <summary>Log a note, dated <paramref name="at"/> (now when null). Throws
    /// <see cref="AppNotFoundException"/> for an unknown app.</summary>
    public async Task<AppNote> AddAsync(string name, DateTime? at, string? body)
    {
        var b = (body ?? "").Trim();
        if (b.Length == 0)
            throw new AppValidationException("a note needs some text");
        InputLimits.Text("body", b, InputLimits.AppNote);
        var id = await IdOfAsync(name) ?? throw new AppNotFoundException($"application not found: '{name}'");
        var row = await _conn.QuerySingleAsync<(long Id, DateTime At)>(
            """
            INSERT INTO app_notes (tenant_id, application_id, at, body)
            VALUES (@t, @id, coalesce(@at, now()), @body)
            RETURNING id, at
            """,
            new { t = _t, id, at = at is { } a ? AppEventRepo.Utc(a) : (DateTime?)null, body = _protector.Protect(b) });
        return new AppNote(row.Id, row.At, b);
    }

    /// <summary>Remove one note from the application; false when there is no such one.</summary>
    public async Task<bool> DeleteAsync(string name, long noteId) =>
        await _conn.ExecuteAsync(
            """
            DELETE FROM app_notes n USING applications a
            WHERE n.tenant_id = @t AND a.tenant_id = @t AND a.id = n.application_id
              AND a.name = @n AND n.id = @noteId
            """,
            new { t = _t, n = Slug.Normalize(name), noteId }) > 0;

    /// <summary>Every note, opened, for the account export: by slug, then oldest first.</summary>
    public async Task<IReadOnlyList<AppNoteExport>> ExportAllAsync()
    {
        var rows = await _conn.QueryAsync<(string Application, DateTime At, string Body)>(
            """
            SELECT a.name, n.at, n.body
            FROM app_notes n JOIN applications a ON a.id = n.application_id AND a.tenant_id = n.tenant_id
            WHERE n.tenant_id = @t
            ORDER BY a.name, n.at, n.id
            """,
            new { t = _t });
        return rows.Select(r => Open(r.Body) is { } body ? new AppNoteExport(r.Application, r.At, body) : null)
            .OfType<AppNoteExport>().ToList();
    }

    /// <summary>
    /// Replace the notes of every application in <paramref name="applications"/> with the file's
    /// — the import's restore, as for interviews: an app the file brings with none ends with
    /// none; one outside the set is untouched and a note for it dropped. Empty notes are
    /// skipped and long ones cut to the cap. Returns how many were written.
    /// </summary>
    public async Task<int> ReplaceAsync(
        IEnumerable<string> applications, IEnumerable<AppNoteExport> notes, IDbTransaction? tx = null)
    {
        var apps = applications.Select(Slug.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (apps.Length == 0)
            return 0;
        var wanted = apps.ToHashSet(StringComparer.Ordinal);
        await _conn.ExecuteAsync(
            """
            DELETE FROM app_notes n USING applications a
            WHERE n.tenant_id = @t AND a.tenant_id = @t AND a.id = n.application_id AND a.name = ANY(@apps)
            """,
            new { t = _t, apps }, tx);
        var rows = notes
            .Where(n => wanted.Contains(Slug.Normalize(n.Application ?? "")))
            .Select(n => (App: Slug.Normalize(n.Application), At: AppEventRepo.Utc(n.At), Body: (n.Body ?? "").Trim()))
            .Where(r => r.Body.Length > 0)
            .Select(r => r with { Body = _protector.Protect(r.Body.Length > InputLimits.AppNote ? r.Body[..InputLimits.AppNote] : r.Body) })
            .ToList();
        if (rows.Count == 0)
            return 0;
        return await _conn.ExecuteAsync(
            """
            INSERT INTO app_notes (tenant_id, application_id, at, body)
            SELECT @t, a.id, r.at, r.body
            FROM unnest(@names::text[], @ats::timestamptz[], @bodies::text[]) AS r(name, at, body)
            JOIN applications a ON a.tenant_id = @t AND a.name = r.name
            """,
            new
            {
                t = _t,
                names = rows.Select(r => r.App).ToArray(),
                ats = rows.Select(r => r.At).ToArray(),
                bodies = rows.Select(r => r.Body).ToArray(),
            },
            tx);
    }
}
