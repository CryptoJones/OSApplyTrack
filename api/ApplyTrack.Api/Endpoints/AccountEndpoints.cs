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
/// </summary>
public static class AccountEndpoints
{
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

        // Import a snapshot produced by export (this or another instance). Overwrite by
        // slug — an incoming app replaces a matching local one, a new slug is added,
        // untouched local apps stay; re-importing is idempotent. The whole load runs in
        // one transaction so a mid-import failure leaves the account untouched.
        app.MapPost("/api/account/import", async (
            ImportDoc body, IDbConnection conn,
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist) =>
        {
            var hasApps = body.Applications is { Count: > 0 };
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
        [JsonPropertyOrder(-3)] public string Format => "applytrack-export";
        [JsonPropertyOrder(-2)] public int Version => 1;
        [JsonPropertyOrder(-1)] public DateTime ExportedAt => DateTime.UtcNow;
    }
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
}

/// <summary>
/// The posted import body. Every part is optional so a hand-trimmed file (just apps,
/// say) still imports; the endpoint rejects a file with nothing usable. <c>Criteria</c>
/// stays a raw <see cref="JsonElement"/> so it runs through <see cref="Criteria.FromJson"/>
/// — the same normalization the <c>/api/criteria</c> PUT applies.
/// </summary>
public sealed record ImportDoc(
    List<ApplicationExport>? Applications,
    JsonElement? Criteria,
    List<string>? Blacklist);
