// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Middleware;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;

namespace ApplyTrack.Api.Tests;

/// <summary>Boundary and over-limit coverage for ordinary authenticated API input.</summary>
[Collection(PostgresCollection.Name)]
public class InputLimitTests : IAsyncLifetime
{
    private readonly PostgresFixture _pg;
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public InputLimitTests(PostgresFixture pg) => _pg = pg;

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

    private static StringContent Json(object body) =>
        Json(JsonSerializer.Serialize(body));

    private static async Task<string> Detail(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("detail").GetString() ?? "";
    }

    private static StringContent SizedJson(int bytes)
    {
        const string prefix = "{\"padding\":\"";
        const string suffix = "\"}";
        var body = prefix + new string('x', bytes - prefix.Length - suffix.Length) + suffix;
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(body));
        return Json(body);
    }

    [Fact]
    public async Task Json_body_accepts_the_exact_global_limit()
    {
        var response = await _client.PostAsync(
            "/api/poll", SizedJson((int)JsonBodyLimitMiddleware.MaxBytes));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Json_body_rejects_one_byte_over_with_detail()
    {
        var response = await _client.PostAsync(
            "/api/poll", SizedJson((int)JsonBodyLimitMiddleware.MaxBytes + 1));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("1024 KiB", await Detail(response));
    }

    [Fact]
    public async Task Application_accepts_field_and_notes_boundaries()
    {
        var response = await _client.PostAsync("/api/apps", Json(new
        {
            company = new string('c', InputLimits.Company),
            role = "Engineer",
            notes = new string('n', InputLimits.Notes),
        }));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("company")]
    [InlineData("notes")]
    public async Task Application_rejects_overlong_fields_with_detail(string field)
    {
        var payload = field == "company"
            ? new { company = new string('c', InputLimits.Company + 1), notes = "" }
            : new { company = "Acme", notes = new string('n', InputLimits.Notes + 1) };

        var response = await _client.PostAsync("/api/apps", Json(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await Detail(response));
    }

    [Fact]
    public async Task Criteria_accepts_collection_boundaries()
    {
        var response = await _client.PutAsync("/api/criteria", Json(new
        {
            keywords = Enumerable.Range(0, InputLimits.Keywords).Select(i => $"keyword-{i}"),
            exclude_locations = Enumerable.Range(0, InputLimits.ExcludedLocations)
                .Select(i => $"location-{i}"),
            ats_boards = Enumerable.Range(0, InputLimits.AtsBoards)
                .Select(i => new { provider = "greenhouse", slug = $"company-{i}" }),
            rss_feeds = Enumerable.Range(0, InputLimits.RssFeeds)
                .Select(i => $"https://feeds.example/{i}.rss"),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("keywords")]
    [InlineData("exclude_locations")]
    [InlineData("ats_boards")]
    [InlineData("rss_feeds")]
    public async Task Criteria_rejects_over_cardinality_with_detail(string field)
    {
        object payload = field switch
        {
            "rss_feeds" => new
            {
                rss_feeds = Enumerable.Range(0, InputLimits.RssFeeds + 1)
                    .Select(i => $"https://feeds.example/{i}.rss"),
            },
            "keywords" => new
            {
                keywords = Enumerable.Range(0, InputLimits.Keywords + 1)
                    .Select(i => $"keyword-{i}"),
            },
            "exclude_locations" => new
            {
                exclude_locations = Enumerable.Range(0, InputLimits.ExcludedLocations + 1)
                    .Select(i => $"location-{i}"),
            },
            _ => new
            {
                ats_boards = Enumerable.Range(0, InputLimits.AtsBoards + 1)
                    .Select(i => new { provider = "lever", slug = $"company-{i}" }),
            },
        };

        var response = await _client.PutAsync("/api/criteria", Json(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await Detail(response));
    }

    [Fact]
    public async Task Resume_accepts_collection_boundaries()
    {
        var response = await _client.PutAsync("/api/resume", Json(new
        {
            experience = Enumerable.Range(0, InputLimits.ResumeExperience).Select(i => new
            {
                company = $"Company {i}",
                title = "Engineer",
                highlights = Enumerable.Range(0, InputLimits.ResumeHighlights)
                    .Select(h => $"Highlight {h}"),
            }),
            skills = Enumerable.Range(0, InputLimits.ResumeSkills).Select(i => $"Skill {i}"),
            certifications = Enumerable.Range(0, InputLimits.ResumeCertifications)
                .Select(i => $"Certification {i}"),
            links = Enumerable.Range(0, InputLimits.ResumeLinks)
                .Select(i => new { label = $"Link {i}", url = $"https://example.com/{i}" }),
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("experience")]
    [InlineData("highlights")]
    [InlineData("skills")]
    [InlineData("certifications")]
    [InlineData("links")]
    public async Task Resume_rejects_over_cardinality_with_detail(string field)
    {
        object payload = field switch
        {
            "experience" => new
            {
                experience = Enumerable.Range(0, InputLimits.ResumeExperience + 1)
                    .Select(i => new { company = $"Company {i}" }),
            },
            "highlights" => new
            {
                experience = new[]
                {
                    new
                    {
                        company = "Acme",
                        highlights = Enumerable.Range(0, InputLimits.ResumeHighlights + 1)
                            .Select(i => $"Highlight {i}"),
                    },
                },
            },
            "skills" => new
            {
                skills = Enumerable.Range(0, InputLimits.ResumeSkills + 1)
                    .Select(i => $"Skill {i}"),
            },
            "certifications" => new
            {
                certifications = Enumerable.Range(0, InputLimits.ResumeCertifications + 1)
                    .Select(i => $"Certification {i}"),
            },
            _ => new
            {
                links = Enumerable.Range(0, InputLimits.ResumeLinks + 1)
                    .Select(i => new { url = $"https://example.com/{i}" }),
            },
        };

        var response = await _client.PutAsync("/api/resume", Json(payload));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(field, await Detail(response));
    }
}
