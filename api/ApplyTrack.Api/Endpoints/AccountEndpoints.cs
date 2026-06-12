// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using System.Text.Json.Serialization;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Account-level self-service: export everything as one JSON document, import that
/// document on another instance, or delete the whole account. These replace billing in
/// the OSS build — the point is portability, so nobody is locked to one instance: you
/// can walk your data to a host with better features/pricing. All are under <c>/api</c>,
/// so the tenancy choke-point already requires a session and the repos arrive
/// pre-scoped — these only ever touch the caller's own tenant.
///
/// Two export flavours, told apart by their <c>format</c> field: the private migration
/// snapshot (everything, re-imported into your own account) and the shared opportunity
/// list (public posting facts only, handed to a peer who imports it as fresh leads).
/// </summary>
public static class AccountEndpoints
{
    // The format discriminators. Import branches on these: the peer-facing list takes
    // the non-destructive insert-if-absent path; our own snapshot (or a pre-1.3 file
    // with no format field) takes the overwrite-by-slug migration path.
    private const string SharedFormat = "applytrack-shared";
    private const string ExportFormat = "applytrack-export";

    // The export is a single private migration snapshot. snake_case + indented so it
    // round-trips byte-compatibly with the /api/* shapes and a human can read it.
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // A full personal snapshot: every application (all fields + its slug, so apply
        // links survive a move) plus the search criteria and company blacklist, as one
        // downloadable JSON. Built in memory — a self-host account is small.
        app.MapGet("/api/account/export", async (
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist) =>
        {
            var records = await apps.ExportAllAsync();
            var doc = new ExportDoc(
                Applications: records.Select(ApplicationExport.From).ToList(),
                Criteria: await criteria.GetAsync(),
                Blacklist: await blacklist.ListAsync());

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, ExportJson);
            var filename = $"applytrack-export-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", filename);
        });

        // The peer-facing counterpart: a curated opportunity list to hand to someone
        // else ("here's where I found 80 places hiring — work it too"). Only the facts
        // of the posting itself leave the account — slug (so the importer can de-dup),
        // company, role, link, location, source. Everything personal — status, notes,
        // contact, dates, score, salary — is stripped at the source, not trusted to a
        // client-side filter.
        app.MapGet("/api/account/export/shared", async (ApplicationRepo apps) =>
        {
            var records = await apps.ExportAllAsync();
            var doc = new SharedExportDoc(
                Applications: records.Select(SharedApplicationExport.From).ToList());

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, ExportJson);
            var filename = $"applytrack-shared-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", filename);
        });

        // Import a snapshot produced by export (this or another instance). Overwrite by
        // slug — an incoming app replaces a matching local one, a new slug is added,
        // untouched local apps stay; re-importing is idempotent. The whole load runs in
        // one transaction so a mid-import failure leaves the account untouched.
        app.MapPost("/api/account/import", async (
            ImportDoc body, IDbConnection conn,
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist) =>
        {
            var hasApps = body.Applications is { Count: > 0 };

            // A shared opportunity list imports differently from a migration snapshot:
            // every entry lands as a fresh lead with personal state blank, and a slug
            // the importer already tracks is skipped, never overwritten — a peer's
            // list must not clobber the importer's own pipeline. Whatever personal
            // fields a doctored file carries are dropped here, by construction.
            if (body.Format == SharedFormat)
            {
                if (!hasApps)
                    throw new AppValidationException("no importable data in file");

                if (conn.State != ConnectionState.Open)
                    conn.Open();
                using var sharedTx = conn.BeginTransaction();

                int added = 0, skipped = 0;
                foreach (var a in body.Applications!)
                {
                    if (await apps.InsertIfAbsentAsync(a.Name, a.ToSharedLeadFields(), sharedTx))
                        added++;
                    else
                        skipped++;
                }
                sharedTx.Commit();

                return Results.Ok(new
                {
                    imported_applications = added,
                    skipped_applications = skipped,
                });
            }

            // Only our own snapshot (or a pre-1.3 file with no discriminator) may take
            // the overwrite-by-slug path. Reject anything else — a typo, a foreign
            // exporter, or a future "applytrack-shared" variant — rather than silently
            // clobbering the importer's apps with a near-miss format string.
            if (!string.IsNullOrEmpty(body.Format) && body.Format != ExportFormat)
                throw new AppValidationException($"unrecognized import format \"{body.Format}\"");

            var hasCriteria = body.Criteria is { ValueKind: JsonValueKind.Object };
            var hasBlacklist = body.Blacklist is { Count: > 0 };
            if (!hasApps && !hasCriteria && !hasBlacklist)
                throw new AppValidationException("no importable data in file");

            if (conn.State != ConnectionState.Open)
                conn.Open();
            using var tx = conn.BeginTransaction();

            var importedApps = 0;
            foreach (var a in body.Applications ?? [])
            {
                await apps.UpsertByNameAsync(a.Name, a.ToFields(), tx);
                importedApps++;
            }

            if (hasCriteria)
                await criteria.UpsertAsync(Criteria.FromJson(body.Criteria!.Value), tx);

            var importedBlacklist = 0;
            foreach (var company in body.Blacklist ?? [])
                if (await blacklist.AddAsync(company, tx))
                    importedBlacklist++;

            tx.Commit();

            return Results.Ok(new
            {
                imported_applications = importedApps,
                imported_blacklist = importedBlacklist,
                criteria_applied = hasCriteria,
            });
        });

        // Delete the account. The FK cascades (0005/0006/0009) drop every per-tenant row
        // in one statement; clearing the cookie tidies the now-dangling session client-side.
        app.MapDelete("/api/account", async (
            HttpContext ctx, TenantContext tenant, UserRepo users) =>
        {
            await users.DeleteAsync(tenant.TenantId);
            ctx.Response.Cookies.Delete(AuthCookie.Name, AuthCookie.DeleteOptions(ctx.Request.IsHttps));
            return Results.NoContent();
        });
    }

    /// <summary>The export envelope — a versioned, self-describing migration snapshot.</summary>
    private sealed record ExportDoc(
        IReadOnlyList<ApplicationExport> Applications,
        Criteria Criteria,
        IReadOnlyList<string> Blacklist)
    {
        [JsonPropertyOrder(-3)] public string Format => ExportFormat;
        [JsonPropertyOrder(-2)] public int Version => 1;
        [JsonPropertyOrder(-1)] public DateTime ExportedAt => DateTime.UtcNow;
    }

    /// <summary>
    /// The shared-list envelope — same self-describing shape as <see cref="ExportDoc"/>
    /// but a distinct <c>format</c>, so import can tell the two apart. No criteria, no
    /// blacklist: those are personal settings and never leave the account this way.
    /// </summary>
    private sealed record SharedExportDoc(
        IReadOnlyList<SharedApplicationExport> Applications)
    {
        [JsonPropertyOrder(-3)] public string Format => SharedFormat;
        [JsonPropertyOrder(-2)] public int Version => 1;
        [JsonPropertyOrder(-1)] public DateTime ExportedAt => DateTime.UtcNow;
    }
}

/// <summary>
/// One application reduced to the facts of the posting itself: the slug (the importer's
/// de-dup key) plus company, role, link, location and source. Everything that describes
/// the exporter rather than the job — status, contact, dates, score, salary, notes —
/// has no field here, so it cannot leak by accident.
/// </summary>
public sealed record SharedApplicationExport(
    string Name, string Company, string Role, string Link, string Location, string Source)
{
    public static SharedApplicationExport From(AppRecord r) =>
        new(r.Name, r.Fields.Company, r.Fields.Role, r.Fields.Link, r.Fields.Location, r.Fields.Source);
}

/// <summary>
/// One application flattened for export/import: the slug <c>name</c> alongside the 15
/// structured fields, in the snake_case shape the rest of the API uses. Bridges the
/// repo's <see cref="AppRecord"/> and the <see cref="AppFields"/> the importer writes.
/// </summary>
public sealed record ApplicationExport(
    string Name, string Company, string Role, string Lane, string Status, string Link,
    string Location, string Salary, string Source, string Contact, string ContactEmail,
    string Applied, string Followup, string Created, string Score, string Notes)
{
    public static ApplicationExport From(AppRecord r)
    {
        var f = r.Fields;
        return new ApplicationExport(
            r.Name, f.Company, f.Role, f.Lane, f.Status, f.Link, f.Location, f.Salary,
            f.Source, f.Contact, f.ContactEmail, f.Applied, f.Followup, f.Created, f.Score, f.Notes);
    }

    public AppFields ToFields() => new()
    {
        Company = Company, Role = Role, Lane = Lane, Status = Status, Link = Link,
        Location = Location, Salary = Salary, Source = Source, Contact = Contact,
        ContactEmail = ContactEmail, Applied = Applied, Followup = Followup,
        Created = Created, Score = Score, Notes = Notes,
    };

    /// <summary>
    /// The shared-list import view: only the public posting facts cross accounts.
    /// Status falls back to the <see cref="AppFields"/> default (<c>lead</c>) and every
    /// personal field stays blank, whatever the incoming document claims.
    /// </summary>
    public AppFields ToSharedLeadFields() => new()
    {
        Company = Company, Role = Role, Link = Link, Location = Location, Source = Source,
    };
}

/// <summary>
/// The posted import body. Every part is optional so a hand-trimmed file (just apps,
/// say) still imports; the endpoint rejects a file with nothing usable. <c>Criteria</c>
/// stays a raw <see cref="JsonElement"/> so it runs through <see cref="Criteria.FromJson"/>
/// — the same normalization the <c>/api/criteria</c> PUT applies. <c>Format</c> selects
/// the import semantics: <c>applytrack-shared</c> gets the leads-only path, anything
/// else (including absent — pre-1.3 exports) the migration upsert.
/// </summary>
public sealed record ImportDoc(
    string? Format,
    List<ApplicationExport>? Applications,
    JsonElement? Criteria,
    List<string>? Blacklist);
