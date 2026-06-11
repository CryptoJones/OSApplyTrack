// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The shareable opportunity list over HTTP — the peer-facing sibling of the private
/// migration snapshot. <c>GET /api/account/export/shared</c> must emit only the facts
/// of the posting (slug, company, role, link, location, source) under the
/// <c>applytrack-shared</c> format; <c>POST /api/account/import</c> of that format
/// must land every entry as a fresh <c>lead</c>, skip slugs the importer already
/// tracks, and drop any personal state a doctored file smuggles in.
/// </summary>
[Collection(PostgresCollection.Name)]
public class SharedExportTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public SharedExportTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));

        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Shared_export_carries_posting_facts_only_under_its_own_format()
    {
        // An application loaded with personal state, none of which may leave.
        await _client.PostAsync("/api/apps", Json("""
            { "company": "Acme Corp", "role": "Engineer", "status": "applied",
              "link": "https://acme.example/job", "location": "Remote",
              "source": "greenhouse", "salary": "200k", "score": "88",
              "contact": "Jane Recruiter", "contact_email": "jane@acme.example",
              "applied": "2026-06-01", "followup": "2026-06-15",
              "notes": "asked about visa sponsorship" }
            """));

        var res = await _client.GetAsync("/api/account/export/shared");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("applytrack-shared",
            res.Content.Headers.ContentDisposition?.FileName ?? "");

        var doc = await ReadJsonAsync(res);
        Assert.Equal("applytrack-shared", doc.GetProperty("format").GetString());
        Assert.Equal(1, doc.GetProperty("version").GetInt32());

        var apps = doc.GetProperty("applications");
        Assert.Equal(1, apps.GetArrayLength());
        var app = apps[0];
        Assert.Equal("acme-corp-engineer.md", app.GetProperty("name").GetString());
        Assert.Equal("Acme Corp", app.GetProperty("company").GetString());
        Assert.Equal("Engineer", app.GetProperty("role").GetString());
        Assert.Equal("https://acme.example/job", app.GetProperty("link").GetString());
        Assert.Equal("Remote", app.GetProperty("location").GetString());
        Assert.Equal("greenhouse", app.GetProperty("source").GetString());

        // Personal state has no key at all — stripped, not blanked.
        foreach (var personal in new[]
                 { "status", "salary", "score", "contact", "contact_email",
                   "applied", "followup", "created", "notes", "lane" })
            Assert.False(app.TryGetProperty(personal, out _),
                $"shared export must not carry '{personal}'");

        // Settings stay home too: no criteria, no blacklist in the shared shape.
        Assert.False(doc.TryGetProperty("criteria", out _));
        Assert.False(doc.TryGetProperty("blacklist", out _));
    }

    [Fact]
    public async Task Shared_import_lands_entries_as_leads_and_drops_smuggled_personal_state()
    {
        // A doctored file claims a status and notes; the leads-only path ignores both.
        var body = """
        {
          "format": "applytrack-shared", "version": 1,
          "applications": [
            { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer",
              "link": "https://acme.example/job", "location": "Remote", "source": "peer",
              "status": "offer", "notes": "smuggled", "contact": "someone" }
          ]
        }
        """;
        var res = await _client.PostAsync("/api/account/import", Json(body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = await ReadJsonAsync(res);
        Assert.Equal(1, summary.GetProperty("imported_applications").GetInt32());
        Assert.Equal(0, summary.GetProperty("skipped_applications").GetInt32());

        var app = await ReadJsonAsync(await _client.GetAsync("/api/apps/acme-corp-engineer.md"));
        var fields = app.GetProperty("fields");
        Assert.Equal("lead", fields.GetProperty("status").GetString());
        Assert.Equal("Acme Corp", fields.GetProperty("company").GetString());
        Assert.Equal("https://acme.example/job", fields.GetProperty("link").GetString());
        Assert.Equal("", fields.GetProperty("notes").GetString());
        Assert.Equal("", fields.GetProperty("contact").GetString());
        Assert.Equal("", fields.GetProperty("applied").GetString());
    }

    [Fact]
    public async Task Shared_import_skips_slugs_the_importer_already_tracks()
    {
        // The importer is mid-pipeline on Acme; a peer's list mentioning the same slug
        // must not touch it.
        await _client.PostAsync("/api/apps", Json(
            """{"company":"Acme Corp","role":"Engineer","status":"onsite","notes":"my prep"}"""));

        var body = """
        {
          "format": "applytrack-shared",
          "applications": [
            { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer" },
            { "name": "globex-developer.md", "company": "Globex", "role": "Developer",
              "link": "https://globex.example/careers" }
          ]
        }
        """;
        var summary = await ReadJsonAsync(await _client.PostAsync("/api/account/import", Json(body)));
        Assert.Equal(1, summary.GetProperty("imported_applications").GetInt32());
        Assert.Equal(1, summary.GetProperty("skipped_applications").GetInt32());

        // The tracked app kept its state; only the new slug arrived, as a lead.
        var mine = await ReadJsonAsync(await _client.GetAsync("/api/apps/acme-corp-engineer.md"));
        Assert.Equal("onsite", mine.GetProperty("fields").GetProperty("status").GetString());
        Assert.Equal("my prep", mine.GetProperty("fields").GetProperty("notes").GetString());

        var theirs = await ReadJsonAsync(await _client.GetAsync("/api/apps/globex-developer.md"));
        Assert.Equal("lead", theirs.GetProperty("fields").GetProperty("status").GetString());

        var all = await ReadJsonAsync(await _client.GetAsync("/api/apps"));
        Assert.Equal(2, all.GetArrayLength());
    }

    [Fact]
    public async Task Shared_export_then_import_round_trips_between_tenants_as_leads()
    {
        // Tenant A (this _client) tracks an app deep in the pipeline and shares a list.
        await _client.PostAsync("/api/apps", Json("""
            { "company": "Acme Corp", "role": "Engineer", "status": "offer",
              "link": "https://acme.example/job", "location": "Remote",
              "source": "greenhouse", "notes": "private" }
            """));
        var shared = await (await _client.GetAsync("/api/account/export/shared"))
            .Content.ReadAsStringAsync();

        // Tenant B imports A's bytes and gets a clean lead, nothing of A's state.
        var (_, sidB) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var clientB = _factory.CreateClient();
        clientB.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sidB}");

        var summary = await ReadJsonAsync(
            await clientB.PostAsync("/api/account/import", Json(shared)));
        Assert.Equal(1, summary.GetProperty("imported_applications").GetInt32());

        var app = await ReadJsonAsync(await clientB.GetAsync("/api/apps/acme-corp-engineer.md"));
        var fields = app.GetProperty("fields");
        Assert.Equal("lead", fields.GetProperty("status").GetString());
        Assert.Equal("https://acme.example/job", fields.GetProperty("link").GetString());
        Assert.Equal("", fields.GetProperty("notes").GetString());
    }

    [Fact]
    public async Task Shared_import_with_no_applications_is_rejected()
    {
        var res = await _client.PostAsync("/api/account/import",
            Json("""{ "format": "applytrack-shared", "applications": [] }"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        var detail = (await ReadJsonAsync(res)).GetProperty("detail").GetString();
        Assert.Contains("no importable data", detail);
    }
}
