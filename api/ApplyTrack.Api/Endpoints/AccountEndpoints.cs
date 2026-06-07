// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.IO.Compression;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Account-level self-service: export everything as a zip, or delete the whole
/// account. Both replace billing in the OSS build — good citizenship (a real backup
/// story) and the right to walk away with your data. Both are under <c>/api</c> so the
/// tenancy choke-point already requires a session; the repos arrive pre-scoped, so
/// these only ever touch the caller's own tenant.
/// </summary>
public static class AccountEndpoints
{
    // The export's settings.json uses the same snake_case shape as /api/criteria, so a
    // re-import (future) or a human reads the familiar keys. Dictionary keys (source
    // ids) are left to pass through verbatim, matching the global JSON policy.
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // A zip backup: one Markdown file per application (the same frontmatter+body the
        // SPA's raw editor round-trips) plus a settings.json holding the search criteria
        // and blacklist. Built in memory — a self-host export is small.
        app.MapGet("/api/account/export", async (
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist) =>
        {
            var records = await apps.ExportAllAsync();
            var settings = new
            {
                criteria = await criteria.GetAsync(),
                blacklist = await blacklist.ListAsync(),
            };

            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var rec in records)
                {
                    // rec.Name is the slug, already ending in ".md".
                    var entry = zip.CreateEntry($"applications/{rec.Name}", CompressionLevel.Optimal);
                    await using var writer = new StreamWriter(entry.Open());
                    await writer.WriteAsync(MarkdownCodec.Render(rec.Fields));
                }

                var settingsEntry = zip.CreateEntry("settings.json", CompressionLevel.Optimal);
                await using var settingsWriter = new StreamWriter(settingsEntry.Open());
                await settingsWriter.WriteAsync(JsonSerializer.Serialize(settings, ExportJson));
            }

            var filename = $"applytrack-export-{DateTime.UtcNow:yyyy-MM-dd}.zip";
            return Results.File(buffer.ToArray(), "application/zip", filename);
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
}
