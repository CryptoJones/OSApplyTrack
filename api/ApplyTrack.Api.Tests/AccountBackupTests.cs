// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Endpoints;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The complete backup (#357): export v2 carries the résumé and its PDF, the cover letters,
/// the person's own answers and the agent, LLM and notification settings — never a secret —
/// and import restores them on another account, still taking v1 files; plus the
/// formula-safe CSV of the applications.
/// </summary>
[Collection(PostgresCollection.Name)]
public class AccountBackupTests : IAsyncLifetime
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.4\n% a tiny test file\n%%EOF\n");

    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _a = null!;
    private long _ta;

    public AccountBackupTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString));
        (_ta, _a) = await SignedInAsync();
    }

    public async Task DisposeAsync()
    {
        _a.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task<(long Tenant, HttpClient Client)> SignedInAsync()
    {
        var (t, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return (t, client);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private async Task<NpgsqlConnection> OwnerAsync()
    {
        var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    // Fill account A with one of everything the backup carries, secrets included.
    private async Task SeedAsync()
    {
        Assert.Equal(HttpStatusCode.Created, (await _a.PostAsync("/api/apps",
            Json("""{"company":"Acme Corp","role":"Engineer","status":"applied"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _a.PutAsync("/api/resume",
            Json("""{"full_name":"Pat Doe","summary":"Builds things.","skills":["C#","SQL"]}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _a.PutAsync("/api/agent-settings",
            Json("""{"dry_run":false,"phone":"555-0100","salary_expectation":"150000","min_fit_score":80}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _a.PutAsync("/api/llm-settings",
            Json("""{"base_url":"https://llm.example.com/v1","model":"m-1","api_key":"sk-SECRET-KEY","cover_letter_signature":"Pat"}"""))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _a.PutAsync("/api/notifications",
            Json("""{"telegram_enabled":true,"telegram_chat_id":"987654321","telegram_bot_token":"123456:ABCdefGHIjklMNOpqrSTUvwxYZ0123456789","notify_packet_ready":false,"digest_enabled":true,"digest_hour":7,"reminder_hour":9}"""))).StatusCode);

        var protector = _factory.Services.GetRequiredService<SecretProtector>();
        await using var conn = await OwnerAsync();
        await conn.ExecuteAsync(
            "INSERT INTO cover_letters (tenant_id, application_name, body, model) VALUES (@t, 'acme-corp-engineer.md', @body, 'm-1')",
            new { t = _ta, body = protector.Protect("Dear Acme, hire me.") });
        await conn.ExecuteAsync(
            "UPDATE resume_profiles SET source_pdf = @pdf, source_pdf_name = 'pat.pdf' WHERE tenant_id = @t",
            new { t = _ta, pdf = protector.ProtectBytes(Pdf) });
        await conn.ExecuteAsync(
            """
            INSERT INTO answer_bank (tenant_id, key, label, type, answer_ciphertext, source) VALUES
                (@t, 'why us', 'Why us?', 'textarea', @mine, 'human'),
                (@t, 'years of go', 'Years of Go?', 'text', @drafted, 'agent')
            """,
            new { t = _ta, mine = protector.Protect("Because."), drafted = protector.Protect("3") });
    }

    [Fact]
    public async Task Export_v2_carries_the_whole_account_and_no_secret()
    {
        await SeedAsync();
        var res = await _a.GetAsync("/api/account/export");
        Assert.True(res.Headers.CacheControl?.NoStore);
        var text = await res.Content.ReadAsStringAsync();
        var doc = JsonDocument.Parse(text).RootElement;

        Assert.Equal(2, doc.GetProperty("version").GetInt32());
        var resume = doc.GetProperty("resume");
        Assert.Equal("Pat Doe", resume.GetProperty("full_name").GetString());
        Assert.Equal(Convert.ToBase64String(Pdf), resume.GetProperty("source_pdf").GetString());
        Assert.Equal("pat.pdf", resume.GetProperty("source_pdf_name").GetString());

        var letter = Assert.Single(doc.GetProperty("cover_letters").EnumerateArray());
        Assert.Equal("acme-corp-engineer.md", letter.GetProperty("application").GetString());
        Assert.Equal("Dear Acme, hire me.", letter.GetProperty("body").GetString());

        // Only the person's own answers travel; the agent's drafts are redrafted anyway.
        var answer = Assert.Single(doc.GetProperty("answer_bank").EnumerateArray());
        Assert.Equal("why us", answer.GetProperty("key").GetString());
        Assert.Equal("Because.", answer.GetProperty("answer").GetString());

        Assert.Equal("555-0100", doc.GetProperty("agent_settings").GetProperty("phone").GetString());
        var llm = doc.GetProperty("llm_settings");
        Assert.Equal("https://llm.example.com/v1", llm.GetProperty("base_url").GetString());
        Assert.Equal("Pat", llm.GetProperty("cover_letter_signature").GetString());
        var n = doc.GetProperty("notification_settings");
        Assert.False(n.GetProperty("notify_packet_ready").GetBoolean());
        Assert.Equal(7, n.GetProperty("digest_hour").GetInt32());
        Assert.Equal(9, n.GetProperty("reminder_hour").GetInt32());

        // No secret, and no sign of where one would go.
        Assert.DoesNotContain("sk-SECRET-KEY", text);
        Assert.DoesNotContain("ABCdefGHI", text);
        Assert.DoesNotContain("api_key", text);
        Assert.DoesNotContain("bot_token", text);
        Assert.DoesNotContain("telegram", text);
        Assert.DoesNotContain("987654321", text);
    }

    [Fact]
    public async Task Import_of_a_v2_export_restores_everything_on_another_account()
    {
        await SeedAsync();
        var exported = await _a.GetStringAsync("/api/account/export");

        var (tb, b) = await SignedInAsync();
        using (b)
        {
            var res = await b.PostAsync("/api/account/import", Json(exported));
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var summary = await ReadJsonAsync(res);
            Assert.Equal(1, summary.GetProperty("imported_cover_letters").GetInt32());
            Assert.Equal(1, summary.GetProperty("imported_answers").GetInt32());
            Assert.True(summary.GetProperty("resume_applied").GetBoolean());
            Assert.True(summary.GetProperty("agent_settings_applied").GetBoolean());
            Assert.True(summary.GetProperty("llm_settings_applied").GetBoolean());
            Assert.True(summary.GetProperty("notification_settings_applied").GetBoolean());

            // B's own export now says what A's did, section by section — the PDF included.
            var again = JsonDocument.Parse(await b.GetStringAsync("/api/account/export")).RootElement;
            var original = JsonDocument.Parse(exported).RootElement;
            foreach (var section in new[] { "resume", "cover_letters", "llm_settings", "notification_settings" })
                Assert.Equal(original.GetProperty(section).GetRawText(), again.GetProperty(section).GetRawText());
            Assert.Equal("Because.", again.GetProperty("answer_bank")[0].GetProperty("answer").GetString());
            var agent = again.GetProperty("agent_settings");
            Assert.Equal("555-0100", agent.GetProperty("phone").GetString());
            Assert.Equal(80, agent.GetProperty("min_fit_score").GetInt32());
            // The switches stay the importer's: B is still in dry run, whatever A was.
            Assert.True(agent.GetProperty("dry_run").GetBoolean());
            Assert.False(agent.GetProperty("enabled").GetBoolean());

            await using var conn = await OwnerAsync();
            Assert.Equal("human", await conn.QuerySingleAsync<string>(
                "SELECT source FROM answer_bank WHERE tenant_id = @t AND key = 'why us'", new { t = tb }));
            // No key came along, so B has none.
            var llm = await ReadJsonAsync(await b.GetAsync("/api/llm-settings"));
            Assert.False(llm.GetProperty("has_api_key").GetBoolean());

            // Importing the same file again changes nothing and doubles nothing.
            Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/account/import", Json(exported))).StatusCode);
            Assert.Equal(1, await conn.QuerySingleAsync<int>(
                "SELECT count(*)::int FROM cover_letters WHERE tenant_id = @t", new { t = tb }));
        }
    }

    [Fact]
    public async Task A_v1_file_still_imports_and_an_unknown_section_is_ignored()
    {
        var body = """
        { "format": "applytrack-export", "version": 1,
          "applications": [ { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer" } ],
          "blacklist": ["Evil Corp"], "some_future_section": { "x": 1 } }
        """;
        var res = await _a.PostAsync("/api/account/import", Json(body));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = await ReadJsonAsync(res);
        Assert.Equal(1, summary.GetProperty("imported_applications").GetInt32());
        Assert.False(summary.GetProperty("resume_applied").GetBoolean());
        Assert.False(summary.GetProperty("llm_settings_applied").GetBoolean());
    }

    [Fact]
    public async Task A_letter_for_an_application_the_file_does_not_bring_is_dropped()
    {
        var res = await _a.PostAsync("/api/account/import", Json("""
            { "applications": [ { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer" } ],
              "cover_letters": [
                { "application": "acme-corp-engineer.md", "body": "Dear Acme.", "model": "m" },
                { "application": "nowhere-inc-cto.md", "body": "Dear Nowhere.", "model": "m" } ] }
            """));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, (await ReadJsonAsync(res)).GetProperty("imported_cover_letters").GetInt32());
    }

    [Fact]
    public async Task A_stored_llm_key_stays_for_the_same_endpoint_and_never_follows_a_new_one()
    {
        await _a.PutAsync("/api/llm-settings", Json("""{"base_url":"https://llm.example.com/v1","model":"m","api_key":"sk-1"}"""));

        await _a.PostAsync("/api/account/import", Json("""{"llm_settings":{"base_url":"https://llm.example.com/v1","model":"m-2"}}"""));
        var same = await ReadJsonAsync(await _a.GetAsync("/api/llm-settings"));
        Assert.True(same.GetProperty("has_api_key").GetBoolean());
        Assert.Equal("m-2", same.GetProperty("model").GetString());

        await _a.PostAsync("/api/account/import", Json("""{"llm_settings":{"base_url":"https://elsewhere.example.net/v1"}}"""));
        var moved = await ReadJsonAsync(await _a.GetAsync("/api/llm-settings"));
        Assert.Equal("https://elsewhere.example.net/v1", moved.GetProperty("base_url").GetString());
        Assert.False(moved.GetProperty("has_api_key").GetBoolean());

        // An internal address is refused, as the PUT refuses it.
        var bad = await _a.PostAsync("/api/account/import", Json("""{"llm_settings":{"base_url":"http://localhost:11434/v1"}}"""));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task A_bad_resume_pdf_refuses_the_whole_file()
    {
        var notPdf = Convert.ToBase64String(Encoding.ASCII.GetBytes("MZ not a pdf"));
        var res = await _a.PostAsync("/api/account/import", Json($$"""
            { "applications": [ { "name": "acme-corp-engineer.md", "company": "Acme Corp", "role": "Engineer" } ],
              "resume": { "summary": "x", "source_pdf": "{{notPdf}}" } }
            """));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, (await ReadJsonAsync(await _a.GetAsync("/api/apps"))).GetArrayLength());

        var junk = await _a.PostAsync("/api/account/import", Json("""{ "resume": { "summary": "x", "source_pdf": "@@@" } }"""));
        Assert.Equal(HttpStatusCode.BadRequest, junk.StatusCode);
    }

    [Fact]
    public async Task An_api_token_exports_and_imports_without_the_llm_or_notification_settings()
    {
        await SeedAsync();
        var made = await ReadJsonAsync(await _a.PostAsync("/api/account/tokens", Json("""{"name":"script","scope":"write"}""")));
        using var bearer = _factory.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", made.GetProperty("token").GetString());

        var doc = JsonDocument.Parse(await bearer.GetStringAsync("/api/account/export")).RootElement;
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("llm_settings").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("notification_settings").ValueKind);
        Assert.Equal("Pat Doe", doc.GetProperty("resume").GetProperty("full_name").GetString());

        var res = await bearer.PostAsync("/api/account/import",
            Json("""{"llm_settings":{"base_url":"https://attacker.example.com/v1"},"notification_settings":{"email_enabled":true},"blacklist":["x"]}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var summary = await ReadJsonAsync(res);
        Assert.False(summary.GetProperty("llm_settings_applied").GetBoolean());
        Assert.False(summary.GetProperty("notification_settings_applied").GetBoolean());
        var llm = await ReadJsonAsync(await _a.GetAsync("/api/llm-settings"));
        Assert.Equal("https://llm.example.com/v1", llm.GetProperty("base_url").GetString());
        Assert.True(llm.GetProperty("has_api_key").GetBoolean());

        // The CSV is a read like the JSON export.
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync(AccountEndpoints.CsvPath)).StatusCode);
    }

    [Fact]
    public async Task The_csv_is_rfc4180_and_formula_safe()
    {
        Assert.Equal(HttpStatusCode.Created, (await _a.PostAsync("/api/apps", Json("""
            {"company":"=HYPERLINK(\"http://evil\",\"x\")","role":"Engineer, Senior","notes":"line one\nline \"two\"","salary":"-5"}
            """))).StatusCode);
        var res = await _a.GetAsync(AccountEndpoints.CsvPath);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        Assert.True(res.Headers.CacheControl?.NoStore);
        Assert.EndsWith(".csv", res.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        var bytes = await res.Content.ReadAsByteArrayAsync();
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        var text = Encoding.UTF8.GetString(bytes[3..]);

        var header = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        Assert.Equal(string.Join(',', ApplicationsCsv.Columns), header);
        Assert.Contains("\"'=HYPERLINK(\"\"http://evil\"\",\"\"x\"\")\"", text);
        Assert.Contains("\"Engineer, Senior\"", text);
        Assert.Contains(",'-5,", text);
        Assert.Contains("\"line one\nline \"\"two\"\"\"", text);
        Assert.EndsWith("\r\n", text);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void A_csv_cell_is_defused_and_quoted(string? value, string expected) =>
        Assert.Equal(expected, ApplicationsCsv.Cell(value));
}
