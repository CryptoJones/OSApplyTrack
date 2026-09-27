// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Contacts and the dated notes log per application (#360): contact CRUD with the
/// optimistic lock, linking people to applications with their part in the process, the
/// notes log and its place in the timeline, encryption at rest of the personal fields,
/// tenant isolation, and the export/import round trip. Each test is a fresh tenant.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ContactsNotesTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private long _t;

    public ContactsNotesTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        var (t, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        _t = t;
        _client = Client(sid);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private HttpClient Client(string sid)
    {
        var c = _factory.CreateClient();
        c.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return c;
    }

    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<string> CreateAppAsync(string company)
    {
        var res = await _client.PostAsync("/api/apps", Json(new { company, role = "Engineer", status = "applied" }));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJsonAsync(res)).GetProperty("filename").GetString()!;
    }

    private async Task<JsonElement> CreateContactAsync(object body)
    {
        var res = await _client.PostAsync("/api/contacts", Json(body));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return await ReadJsonAsync(res);
    }

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    [Fact]
    public async Task A_contact_is_created_read_updated_under_its_version_and_deleted()
    {
        var made = await CreateContactAsync(new
        {
            name = " Rae Recruiter ", email = "rae@acme.example", phone = "+1 402 555 0100",
            role = "Technical recruiter", company = "Acme", linkedin = "https://www.linkedin.com/in/rae", notes = "Prefers email",
        });
        var id = made.GetProperty("id").GetInt64();
        Assert.Equal("Rae Recruiter", made.GetProperty("name").GetString());
        Assert.Equal("1", made.GetProperty("version").GetString());

        var got = await ReadJsonAsync(await _client.GetAsync($"/api/contacts/{id}"));
        Assert.Equal("rae@acme.example", got.GetProperty("email").GetString());
        Assert.Equal("+1 402 555 0100", got.GetProperty("phone").GetString());
        Assert.Equal(0, got.GetProperty("applications").GetArrayLength());

        var put = await _client.PutAsync($"/api/contacts/{id}?expected_version=1", Json(new { name = "Rae R.", email = "rae@new.example" }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var updated = await ReadJsonAsync(put);
        Assert.Equal("2", updated.GetProperty("version").GetString());
        Assert.Equal("", updated.GetProperty("phone").GetString());

        // A stale version is a conflict, as on applications; no version at all is last-write-wins.
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PutAsync($"/api/contacts/{id}?expected_version=1", Json(new { name = "Stale" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _client.PutAsync($"/api/contacts/{id}", Json(new { name = "Rae" }))).StatusCode);

        var list = await ReadJsonAsync(await _client.GetAsync("/api/contacts"));
        Assert.Equal("Rae", Assert.Single(list.EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsync($"/api/contacts/{id}", Json(new { name = "Ghost" }))).StatusCode);
    }

    [Fact]
    public async Task A_contact_needs_a_name_an_http_profile_link_and_fields_within_their_caps()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync("/api/contacts", Json(new { name = "  " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsync("/api/contacts", Json(new { name = "X", linkedin = "javascript:alert(1)" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsync("/api/contacts", Json(new { name = "X", phone = new string('1', 65) }))).StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(await _client.GetAsync("/api/contacts"))).GetArrayLength());
    }

    [Fact]
    public async Task People_are_linked_to_an_application_with_their_part_and_unlinked()
    {
        var app = await CreateAppAsync("Acme");
        var other = await CreateAppAsync("Globex");
        var rae = await CreateContactAsync(new { name = "Rae", email = "rae@acme.example" });
        var raeId = rae.GetProperty("id").GetInt64();

        var linked = await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = raeId, role = "recruiter" }));
        Assert.Equal(HttpStatusCode.Created, linked.StatusCode);
        // A new person made and linked in one call.
        var made = await _client.PostAsync($"/api/apps/{app}/contacts",
            Json(new { contact = new { name = "Hank Manager", email = "hank@acme.example" }, role = "hiring manager" }));
        Assert.Equal(HttpStatusCode.Created, made.StatusCode);
        var hankId = (await ReadJsonAsync(made)).GetProperty("contact").GetProperty("id").GetInt64();
        // Linking again replaces the role; the same person on a second application.
        await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = raeId, role = "sourcer" }));
        await _client.PostAsync($"/api/apps/{other}/contacts", Json(new { contact_id = raeId, role = "recruiter" }));

        var people = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/contacts"));
        Assert.Equal(["Hank Manager", "Rae"], people.EnumerateArray().Select(p => p.GetProperty("contact").GetProperty("name").GetString()));
        Assert.Equal("sourcer", people[1].GetProperty("role").GetString());
        Assert.Equal("rae@acme.example", people[1].GetProperty("contact").GetProperty("email").GetString());
        var links = (await ReadJsonAsync(await _client.GetAsync($"/api/contacts/{raeId}"))).GetProperty("applications");
        Assert.Equal(2, links.GetArrayLength());

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/apps/{app}/contacts/{raeId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/apps/{app}/contacts/{raeId}")).StatusCode);
        Assert.Single((await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/contacts"))).EnumerateArray());
        // The contact itself stays, on the other application.
        Assert.Equal(1, (await ReadJsonAsync(await _client.GetAsync($"/api/contacts/{raeId}"))).GetProperty("applications").GetArrayLength());

        // Deleting a contact takes its links; deleting the application takes the rest.
        await _client.DeleteAsync($"/api/contacts/{hankId}");
        Assert.Equal(0, (await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/contacts"))).GetArrayLength());
        await _client.DeleteAsync($"/api/apps/{other}");
        Assert.Equal(0, (await ReadJsonAsync(await _client.GetAsync($"/api/contacts/{raeId}"))).GetProperty("applications").GetArrayLength());

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/apps/nope.md/contacts")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _client.PostAsync("/api/apps/nope.md/contacts", Json(new { contact = new { name = "Orphan" } }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = 999999999L }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { role = "x" }))).StatusCode);
        // The failed inline create above left nobody behind.
        Assert.DoesNotContain(
            (await ReadJsonAsync(await _client.GetAsync("/api/contacts"))).EnumerateArray(), c => c.GetProperty("name").GetString() == "Orphan");
    }

    [Fact]
    public async Task Notes_are_logged_listed_removed_and_join_the_timeline()
    {
        var app = await CreateAppAsync("Acme");
        var first = await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "Recruiter call: salary band 150-170k", at = "2099-01-02T15:00:00Z" }));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var now = await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "Sent thank-you note" }));
        Assert.Equal(HttpStatusCode.Created, now.StatusCode);
        await _client.PostAsync($"/api/apps/{app}/interviews", Json(new { at = "2099-01-03T15:00:00Z", note = "Panel" }));

        var notes = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/notes"));
        Assert.Equal(["Sent thank-you note", "Recruiter call: salary band 150-170k"],
            notes.EnumerateArray().Select(n => n.GetProperty("body").GetString()));

        var timeline = await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/history"));
        Assert.Equal(["status", "note", "note", "interview"], timeline.EnumerateArray().Select(e => e.GetProperty("kind").GetString()));
        Assert.Equal("Recruiter call: salary band 150-170k", timeline[2].GetProperty("note").GetString());
        Assert.Equal("", timeline[2].GetProperty("to_status").GetString());

        var id = notes[0].GetProperty("id").GetInt64();
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/apps/{app}/notes/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/apps/{app}/notes/{id}")).StatusCode);
        Assert.Single((await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/notes"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "  " }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "x", at = "whenever" }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = new string('x', InputLimits.AppNote + 1) }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PostAsync("/api/apps/nope.md/notes", Json(new { body = "x" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/apps/nope.md/notes")).StatusCode);
    }

    [Fact]
    public void Notes_merge_into_the_timeline_in_time_order_after_what_happened_at_the_same_moment()
    {
        var at = new DateTime(2099, 1, 2, 15, 0, 0, DateTimeKind.Utc);
        var timeline = new List<TimelineEntry>
        {
            new("status", null, "lead", at.AddDays(-1)),
            new("status", "lead", "applied", at),
            new("followup", null, "", at.AddDays(3)),
        };
        var merged = ContactsEndpoints.WithNotes(timeline, [new AppNote(1, at, "same moment"), new AppNote(2, at.AddDays(-2), "first")]);
        Assert.Equal(["first", null, null, "same moment", null], merged.Select(e => e.Note));
        Assert.Same(timeline, ContactsEndpoints.WithNotes(timeline, []));
    }

    [Fact]
    public async Task Email_phone_profile_link_contact_notes_and_the_notes_log_are_sealed_at_rest()
    {
        var app = await CreateAppAsync("Acme");
        var c = await CreateContactAsync(new
        {
            name = "Rae", email = "rae@secret.example", phone = "555-0100", linkedin = "https://linkedin.example/in/rae", notes = "met at the meetup",
        });
        await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "the offer is 200k" }));

        await using var conn = await OwnerAsync();
        var raw = await conn.QuerySingleAsync<(string Name, string Email, string Phone, string Linkedin, string Notes)>(
            "SELECT name, email, phone, linkedin, notes FROM contacts WHERE tenant_id = @t AND id = @id",
            new { t = _t, id = c.GetProperty("id").GetInt64() });
        Assert.Equal("Rae", raw.Name);
        Assert.True(SecretProtector.IsProtected(raw.Email));
        Assert.True(SecretProtector.IsProtected(raw.Phone));
        Assert.True(SecretProtector.IsProtected(raw.Linkedin));
        Assert.True(SecretProtector.IsProtected(raw.Notes));
        Assert.DoesNotContain("secret", raw.Email);
        var body = await conn.QuerySingleAsync<string>("SELECT body FROM app_notes WHERE tenant_id = @t", new { t = _t });
        Assert.True(SecretProtector.IsProtected(body));
        Assert.DoesNotContain("200k", body);

        // A rotation's sweep moves them to the new key, and they read back under it alone.
        var rotated = new SecretProtector("rotated-key", [TestAuth.MasterKey]);
        var report = await AtRestEncryptor.SweepAsync(_pg.ConnectionString, rotated, NullLogger.Instance, onlyTenant: _t);
        Assert.Equal(new AtRestEncryptor.Report(5, 0), report);
        var only = new SecretProtector("rotated-key");
        Assert.Equal("rae@secret.example", (await new ContactRepo(conn, _t, only).ListAsync()).Single().Email);
        Assert.Equal("the offer is 200k", (await new AppNoteRepo(conn, _t, only).ListAsync(app))!.Single().Body);
        // Under a key it can't open, a contact reads with those fields blank and a note is left out.
        Assert.Equal("", (await new ContactRepo(conn, _t, TestAuth.Protector).ListAsync()).Single().Email);
        Assert.Empty((await new AppNoteRepo(conn, _t, TestAuth.Protector).ListAsync(app))!);
    }

    [Fact]
    public async Task Another_tenants_contacts_links_and_notes_cannot_be_reached()
    {
        var app = await CreateAppAsync("Acme");
        var c = await CreateContactAsync(new { name = "Rae", email = "rae@acme.example" });
        var id = c.GetProperty("id").GetInt64();
        await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = id }));
        var note = await ReadJsonAsync(await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "private" })));
        var noteId = note.GetProperty("id").GetInt64();

        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var other = Client(sid);
        Assert.Equal(0, (await ReadJsonAsync(await other.GetAsync("/api/contacts"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PutAsync($"/api/contacts/{id}", Json(new { name = "Stolen" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/contacts/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/api/apps/{app}/notes")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/apps/{app}/notes/{noteId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.DeleteAsync($"/api/apps/{app}/contacts/{id}")).StatusCode);
        // Their own application can't take this tenant's contact either.
        var theirs = await other.PostAsync("/api/apps", Json(new { company = "Initech", role = "Engineer" }));
        var theirApp = (await ReadJsonAsync(theirs)).GetProperty("filename").GetString();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/api/apps/{theirApp}/contacts", Json(new { contact_id = id }))).StatusCode);

        Assert.Equal("Rae", (await ReadJsonAsync(await _client.GetAsync($"/api/contacts/{id}"))).GetProperty("name").GetString());
        Assert.Single((await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/notes"))).EnumerateArray());
        Assert.Single((await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/contacts"))).EnumerateArray());
    }

    [Fact]
    public async Task Contacts_links_and_notes_travel_in_the_export_and_the_import_restores_them_once()
    {
        var app = await CreateAppAsync("Acme");
        var rae = await CreateContactAsync(new { name = "Rae", email = "rae@acme.example", phone = "555-0100" });
        await CreateContactAsync(new { name = "Unlinked Ursula" });
        await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = rae.GetProperty("id").GetInt64(), role = "recruiter" }));
        await _client.PostAsync($"/api/apps/{app}/notes", Json(new { body = "Phone screen went well", at = "2099-01-02T15:00:00Z" }));

        var export = await ReadJsonAsync(await _client.GetAsync("/api/account/export"));
        Assert.Equal(2, export.GetProperty("version").GetInt32());
        Assert.Equal(2, export.GetProperty("contacts").GetArrayLength());
        var link = Assert.Single(export.GetProperty("application_contacts").EnumerateArray());
        Assert.Equal(app, link.GetProperty("application").GetString());
        Assert.Equal("recruiter", link.GetProperty("role").GetString());
        var carried = Assert.Single(export.GetProperty("notes").EnumerateArray());
        Assert.Equal("Phone screen went well", carried.GetProperty("body").GetString());
        // The shared list never carries any of it.
        var shared = await (await _client.GetAsync("/api/account/export/shared")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("Rae", shared);
        Assert.DoesNotContain("Phone screen", shared);

        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        using var fresh = Client(sid);
        for (var pass = 0; pass < 2; pass++)
        {
            var imported = await fresh.PostAsync("/api/account/import", new StringContent(export.GetRawText(), Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
            var result = await ReadJsonAsync(imported);
            Assert.Equal(2, result.GetProperty("imported_contacts").GetInt32());
            Assert.Equal(1, result.GetProperty("imported_notes").GetInt32());
        }
        // Twice imported, once present.
        var contacts = await ReadJsonAsync(await fresh.GetAsync("/api/contacts"));
        Assert.Equal(["Rae", "Unlinked Ursula"], contacts.EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        Assert.Equal("555-0100", contacts[0].GetProperty("phone").GetString());
        var people = await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{app}/contacts"));
        Assert.Equal("recruiter", Assert.Single(people.EnumerateArray()).GetProperty("role").GetString());
        var notes = await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{app}/notes"));
        Assert.Equal("Phone screen went well", Assert.Single(notes.EnumerateArray()).GetProperty("body").GetString());

        // A file from before 1.65 (no such sections) leaves the notes and links alone.
        var old = JsonSerializer.Serialize(new
        {
            format = "applytrack-export", version = 2,
            applications = new[] { new { name = app, company = "Acme", role = "Engineer", status = "screen" } },
        });
        Assert.Equal(HttpStatusCode.OK, (await fresh.PostAsync("/api/account/import", new StringContent(old, Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Single((await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{app}/notes"))).EnumerateArray());
        Assert.Single((await ReadJsonAsync(await fresh.GetAsync($"/api/apps/{app}/contacts"))).EnumerateArray());
    }

    [Fact]
    public async Task An_import_refuses_a_link_to_a_contact_the_file_does_not_carry()
    {
        var app = await CreateAppAsync("Acme");
        var c = await CreateContactAsync(new { name = "Rae" });
        await _client.PostAsync($"/api/apps/{app}/contacts", Json(new { contact_id = c.GetProperty("id").GetInt64() }));
        var doc = JsonSerializer.Serialize(new
        {
            format = "applytrack-export", version = 2,
            applications = new[] { new { name = app, company = "Acme", role = "Engineer" } },
            application_contacts = new[] { new { application = app, contact = 1, role = "recruiter" } },
        });
        var res = await _client.PostAsync("/api/account/import", new StringContent(doc, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        // The existing link is untouched.
        Assert.Single((await ReadJsonAsync(await _client.GetAsync($"/api/apps/{app}/contacts"))).EnumerateArray());
    }

    [Fact]
    public async Task An_import_refuses_a_bad_contact_before_it_writes_anything()
    {
        var doc = JsonSerializer.Serialize(new
        {
            format = "applytrack-export", version = 2,
            applications = new[] { new { name = "acme-engineer.md", company = "Acme", role = "Engineer" } },
            contacts = new object[] { new { id = 1, name = "Fine" }, new { id = 2, name = "Evil", linkedin = "javascript:alert(1)" } },
        });
        var res = await _client.PostAsync("/api/account/import", new StringContent(doc, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(await _client.GetAsync("/api/contacts"))).GetArrayLength());
        Assert.Equal(0, (await ReadJsonAsync(await _client.GetAsync("/api/apps"))).GetArrayLength());
    }
}
