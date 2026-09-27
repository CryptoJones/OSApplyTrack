// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Security.Cryptography;
using Dapper;
using ApplyTrack.Api.Crypto;

namespace ApplyTrack.Api.Data;

/// <summary>A contact as a client writes one. Every field but the name is optional.</summary>
public sealed record ContactFields(
    string? Name, string? Email = null, string? Phone = null, string? Role = null,
    string? Company = null, string? Linkedin = null, string? Notes = null)
{
    /// <summary>Trimmed and checked: a name, the field caps, and a LinkedIn (or any profile)
    /// link that is http(s) — the SPA renders it as a link.</summary>
    public ContactFields Normalized()
    {
        var n = new ContactFields(
            (Name ?? "").Trim(), (Email ?? "").Trim(), (Phone ?? "").Trim(), (Role ?? "").Trim(),
            (Company ?? "").Trim(), (Linkedin ?? "").Trim(), (Notes ?? "").Trim());
        if (n.Name!.Length == 0)
            throw new AppValidationException("a contact needs a name");
        InputLimits.Text("name", n.Name, InputLimits.ContactName);
        InputLimits.Text("email", n.Email, InputLimits.ContactEmail);
        InputLimits.Text("phone", n.Phone, InputLimits.ContactPhone);
        InputLimits.Text("role", n.Role, InputLimits.ContactRole);
        InputLimits.Text("company", n.Company, InputLimits.Company);
        InputLimits.Text("linkedin", n.Linkedin, InputLimits.Link);
        InputLimits.Text("notes", n.Notes, InputLimits.ContactNotes);
        if (n.Linkedin!.Length > 0
            && !(Uri.TryCreate(n.Linkedin, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp)))
            throw new AppValidationException("linkedin must be an http(s) link");
        return n;
    }
}

/// <summary>An application a contact is linked to, and who they are in it.</summary>
public sealed record ContactLink(string Application, string Role);

/// <summary>One contact as the API answers it: the fields opened, the optimistic-lock
/// <c>version</c> (a string, as on applications) and the applications it is linked to.</summary>
public sealed record Contact(
    long Id, string Name, string Email, string Phone, string Role, string Company, string Linkedin,
    string Notes, string Version, IReadOnlyList<ContactLink> Applications);

/// <summary>A contact on one application: the person, and their part in this process
/// (recruiter, hiring manager, panel…).</summary>
public sealed record LinkedContact(Contact Contact, string Role);

/// <summary>A contact in the account export (#360). <c>id</c> is the exporting account's and
/// means nothing elsewhere: it only ties <see cref="ApplicationContactExport"/> rows to it.</summary>
public sealed record ContactExport(
    long Id, string Name, string Email = "", string Phone = "", string Role = "", string Company = "",
    string Linkedin = "", string Notes = "");

/// <summary>A contact's link to an application in the export: the app's slug, the contact's
/// file id, their part in the process.</summary>
public sealed record ApplicationContactExport(string Application, long Contact, string Role = "");

/// <summary>
/// Tenant-scoped reads and writes over <c>contacts</c> and <c>application_contacts</c>
/// (#360). Email, phone, the profile link and the notes are personal data and sealed at
/// rest (<see cref="SecretProtector"/>); a value an instance can't open reads as empty
/// rather than failing the list. Every statement filters on the tenant, joins included.
/// </summary>
public sealed class ContactRepo
{
    static ContactRepo() => DefaultTypeMap.MatchNamesWithUnderscores = true;

    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly SecretProtector _protector;

    public ContactRepo(IDbConnection conn, long tenantId, SecretProtector protector)
    {
        _conn = conn;
        _t = tenantId;
        _protector = protector;
    }

    private sealed record Row
    {
        public long Id { get; init; }
        public string Name { get; init; } = "";
        public string Email { get; init; } = "";
        public string Phone { get; init; } = "";
        public string Role { get; init; } = "";
        public string Company { get; init; } = "";
        public string Linkedin { get; init; } = "";
        public string Notes { get; init; } = "";
        public long Version { get; init; }
    }

    private sealed record LinkRow(long ContactId, string Application, string Role);

    private const string Columns = "id, name, email, phone, role, company, linkedin, notes, version";

    private string Seal(string value) => value.Length == 0 ? "" : _protector.Protect(value);

    private string Open(string value)
    {
        if (!SecretProtector.IsProtected(value))
            return value;
        try { return _protector.Unprotect(value); }
        catch (CryptographicException) { return ""; }
    }

    private Contact ToContact(Row r, IReadOnlyList<ContactLink> links) =>
        new(r.Id, r.Name, Open(r.Email), Open(r.Phone), r.Role, r.Company, Open(r.Linkedin), Open(r.Notes),
            r.Version.ToString(), links);

    private async Task<Dictionary<long, List<ContactLink>>> LinksAsync(long[]? contactIds = null)
    {
        var rows = await _conn.QueryAsync<LinkRow>(
            """
            SELECT l.contact_id, a.name AS application, l.role
            FROM application_contacts l JOIN applications a ON a.id = l.application_id AND a.tenant_id = l.tenant_id
            WHERE l.tenant_id = @t AND (@c::bigint[] IS NULL OR l.contact_id = ANY(@c))
            ORDER BY a.name
            """,
            new { t = _t, c = contactIds });
        return rows.GroupBy(r => r.ContactId)
            .ToDictionary(g => g.Key, g => g.Select(r => new ContactLink(r.Application, r.Role)).ToList());
    }

    /// <summary>Every contact, by name, with the applications each is on.</summary>
    public async Task<IReadOnlyList<Contact>> ListAsync()
    {
        var rows = await _conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM contacts WHERE tenant_id = @t ORDER BY lower(name), id", new { t = _t });
        var links = await LinksAsync();
        return rows.Select(r => ToContact(r, links.GetValueOrDefault(r.Id) ?? [])).ToList();
    }

    /// <summary>One contact, or null when this tenant has no such one.</summary>
    public async Task<Contact?> GetAsync(long id)
    {
        var row = await _conn.QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM contacts WHERE tenant_id = @t AND id = @id", new { t = _t, id });
        if (row is null)
            return null;
        var links = await LinksAsync([id]);
        return ToContact(row, links.GetValueOrDefault(id) ?? []);
    }

    public async Task<Contact> CreateAsync(ContactFields fields, IDbTransaction? tx = null)
    {
        var f = fields.Normalized();
        var id = await _conn.QuerySingleAsync<long>(
            """
            INSERT INTO contacts (tenant_id, name, email, phone, role, company, linkedin, notes)
            VALUES (@t, @name, @email, @phone, @role, @company, @linkedin, @notes)
            RETURNING id
            """,
            new
            {
                t = _t, name = f.Name, email = Seal(f.Email!), phone = Seal(f.Phone!), role = f.Role,
                company = f.Company, linkedin = Seal(f.Linkedin!), notes = Seal(f.Notes!),
            },
            tx);
        return new Contact(id, f.Name!, f.Email!, f.Phone!, f.Role!, f.Company!, f.Linkedin!, f.Notes!, "1", []);
    }

    /// <summary>Replace a contact's fields. With <paramref name="expectedVersion"/> the write
    /// happens only if nobody changed it since (409 otherwise), as on applications.</summary>
    public async Task<Contact> UpdateAsync(long id, ContactFields fields, string? expectedVersion)
    {
        var f = fields.Normalized();
        long? ev = null;
        if (expectedVersion is { Length: > 0 })
            ev = long.TryParse(expectedVersion, out var parsed)
                ? parsed
                : throw new AppConflictException("this contact changed since you opened it (stale version token)");
        var n = await _conn.ExecuteAsync(
            """
            UPDATE contacts SET name = @name, email = @email, phone = @phone, role = @role, company = @company,
                   linkedin = @linkedin, notes = @notes, version = version + 1, updated_at = now()
            WHERE tenant_id = @t AND id = @id AND (@ev::bigint IS NULL OR version = @ev)
            """,
            new
            {
                t = _t, id, ev, name = f.Name, email = Seal(f.Email!), phone = Seal(f.Phone!), role = f.Role,
                company = f.Company, linkedin = Seal(f.Linkedin!), notes = Seal(f.Notes!),
            });
        if (n == 0)
            throw await GetAsync(id) is null
                ? new AppNotFoundException("contact not found")
                : new AppConflictException($"this contact changed since you opened it (expected version {ev})");
        return (await GetAsync(id))!;
    }

    /// <summary>Delete a contact, and with it every link to it; false when there is no such one.</summary>
    public async Task<bool> DeleteAsync(long id) =>
        await _conn.ExecuteAsync("DELETE FROM contacts WHERE tenant_id = @t AND id = @id", new { t = _t, id }) > 0;

    private Task<long?> AppIdAsync(string name, IDbTransaction? tx = null) =>
        _conn.QuerySingleOrDefaultAsync<long?>(
            "SELECT id FROM applications WHERE tenant_id = @t AND name = @n", new { t = _t, n = Slug.Normalize(name) }, tx);

    /// <summary>The contacts on an application, by name; null when there is no such app.</summary>
    public async Task<IReadOnlyList<LinkedContact>?> ForApplicationAsync(string name)
    {
        if (await AppIdAsync(name) is not { } appId)
            return null;
        var rows = (await _conn.QueryAsync<(long Id, string LinkRole)>(
            """
            SELECT c.id, l.role FROM application_contacts l JOIN contacts c ON c.id = l.contact_id AND c.tenant_id = l.tenant_id
            WHERE l.tenant_id = @t AND l.application_id = @appId
            ORDER BY lower(c.name), c.id
            """,
            new { t = _t, appId })).ToList();
        if (rows.Count == 0)
            return [];
        var ids = rows.Select(r => r.Id).ToArray();
        var contacts = (await _conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM contacts WHERE tenant_id = @t AND id = ANY(@ids)", new { t = _t, ids }))
            .ToDictionary(r => r.Id);
        var links = await LinksAsync(ids);
        return rows.Where(r => contacts.ContainsKey(r.Id))
            .Select(r => new LinkedContact(ToContact(contacts[r.Id], links.GetValueOrDefault(r.Id) ?? []), r.LinkRole))
            .ToList();
    }

    /// <summary>Put a contact on an application with their part in it (a second link replaces
    /// the role). 404 for an unknown app or contact.</summary>
    public async Task<LinkedContact> LinkAsync(string name, long contactId, string? role)
    {
        var r = (role ?? "").Trim();
        InputLimits.Text("role", r, InputLimits.ContactRole);
        var appId = await AppIdAsync(name) ?? throw new AppNotFoundException($"application not found: '{name}'");
        var n = await _conn.ExecuteAsync(
            """
            INSERT INTO application_contacts (tenant_id, application_id, contact_id, role)
            SELECT @t, @appId, c.id, @r FROM contacts c WHERE c.tenant_id = @t AND c.id = @contactId
            ON CONFLICT (application_id, contact_id) DO UPDATE SET role = EXCLUDED.role
            """,
            new { t = _t, appId, contactId, r });
        if (n == 0)
            throw new AppNotFoundException("contact not found");
        return new LinkedContact((await GetAsync(contactId))!, r);
    }

    /// <summary>Take a contact off an application (the contact stays); false when it wasn't on it.</summary>
    public async Task<bool> UnlinkAsync(string name, long contactId) =>
        await _conn.ExecuteAsync(
            """
            DELETE FROM application_contacts l USING applications a
            WHERE l.tenant_id = @t AND a.tenant_id = @t AND a.id = l.application_id
              AND a.name = @n AND l.contact_id = @contactId
            """,
            new { t = _t, n = Slug.Normalize(name), contactId }) > 0;

    /// <summary>Every contact, opened, and every link, for the account export.</summary>
    public async Task<(IReadOnlyList<ContactExport> Contacts, IReadOnlyList<ApplicationContactExport> Links)> ExportAllAsync()
    {
        var rows = await _conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM contacts WHERE tenant_id = @t ORDER BY id", new { t = _t });
        var contacts = rows.Select(r => new ContactExport(
            r.Id, r.Name, Open(r.Email), Open(r.Phone), r.Role, r.Company, Open(r.Linkedin), Open(r.Notes))).ToList();
        var links = (await _conn.QueryAsync<ApplicationContactExport>(
            """
            SELECT a.name AS application, l.contact_id AS contact, l.role
            FROM application_contacts l JOIN applications a ON a.id = l.application_id AND a.tenant_id = l.tenant_id
            WHERE l.tenant_id = @t
            ORDER BY a.name, l.contact_id
            """,
            new { t = _t })).ToList();
        return (contacts, links);
    }

    /// <summary>
    /// The import's contacts: each one in the file matches a local contact of the same name and
    /// email (case aside) and overwrites it, or is added — so re-importing adds nobody twice.
    /// Returns the file id → local id map the links are restored through. The fields must
    /// already be checked (<see cref="ContactFields.Normalized"/>).
    /// </summary>
    public async Task<Dictionary<long, long>> MergeAsync(IEnumerable<(long FileId, ContactFields Fields)> incoming, IDbTransaction tx)
    {
        var existing = (await _conn.QueryAsync<Row>(
                $"SELECT {Columns} FROM contacts WHERE tenant_id = @t ORDER BY id", new { t = _t }, tx))
            .GroupBy(r => Key(r.Name, Open(r.Email)))
            .ToDictionary(g => g.Key, g => g.First().Id);
        var map = new Dictionary<long, long>();
        foreach (var (fileId, f) in incoming)
        {
            var key = Key(f.Name!, f.Email!);
            if (existing.TryGetValue(key, out var id))
            {
                await _conn.ExecuteAsync(
                    """
                    UPDATE contacts SET name = @name, email = @email, phone = @phone, role = @role, company = @company,
                           linkedin = @linkedin, notes = @notes, version = version + 1, updated_at = now()
                    WHERE tenant_id = @t AND id = @id
                    """,
                    new
                    {
                        t = _t, id, name = f.Name, email = Seal(f.Email!), phone = Seal(f.Phone!), role = f.Role,
                        company = f.Company, linkedin = Seal(f.Linkedin!), notes = Seal(f.Notes!),
                    },
                    tx);
            }
            else
            {
                id = (await CreateAsync(f, tx)).Id;
                existing[key] = id;
            }
            map[fileId] = id;
        }
        return map;
    }

    private static string Key(string name, string email) =>
        name.Trim().ToLowerInvariant() + "\n" + email.Trim().ToLowerInvariant();

    /// <summary>
    /// Replace the contact links of every application in <paramref name="applications"/> with the
    /// file's — as the interviews are restored: an app the file brings with none ends with none,
    /// one outside the set is untouched, and a link naming a contact the file doesn't carry is
    /// dropped. Returns how many were written.
    /// </summary>
    public async Task<int> ReplaceLinksAsync(
        IEnumerable<string> applications, IEnumerable<ApplicationContactExport> links,
        IReadOnlyDictionary<long, long> contactIds, IDbTransaction tx)
    {
        var apps = applications.Select(Slug.Normalize).Distinct(StringComparer.Ordinal).ToArray();
        if (apps.Length == 0)
            return 0;
        var wanted = apps.ToHashSet(StringComparer.Ordinal);
        await _conn.ExecuteAsync(
            """
            DELETE FROM application_contacts l USING applications a
            WHERE l.tenant_id = @t AND a.tenant_id = @t AND a.id = l.application_id AND a.name = ANY(@apps)
            """,
            new { t = _t, apps }, tx);
        var rows = links
            .Where(l => wanted.Contains(Slug.Normalize(l.Application)) && contactIds.ContainsKey(l.Contact))
            .Select(l => (App: Slug.Normalize(l.Application), Contact: contactIds[l.Contact], Role: Clip((l.Role ?? "").Trim(), InputLimits.ContactRole)))
            .DistinctBy(r => (r.App, r.Contact))
            .ToList();
        if (rows.Count == 0)
            return 0;
        return await _conn.ExecuteAsync(
            """
            INSERT INTO application_contacts (tenant_id, application_id, contact_id, role)
            SELECT @t, a.id, c.id, r.role
            FROM unnest(@names::text[], @contacts::bigint[], @roles::text[]) AS r(name, contact, role)
            JOIN applications a ON a.tenant_id = @t AND a.name = r.name
            JOIN contacts c ON c.tenant_id = @t AND c.id = r.contact
            ON CONFLICT (application_id, contact_id) DO UPDATE SET role = EXCLUDED.role
            """,
            new
            {
                t = _t,
                names = rows.Select(r => r.App).ToArray(),
                contacts = rows.Select(r => r.Contact).ToArray(),
                roles = rows.Select(r => r.Role).ToArray(),
            },
            tx);
    }

    private static string Clip(string s, int max) => s.Length > max ? s[..max] : s;
}
