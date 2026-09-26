// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Auth;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Compression is scoped (#352): the SPA's files and the three polled lists go out
/// Brotli/gzip; every other API response stays plain so none can become a BREACH oracle.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ResponseCompressionTests(PostgresFixture pg) : IAsyncLifetime
{
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString));
        var (_, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Theory]
    [InlineData("/app.js", "br")]
    [InlineData("/app.css", "gzip")]
    [InlineData("/api/apps", "br")]
    [InlineData("/api/pipeline", "gzip")]
    [InlineData("/api/errors", "br")]
    public async Task Static_files_and_the_polled_lists_are_compressed(string path, string encoding)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.AcceptEncoding.ParseAdd(encoding);
        var res = await _client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Contains(encoding, res.Content.Headers.ContentEncoding);
    }

    [Theory]
    [InlineData("/api/auth/me")]
    [InlineData("/api/resume")]
    public async Task Other_api_responses_are_not(string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.AcceptEncoding.ParseAdd("br, gzip");
        var res = await _client.SendAsync(req);

        res.EnsureSuccessStatusCode();
        Assert.Empty(res.Content.Headers.ContentEncoding);
    }
}
