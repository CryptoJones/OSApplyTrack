// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// <c>GET /api/errors</c> and <c>stats.errors</c> (#284): the applications that are stuck —
/// still Ready, nothing queued, last run failed — each with what happens next.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ErrorsEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private async Task<(HttpClient Client, long Tenant, NpgsqlConnection Conn)> ClientAsync()
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString));
        _factories.Add(f);
        var (tenant, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        return (client, tenant, conn);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    /// <summary>A Ready application with one piece of evidence, that many hours old.</summary>
    private static async Task<string> ReadyAsync(
        HttpClient client, NpgsqlConnection conn, long t, string company, string link, string kind, string detail, int hoursAgo = 1)
    {
        var res = await client.PostAsync("/api/apps", new StringContent(
            JsonSerializer.Serialize(new { company, role = "Engineer", score = "80", link, status = "ready" }),
            Encoding.UTF8, "application/json"));
        var name = (await ReadJson(res)).GetProperty("filename").GetString()!;
        await conn.ExecuteAsync(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail, created_at) "
            + "VALUES (@t, @name, @kind, @link, '', @detail::jsonb, now() - make_interval(hours => @hoursAgo))",
            new { t, name, kind, link, detail, hoursAgo });
        return name;
    }

    [Fact]
    public async Task Only_a_ready_application_whose_newest_run_failed_and_has_nothing_queued_is_an_error()
    {
        var (client, t, conn) = await ClientAsync();
        await using var _ = conn;
        var failed = await ReadyAsync(client, conn, t, "Failed", "https://jobs.lever.co/failed/1", "failed",
            """{"dry_run":true,"error":"no application form was found on the page — nothing to fill (no Apply button or link on the page)"}""");
        await ReadyAsync(client, conn, t, "Clean", "https://jobs.lever.co/clean/1", "dry_run", """{"dry_run":true,"unmapped":[],"error":""}""");
        // Failed, but a retry is already queued: it is in the Pipeline, not stuck.
        var queued = await ReadyAsync(client, conn, t, "Queued", "https://jobs.lever.co/queued/1", "failed", """{"dry_run":true,"error":"boom"}""");
        await conn.ExecuteAsync("INSERT INTO submit_requests (tenant_id, application_name) VALUES (@t, @queued)", new { t, queued });
        // Failed once, then ran clean: the newest evidence is what counts.
        var recovered = await ReadyAsync(client, conn, t, "Recovered", "https://jobs.lever.co/recovered/1", "failed", """{"dry_run":true,"error":"boom"}""", hoursAgo: 5);
        await conn.ExecuteAsync(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail) VALUES (@t, @recovered, 'dry_run', '', '', '{}'::jsonb)",
            new { t, recovered });

        var body = await ReadJson(await client.GetAsync("/api/errors"));

        var row = Assert.Single(body.GetProperty("errors").EnumerateArray());
        Assert.Equal(failed, row.GetProperty("name").GetString());
        Assert.Equal("Failed", row.GetProperty("company").GetString());
        Assert.Contains("no application form was found", row.GetProperty("error").GetString());
        Assert.Equal(1, row.GetProperty("runs").GetInt32());
        Assert.Equal(1, body.GetProperty("count").GetInt32());
        // The strip's number is the same list, counted.
        var stats = await ReadJson(await client.GetAsync("/api/stats"));
        Assert.Equal(1, stats.GetProperty("errors").GetInt32());
        Assert.Equal(4, stats.GetProperty("status").GetProperty("ready").GetInt32());
    }

    [Fact]
    public async Task Each_error_says_what_happens_next()
    {
        var (client, t, conn) = await ClientAsync();
        await using var _ = conn;
        var transient = await ReadyAsync(client, conn, t, "Transient", "https://jobs.lever.co/transient/1", "failed",
            """{"reason":"TimeoutException: Timeout 20000ms exceeded.","dry_run":true,"transient":true}""", hoursAgo: 2);
        var captcha = await ReadyAsync(client, conn, t, "Captcha", "https://jobs.lever.co/captcha/1", "failed",
            """{"captcha":true,"error":"this form is guarded by a captcha"}""");
        var clicked = await ReadyAsync(client, conn, t, "Clicked", "https://jobs.lever.co/clicked/1", "failed",
            """{"dry_run":false,"error":"Submit was clicked but no confirmation text was recognised"}""");
        var workday = await ReadyAsync(client, conn, t, "Workday", "https://acme.wd5.myworkdayjobs.com/Careers/job/1", "failed",
            """{"reason":"TimeoutException: x","dry_run":true,"transient":true}""");
        var givenUp = await ReadyAsync(client, conn, t, "GivenUp", "https://jobs.lever.co/givenup/1", "failed",
            """{"reason":"TimeoutException: x","dry_run":true,"transient":true}""");
        for (var i = 0; i < 2; i++)
            await conn.ExecuteAsync(
                "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail, created_at) "
                + "VALUES (@t, @givenUp, 'failed', '', '', '{\"dry_run\":true,\"transient\":true}'::jsonb, now() - interval '2 days')",
                new { t, givenUp });

        var body = await ReadJson(await client.GetAsync("/api/errors"));
        var rows = body.GetProperty("errors").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);

        Assert.Equal("retry", rows[transient].GetProperty("next").GetString());
        var retryAt = rows[transient].GetProperty("retry_at").GetDateTimeOffset();
        Assert.InRange(retryAt, DateTimeOffset.UtcNow.AddHours(3.9), DateTimeOffset.UtcNow.AddHours(4.1));
        Assert.Contains("attempt 2 of 3", rows[transient].GetProperty("why").GetString());

        Assert.Equal("you", rows[captcha].GetProperty("next").GetString());
        Assert.True(rows[captcha].GetProperty("captcha").GetBoolean());
        Assert.Equal("you", rows[clicked].GetProperty("next").GetString());
        Assert.Contains("may have gone through", rows[clicked].GetProperty("why").GetString());
        Assert.Equal("you", rows[workday].GetProperty("next").GetString());
        Assert.Contains("Workday", rows[workday].GetProperty("why").GetString());
        Assert.Equal("you", rows[givenUp].GetProperty("next").GetString());
        Assert.Contains("gave up after 3 failed runs", rows[givenUp].GetProperty("why").GetString());

        Assert.Equal(1, body.GetProperty("retrying").GetInt32());
        Assert.Equal(4, body.GetProperty("needs_you").GetInt32());
    }

    /// <summary>The view with the client holding <paramref name="tag"/>.</summary>
    private static Task<HttpResponseMessage> ErrorsSinceAsync(HttpClient client, System.Net.Http.Headers.EntityTagHeaderValue tag)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/errors");
        req.Headers.IfNoneMatch.Add(tag);
        return client.SendAsync(req);
    }

    [Fact]
    public async Task The_errors_view_answers_304_until_something_it_reads_moves()
    {
        // The SPA polls every 5 seconds (#349); an unchanged view must be a 304, and every
        // write that can change a row must change the tag.
        var (client, t, conn) = await ClientAsync();
        await using var _ = conn;
        var failed = await ReadyAsync(client, conn, t, "Etag", "https://jobs.lever.co/etag/1", "failed",
            """{"dry_run":true,"error":"boom"}""");

        var first = await client.GetAsync("/api/errors");
        Assert.Equal(System.Net.HttpStatusCode.OK, first.StatusCode);
        var tag = first.Headers.ETag!;
        Assert.Contains("private", first.Headers.CacheControl?.ToString());

        var same = await ErrorsSinceAsync(client, tag);
        Assert.Equal(System.Net.HttpStatusCode.NotModified, same.StatusCode);
        Assert.Equal(tag.Tag, same.Headers.ETag?.Tag);

        // Another account's tag is never this one's.
        var (other, _, otherConn) = await ClientAsync();
        await using var __ = otherConn;
        Assert.NotEqual(tag.Tag, (await other.GetAsync("/api/errors")).Headers.ETag?.Tag);

        async Task<System.Net.Http.Headers.EntityTagHeaderValue> Moved(int expected)
        {
            var res = await ErrorsSinceAsync(client, tag);
            Assert.Equal(System.Net.HttpStatusCode.OK, res.StatusCode);
            Assert.NotEqual(tag.Tag, res.Headers.ETag?.Tag);
            Assert.Equal(expected, (await ReadJson(res)).GetProperty("count").GetInt32());
            return res.Headers.ETag!;
        }

        // Queued: no longer stuck.
        await conn.ExecuteAsync("INSERT INTO submit_requests (tenant_id, application_name) VALUES (@t, @failed)", new { t, failed });
        tag = await Moved(0);
        // The run finished without writing evidence: stuck again.
        await conn.ExecuteAsync("UPDATE submit_requests SET done_at = now() WHERE tenant_id = @t", new { t });
        tag = await Moved(1);
        // A clean run: the newest evidence is no longer a failure.
        await conn.ExecuteAsync(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail) VALUES (@t, @failed, 'dry_run', '', '', '{}'::jsonb)",
            new { t, failed });
        tag = await Moved(0);
        // Failed again, then a fresh packet: prepared afresh, not stuck.
        await conn.ExecuteAsync(
            "INSERT INTO agent_evidence (tenant_id, application_name, kind, url, confirmation, detail, created_at) VALUES (@t, @failed, 'failed', '', '', '{}'::jsonb, now() + interval '1 second')",
            new { t, failed });
        tag = await Moved(1);
        await conn.ExecuteAsync(
            "INSERT INTO agent_events (tenant_id, application_name, kind, detail, created_at) VALUES (@t, @failed, 'packet', '{}'::jsonb, now() + interval '2 seconds')",
            new { t, failed });
        tag = await Moved(0);

        Assert.Equal(System.Net.HttpStatusCode.NotModified, (await ErrorsSinceAsync(client, tag)).StatusCode);
    }
}
