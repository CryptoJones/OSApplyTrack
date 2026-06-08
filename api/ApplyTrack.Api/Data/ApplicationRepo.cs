// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.RegularExpressions;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Data;

/// <summary>One application as the SPA's detail view needs it.</summary>
public sealed record AppRecord(string Name, AppFields Fields, long Version);

/// <summary>
/// Tenant-scoped CRUD over the <c>applications</c> table (Dapper) — the C# heir to
/// the Python <c>AppStore</c>. <b>Every</b> query unconditionally filters
/// <c>WHERE tenant_id = @t</c>; the optimistic lock lives in the versioned UPDATE.
/// </summary>
public sealed partial class ApplicationRepo
{
    static ApplicationRepo() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    private readonly IDbConnection _conn;
    private readonly long _t;

    public ApplicationRepo(IDbConnection conn, long tenantId)
    {
        _conn = conn;
        _t = tenantId;
    }

    private sealed record AppRow
    {
        public string Name { get; init; } = "";
        public string Company { get; init; } = "";
        public string Role { get; init; } = "";
        public string Lane { get; init; } = "";
        public string Status { get; init; } = "";
        public string Link { get; init; } = "";
        public string Location { get; init; } = "";
        public string Salary { get; init; } = "";
        public string Source { get; init; } = "";
        public string Contact { get; init; } = "";
        public string ContactEmail { get; init; } = "";
        public string Applied { get; init; } = "";
        public string Followup { get; init; } = "";
        public string Created { get; init; } = "";
        public string Score { get; init; } = "";
        public string Notes { get; init; } = "";
        public long Version { get; init; }

        public AppFields ToFields() => new()
        {
            Company = Company, Role = Role, Lane = Lane, Status = Status, Link = Link,
            Location = Location, Salary = Salary, Source = Source, Contact = Contact,
            ContactEmail = ContactEmail, Applied = Applied, Followup = Followup,
            Created = Created, Score = Score, Notes = Notes,
        };
    }

    public async Task<IReadOnlyList<AppSummary>> ListAsync()
    {
        var rows = await _conn.QueryAsync<AppRow>(
            """
            SELECT name, company, role, lane, status, contact, contact_email,
                   applied, followup, score, link, notes
            FROM applications
            WHERE tenant_id = @t
            ORDER BY array_position(@order, status), lower(company)
            """,
            new { t = _t, order = AppFields.Statuses });

        return rows.Select(r => new AppSummary
        {
            Filename = r.Name,
            Company = r.Company.Length > 0 ? r.Company : Slug.NameStem(r.Name),
            Role = r.Role,
            Lane = r.Lane,
            Status = r.Status,
            Contact = r.Contact,
            ContactEmail = r.ContactEmail,
            Applied = r.Applied,
            Followup = r.Followup,
            Score = r.Score,
            Link = r.Link,
            Snippet = Snippet(r.Notes),
        }).ToList();
    }

    /// <summary>
    /// Every application as a full record, for the account export. Ordered by slug so
    /// the zip is deterministic. Tenant-scoped like every other read.
    /// </summary>
    public async Task<IReadOnlyList<AppRecord>> ExportAllAsync()
    {
        var rows = await _conn.QueryAsync<AppRow>(
            "SELECT * FROM applications WHERE tenant_id = @t ORDER BY name", new { t = _t });
        return rows.Select(r => new AppRecord(r.Name, r.ToFields(), r.Version)).ToList();
    }

    public async Task<AppRecord?> GetAsync(string name)
    {
        var n = Slug.Normalize(name);
        var row = await _conn.QuerySingleOrDefaultAsync<AppRow>(
            "SELECT * FROM applications WHERE tenant_id = @t AND name = @n",
            new { t = _t, n });
        return row is null ? null : new AppRecord(row.Name, row.ToFields(), row.Version);
    }

    public async Task<(Dictionary<string, int> Status, Dictionary<string, int> Lane)> StatsAsync()
    {
        var byStatus = (await _conn.QueryAsync<(string Key, int Count)>(
                "SELECT status, count(*) FROM applications WHERE tenant_id = @t GROUP BY status",
                new { t = _t }))
            .ToDictionary(r => r.Key, r => r.Count);
        var byLane = (await _conn.QueryAsync<(string Key, int Count)>(
                "SELECT lane, count(*) FROM applications WHERE tenant_id = @t GROUP BY lane",
                new { t = _t }))
            .ToDictionary(r => r.Key, r => r.Count);
        return (byStatus, byLane);
    }

    public async Task<string> CreateAsync(AppFields raw)
    {
        var f = raw.Normalized();
        if (f.Company.Length == 0)
            throw new AppValidationException("an application requires a company");
        var name = Slug.FilenameFor(f.Company, f.Role);
        var created = f.Created.Length > 0 ? f.Created : MarkdownCodec.Today();
        try
        {
            await _conn.ExecuteAsync(
                """
                INSERT INTO applications
                    (tenant_id, name, company, role, lane, status, link, location, salary,
                     source, contact, contact_email, applied, followup, created, score, notes)
                VALUES
                    (@t, @name, @Company, @Role, @Lane, @Status, @Link, @Location, @Salary,
                     @Source, @Contact, @ContactEmail, @Applied, @Followup, @created, @Score, @Notes)
                """,
                new
                {
                    t = _t, name, created,
                    f.Company, f.Role, f.Lane, f.Status, f.Link, f.Location, f.Salary,
                    f.Source, f.Contact, f.ContactEmail, f.Applied, f.Followup, f.Score, f.Notes,
                });
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new AppValidationException($"an application named '{name}' already exists");
        }
        return name;
    }

    /// <summary>
    /// Insert-or-overwrite an application keyed on its slug <c>name</c> — the importer's
    /// path. Used by the account import: an incoming app replaces a matching local one
    /// (bumping <c>version</c>) and a brand-new slug is inserted. Slug-preserving so apply
    /// links survive the move. The C# twin of the Python <c>importer.py</c> upsert; runs
    /// inside the caller's transaction when one is supplied so the whole import is atomic.
    /// </summary>
    public async Task UpsertByNameAsync(string name, AppFields raw, IDbTransaction? tx = null)
    {
        var n = Slug.Normalize(name);
        var f = raw.Normalized();
        var created = f.Created.Length > 0 ? f.Created : MarkdownCodec.Today();
        await _conn.ExecuteAsync(
            """
            INSERT INTO applications
                (tenant_id, name, company, role, lane, status, link, location, salary,
                 source, contact, contact_email, applied, followup, created, score, notes)
            VALUES
                (@t, @n, @Company, @Role, @Lane, @Status, @Link, @Location, @Salary,
                 @Source, @Contact, @ContactEmail, @Applied, @Followup, @created, @Score, @Notes)
            ON CONFLICT (tenant_id, name) DO UPDATE SET
                company = EXCLUDED.company, role = EXCLUDED.role, lane = EXCLUDED.lane,
                status = EXCLUDED.status, link = EXCLUDED.link, location = EXCLUDED.location,
                salary = EXCLUDED.salary, source = EXCLUDED.source, contact = EXCLUDED.contact,
                contact_email = EXCLUDED.contact_email, applied = EXCLUDED.applied,
                followup = EXCLUDED.followup, created = EXCLUDED.created, score = EXCLUDED.score,
                notes = EXCLUDED.notes, version = applications.version + 1, updated_at = now()
            """,
            new
            {
                t = _t, n, created,
                f.Company, f.Role, f.Lane, f.Status, f.Link, f.Location, f.Salary,
                f.Source, f.Contact, f.ContactEmail, f.Applied, f.Followup, f.Score, f.Notes,
            },
            tx);
    }

    public Task<string> UpdateStructuredAsync(string name, AppFields fields, string? expectedVersion) =>
        DoUpdateAsync(name, fields.Normalized(), expectedVersion);

    public Task<string> UpdateRawAsync(string name, string content, string? expectedVersion) =>
        DoUpdateAsync(name, MarkdownCodec.Parse(content), expectedVersion);

    public async Task DeleteAsync(string name)
    {
        var n = Slug.Normalize(name);
        var affected = await _conn.ExecuteAsync(
            "DELETE FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n });
        if (affected == 0)
            throw new AppNotFoundException($"application not found: '{name}'");
    }

    private async Task<string> DoUpdateAsync(string name, AppFields f, string? expectedVersion)
    {
        var n = Slug.Normalize(name);
        var existingCreated = await _conn.QuerySingleOrDefaultAsync<string?>(
            "SELECT created FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n });
        if (existingCreated is null)
        {
            // QuerySingleOrDefault returns null for "no row" and for a NULL value; the
            // column is NOT NULL, so a null here unambiguously means the row is absent.
            throw new AppNotFoundException($"application not found: '{name}'");
        }

        var created = f.Created.Length > 0 ? f.Created
            : existingCreated.Length > 0 ? existingCreated
            : MarkdownCodec.Today();

        long? ev = null;
        if (expectedVersion is { Length: > 0 })
        {
            if (!long.TryParse(expectedVersion, out var parsed))
                throw new AppConflictException($"'{name}' changed since you opened it (stale version token)");
            ev = parsed;
        }

        var versionGuard = ev is null ? "" : " AND version = @ev";
        var affected = await _conn.ExecuteAsync(
            $"""
             UPDATE applications SET
                 company = @Company, role = @Role, lane = @Lane, status = @Status, link = @Link,
                 location = @Location, salary = @Salary, source = @Source, contact = @Contact,
                 contact_email = @ContactEmail, applied = @Applied, followup = @Followup,
                 created = @created, score = @Score, notes = @Notes,
                 version = version + 1, updated_at = now()
             WHERE tenant_id = @t AND name = @n{versionGuard}
             """,
            new
            {
                t = _t, n, ev, created,
                f.Company, f.Role, f.Lane, f.Status, f.Link, f.Location, f.Salary,
                f.Source, f.Contact, f.ContactEmail, f.Applied, f.Followup, f.Score, f.Notes,
            });

        if (affected == 0)
        {
            // The row existed a moment ago; 0 rows means the version moved (a poller or
            // another tab wrote first) — the SPA's 409 overwrite-confirm flow handles it.
            throw new AppConflictException(
                $"'{name}' changed since you opened it (expected version {ev})");
        }
        return n;
    }

    private static string Snippet(string notes)
    {
        var collapsed = Whitespace().Replace(notes, " ").Trim();
        return collapsed.Length > 160 ? collapsed[..157].TrimEnd() + "..." : collapsed;
    }
}
