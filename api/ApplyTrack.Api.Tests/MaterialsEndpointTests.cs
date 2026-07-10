// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Llm;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The materials-engine routes over HTTP: the per-tenant résumé brief
/// (<c>/api/resume</c>), the per-tenant LLM endpoint override (<c>/api/llm-settings</c>,
/// whose API key is write-only and never echoed), and cover-letter generation on
/// <c>POST /api/apps/{name}/draft</c> with a stubbed model. Each test method runs as a
/// fresh seeded tenant on the shared container (xUnit news up the class per test), so
/// résumé / settings / letters never bleed between tests.
/// </summary>
[Collection(PostgresCollection.Name)]
public class MaterialsEndpointTests : IAsyncLifetime
{
    private const string NonEmptyResume =
        """{"full_name":"Ada Byte","headline":"Backend Engineer","summary":"Ships reliable services.","skills":["C#","Postgres"]}""";

    private readonly PostgresFixture _pg;
    private readonly List<WebApplicationFactory<Program>> _factories = [];
    private WebApplicationFactory<Program> _factory = null!; // default: real client, no LLM/secrets config
    private HttpClient _client = null!;

    public MaterialsEndpointTests(PostgresFixture pg) => _pg = pg;

    public async Task InitializeAsync()
    {
        _factory = NewFactory(null);
        _client = await AuthedClientAsync(_factory);
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        foreach (var f in _factories)
            await f.DisposeAsync();
    }

    private WebApplicationFactory<Program> NewFactory(Action<IWebHostBuilder>? configure)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", _pg.ConnectionString);
            configure?.Invoke(b);
        });
        _factories.Add(f);
        return f;
    }

    // Wires a deterministic stub model in and points the instance default at it, so a
    // draft round-trips without touching the network.
    private static Action<IWebHostBuilder> WithStub(StubLlmClient stub) => b =>
    {
        b.UseSetting("Llm:BaseUrl", "http://stub/v1");
        b.UseSetting("Llm:Model", "stub-model");
        b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ILlmClient>();
            s.AddSingleton<ILlmClient>(stub);
        });
    };

    private async Task<HttpClient> AuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var (_, sid) = await TestAuth.SeedSessionAsync(_pg.ConnectionString);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return client;
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static MultipartFormDataContent PdfForm(params string[] lines)
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.A4);
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var y = 790d;
        foreach (var line in lines)
        {
            page.AddText(line, 12, new PdfPoint(40, y), font);
            y -= 18d;
        }

        var file = new ByteArrayContent(builder.Build());
        file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
        var form = new MultipartFormDataContent();
        form.Add(file, "resume", "resume.pdf");
        return form;
    }

    private static async Task<string> CreateAppAsync(HttpClient client, string company, string role)
    {
        var res = await client.PostAsync("/api/apps",
            Json($$"""{"company":"{{company}}","role":"{{role}}"}"""));
        return (await ReadJson(res)).GetProperty("filename").GetString()!;
    }

    // ---- Résumé ------------------------------------------------------------

    [Fact]
    public async Task Resume_get_defaults_to_an_empty_profile()
    {
        var r = await ReadJson(await _client.GetAsync("/api/resume"));
        Assert.Equal("", r.GetProperty("full_name").GetString());
        Assert.Empty(r.GetProperty("experience").EnumerateArray());
        Assert.Empty(r.GetProperty("skills").EnumerateArray());
    }

    [Fact]
    public async Task Resume_put_normalizes_and_round_trips()
    {
        var payload =
            """
            { "full_name":"Ada Byte", "headline":"Backend Engineer", "location":"Remote",
              "summary":"Ships reliable services.",
              "experience":[{"company":"Globex","title":"Engineer","dates":"2020-2024",
                             "highlights":["Cut p99 latency"]}],
              "skills":["C#","c#","Postgres"],
              "certifications":["CKA"],
              "links":[{"label":"GitHub","url":"https://github.com/ada"},{"label":"no-url"}] }
            """;
        var put = await ReadJson(await _client.PutAsync("/api/resume", Json(payload)));

        // Case-insensitive dedup folds the repeated skill; a link with no url is dropped.
        Assert.Equal(new[] { "C#", "Postgres" },
            put.GetProperty("skills").EnumerateArray().Select(s => s.GetString()));
        Assert.Single(put.GetProperty("links").EnumerateArray());

        var got = await ReadJson(await _client.GetAsync("/api/resume"));
        Assert.Equal("Ada Byte", got.GetProperty("full_name").GetString());
        Assert.Equal("Globex", got.GetProperty("experience")[0].GetProperty("company").GetString());
        Assert.Equal("Cut p99 latency",
            got.GetProperty("experience")[0].GetProperty("highlights")[0].GetString());
    }

    [Fact]
    public async Task Resume_upload_pdf_stores_full_text_for_the_model()
    {
        using var form = PdfForm(
            "Ada Byte",
            "Backend Engineer",
            "Lincoln, NE | ada@example.test | github.com/adabyte",
            "Summary",
            "Ships reliable services.",
            "Skills",
            "C#; Postgres; Distributed systems",
            "Certifications",
            "CKA",
            "Experience",
            "Senior Engineer - Globex - 2020-2024",
            "Cut p99 latency");

        var uploaded = await ReadJson(await _client.PostAsync("/api/resume/upload", form));

        var text = uploaded.GetProperty("summary").GetString()!;
        Assert.Equal("", uploaded.GetProperty("full_name").GetString());
        Assert.Equal("", uploaded.GetProperty("headline").GetString());
        Assert.Equal("", uploaded.GetProperty("location").GetString());
        Assert.Empty(uploaded.GetProperty("skills").EnumerateArray());
        Assert.Empty(uploaded.GetProperty("certifications").EnumerateArray());
        Assert.Empty(uploaded.GetProperty("links").EnumerateArray());
        Assert.Empty(uploaded.GetProperty("experience").EnumerateArray());
        Assert.Contains("Ada Byte", text);
        Assert.Contains("Backend Engineer", text);
        Assert.Contains("Lincoln, NE", text);
        Assert.Contains("Skills", text);
        Assert.Contains("C#", text);
        Assert.Contains("Postgres", text);
        Assert.Contains("CKA", text);
        Assert.Contains("Cut p99 latency", text);

        var got = await ReadJson(await _client.GetAsync("/api/resume"));
        Assert.Equal(text, got.GetProperty("summary").GetString());
        Assert.Empty(got.GetProperty("experience").EnumerateArray());
    }

    [Fact]
    public async Task Resume_upload_rejects_non_pdf_bytes()
    {
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes("Ada Byte"));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        using var form = new MultipartFormDataContent();
        form.Add(file, "resume", "resume.txt");

        var res = await _client.PostAsync("/api/resume/upload", form);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("PDF", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    // ---- LLM settings ------------------------------------------------------

    [Fact]
    public async Task Llm_settings_default_view_reports_no_key_and_no_secrets_support()
    {
        var v = await ReadJson(await _client.GetAsync("/api/llm-settings"));
        Assert.Equal("", v.GetProperty("base_url").GetString());
        Assert.False(v.GetProperty("has_api_key").GetBoolean());
        Assert.False(v.GetProperty("secrets_available").GetBoolean()); // default factory sets no master key
        Assert.True(v.TryGetProperty("instance", out _));
        Assert.False(v.TryGetProperty("api_key", out _)); // the key is never part of the view
    }

    [Fact]
    public async Task Llm_settings_put_stores_a_public_endpoint_without_a_key()
    {
        var put = await ReadJson(await _client.PutAsync("/api/llm-settings",
            Json("""{"base_url":"https://api.openai.com/v1","model":"gpt-4o-mini"}""")));
        Assert.Equal("https://api.openai.com/v1", put.GetProperty("base_url").GetString());
        Assert.Equal("gpt-4o-mini", put.GetProperty("model").GetString());
        Assert.False(put.GetProperty("has_api_key").GetBoolean());

        var v = await ReadJson(await _client.GetAsync("/api/llm-settings"));
        Assert.Equal("gpt-4o-mini", v.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Llm_settings_refuses_tenant_localhost_endpoint()
    {
        var res = await _client.PutAsync("/api/llm-settings",
            Json("""{"base_url":"http://localhost:11434/v1","model":"llama3.1"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("local or internal", (await ReadJson(res)).GetProperty("detail").GetString());

        var v = await ReadJson(await _client.GetAsync("/api/llm-settings"));
        Assert.Equal("", v.GetProperty("base_url").GetString());
        Assert.Equal("", v.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Instance_default_can_still_point_at_a_local_model()
    {
        using var client = await AuthedClientAsync(NewFactory(b =>
        {
            b.UseSetting("Llm:BaseUrl", "http://localhost:11434/v1");
            b.UseSetting("Llm:Model", "llama3.1");
        }));

        var v = await ReadJson(await client.GetAsync("/api/llm-settings"));
        var instance = v.GetProperty("instance");
        Assert.Equal("http://localhost:11434/v1", instance.GetProperty("base_url").GetString());
        Assert.Equal("llama3.1", instance.GetProperty("model").GetString());
        Assert.False(v.GetProperty("has_api_key").GetBoolean());
    }

    [Fact]
    public async Task Llm_settings_refuses_a_key_when_no_master_secret_is_configured()
    {
        var res = await _client.PutAsync("/api/llm-settings",
            Json("""{"base_url":"https://api.openai.com/v1","model":"gpt-4o-mini","api_key":"sk-live-123"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("API key", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Llm_settings_stores_a_key_encrypted_and_never_echoes_it()
    {
        const string secret = "sk-super-secret-XYZ";
        using var client = await AuthedClientAsync(
            NewFactory(b => b.UseSetting("Secrets:Key", "test-master-key")));

        var putRaw = await (await client.PutAsync("/api/llm-settings",
                Json($$"""{"base_url":"https://api.openai.com/v1","model":"gpt-4o-mini","api_key":"{{secret}}"}""")))
            .Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, putRaw); // the key never travels back to the client
        Assert.True(JsonDocument.Parse(putRaw).RootElement.GetProperty("has_api_key").GetBoolean());

        var getRes = await client.GetAsync("/api/llm-settings");
        var getRaw = await getRes.Content.ReadAsStringAsync();
        Assert.DoesNotContain(secret, getRaw);
        var v = JsonDocument.Parse(getRaw).RootElement;
        Assert.True(v.GetProperty("has_api_key").GetBoolean());
        Assert.True(v.GetProperty("secrets_available").GetBoolean());
    }

    // ---- Cover-letter toggle ------------------------------------------------

    [Fact]
    public async Task Cover_letters_default_on_and_the_toggle_round_trips()
    {
        // No llm_settings row yet: the engine defaults ON.
        var v = await ReadJson(await _client.GetAsync("/api/llm-settings"));
        Assert.True(v.GetProperty("cover_letters_enabled").GetBoolean());

        // Turn it off; the PUT echoes the new state.
        var put = await ReadJson(await _client.PutAsync("/api/llm-settings",
            Json("""{"cover_letters_enabled":false}""")));
        Assert.False(put.GetProperty("cover_letters_enabled").GetBoolean());

        // A later PUT that omits the field leaves the stored toggle alone —
        // the same omitted-means-keep semantics as api_key.
        await _client.PutAsync("/api/llm-settings", Json("""{"model":"llama3.1"}"""));
        var after = await ReadJson(await _client.GetAsync("/api/llm-settings"));
        Assert.False(after.GetProperty("cover_letters_enabled").GetBoolean());
        Assert.Equal("llama3.1", after.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Cover_letter_signature_round_trips_and_is_appended_to_new_drafts()
    {
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        await client.PutAsync("/api/resume", Json(NonEmptyResume));
        await client.PutAsync("/api/llm-settings", Json("""{"cover_letter_signature":"Sincerely,\nAda Byte"}"""));
        var settings = await ReadJson(await client.GetAsync("/api/llm-settings"));
        Assert.Equal("Sincerely,\nAda Byte", settings.GetProperty("cover_letter_signature").GetString());

        var name = await CreateAppAsync(client, "Acme Corp", "Engineer");
        var draft = await ReadJson(await client.PostAsync($"/api/apps/{name}/draft", null));
        Assert.EndsWith("Sincerely,\nAda Byte", draft.GetProperty("material").GetString());
    }

    [Fact]
    public async Task Cover_letter_pdf_download_returns_a_pdf()
    {
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        await client.PutAsync("/api/resume", Json(NonEmptyResume));
        var name = await CreateAppAsync(client, "Acme Corp", "Engineer");
        await client.PostAsync($"/api/apps/{name}/draft", null);

        var pdf = await client.GetAsync($"/api/apps/{name}/cover-letter.pdf");
        Assert.Equal(HttpStatusCode.OK, pdf.StatusCode);
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.Equal("%PDF-", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..5]));
    }

    [Fact]
    public async Task Draft_is_refused_when_cover_letters_are_disabled()
    {
        // Fully configured (stub model + résumé) — the toggle alone must refuse.
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        await client.PutAsync("/api/resume", Json(NonEmptyResume));
        await client.PutAsync("/api/llm-settings", Json("""{"cover_letters_enabled":false}"""));
        var name = await CreateAppAsync(client, "Acme Corp", "Engineer");

        var res = await client.PostAsync($"/api/apps/{name}/draft", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("turned off", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    // ---- Drafting ----------------------------------------------------------

    [Fact]
    public async Task Draft_without_a_resume_is_rejected_before_the_model()
    {
        var name = await CreateAppAsync(_client, "Acme Corp", "Engineer");
        var res = await _client.PostAsync($"/api/apps/{name}/draft", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("résumé", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Draft_without_a_configured_endpoint_returns_a_clear_502()
    {
        await _client.PutAsync("/api/resume", Json(NonEmptyResume));
        var name = await CreateAppAsync(_client, "Beta LLC", "Dev");

        var res = await _client.PostAsync($"/api/apps/{name}/draft", null);
        Assert.Equal(HttpStatusCode.BadGateway, res.StatusCode);
        Assert.Contains("LLM endpoint", (await ReadJson(res)).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Draft_generates_saves_and_surfaces_the_letter_as_material()
    {
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        await client.PutAsync("/api/resume", Json(NonEmptyResume));
        var name = await CreateAppAsync(client, "Acme Corp", "Engineer");

        var draft = await ReadJson(await client.PostAsync($"/api/apps/{name}/draft", null));
        Assert.True(draft.GetProperty("ok").GetBoolean());
        Assert.Equal(StubLlmClient.DefaultBody, draft.GetProperty("material").GetString());

        // The saved letter surfaces on the next app-detail GET.
        var detail = await ReadJson(await client.GetAsync($"/api/apps/{name}"));
        Assert.Equal(StubLlmClient.DefaultBody, detail.GetProperty("material").GetString());
    }

    [Fact]
    public async Task Cover_letters_are_isolated_between_tenants()
    {
        var factory = NewFactory(WithStub(new StubLlmClient()));
        using var a = await AuthedClientAsync(factory);
        using var b = await AuthedClientAsync(factory);

        // Tenant A drafts a letter for its app.
        await a.PutAsync("/api/resume", Json(NonEmptyResume));
        var name = await CreateAppAsync(a, "Acme Corp", "Engineer");
        Assert.Equal(HttpStatusCode.OK, (await a.PostAsync($"/api/apps/{name}/draft", null)).StatusCode);
        var aDetail = await ReadJson(await a.GetAsync($"/api/apps/{name}"));
        Assert.NotEqual("", aDetail.GetProperty("material").GetString());

        // Tenant B owns the same-slug app but never drafted — it sees no letter.
        var bName = await CreateAppAsync(b, "Acme Corp", "Engineer");
        Assert.Equal(name, bName); // same slug, different tenant
        var bDetail = await ReadJson(await b.GetAsync($"/api/apps/{bName}"));
        Assert.Equal("", bDetail.GetProperty("material").GetString());
    }

    [Fact]
    public async Task Cover_letter_can_be_discarded_then_is_gone()
    {
        using var client = await AuthedClientAsync(NewFactory(WithStub(new StubLlmClient())));
        await client.PutAsync("/api/resume", Json(NonEmptyResume));
        var name = await CreateAppAsync(client, "Acme Corp", "Engineer");
        await client.PostAsync($"/api/apps/{name}/draft", null);

        Assert.Equal(HttpStatusCode.NoContent,
            (await client.DeleteAsync($"/api/apps/{name}/cover-letter")).StatusCode);
        var detail = await ReadJson(await client.GetAsync($"/api/apps/{name}"));
        Assert.Equal("", detail.GetProperty("material").GetString());

        // Nothing left to discard.
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.DeleteAsync($"/api/apps/{name}/cover-letter")).StatusCode);
    }
}
