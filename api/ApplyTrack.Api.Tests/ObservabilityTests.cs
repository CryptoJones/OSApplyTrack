// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Observability;
using Dapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using OpenTelemetry.Metrics;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Opt-in observability (#359): OpenTelemetry and <c>/metrics</c> are off unless configured,
/// the scrape answers only on its own port, no metric names a tenant, and
/// <c>GET /api/poll/status</c> reads the poller's per-pass records.
/// </summary>
[Collection(PostgresCollection.Name)]
public class ObservabilityTests(PostgresFixture pg) : IAsyncLifetime
{
    private const int MetricsPort = 9464;
    private const string PortHeader = "X-Test-Local-Port";
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private WebApplicationFactory<Program> Factory(Action<IWebHostBuilder>? configure = null)
    {
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            // TestServer has no sockets, so a test names the port a request "arrived on".
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new LocalPortFromHeader()));
            configure?.Invoke(b);
        });
        _factories.Add(f);
        return f;
    }

    private sealed class LocalPortFromHeader : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((ctx, go) =>
            {
                ctx.Connection.LocalPort = int.TryParse(ctx.Request.Headers[PortHeader], out var p) ? p : 8080;
                return go(ctx);
            });
            next(app);
        };
    }

    private static HttpRequestMessage Get(string path, int? port = null, string? host = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (port is { } p) req.Headers.Add(PortHeader, p.ToString());
        if (host is not null) req.Headers.Host = host;
        return req;
    }

    [Fact]
    public async Task Nothing_is_registered_and_there_is_no_metrics_endpoint_by_default()
    {
        var f = Factory();
        var client = f.CreateClient();

        Assert.Null(f.Services.GetService<MeterProvider>());
        Assert.False(f.Services.GetRequiredService<TelemetryOptions>().Enabled);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get("/metrics", MetricsPort))).StatusCode);
    }

    [Fact]
    public async Task Metrics_answer_on_their_own_port_only_and_carry_no_tenant()
    {
        var f = Factory(b => b.UseSetting("Metrics:Port", MetricsPort.ToString()));
        var client = f.CreateClient();
        // A tenant autofills a posting and its LLM call fails: neither the host nor the
        // tenant may surface in the scrape.
        await client.SendAsync(Get("/api/apps/secret-company-engineer.md"));
        AppMetrics.RecordLlmCall("tenant", TimeSpan.FromSeconds(1.5), new LlmUnavailableException("https://llm.secret-company.example failed"));
        AppMetrics.RecordAgentRun("greenhouse", "dry_run", dryRun: true);

        var scrape = await client.SendAsync(Get("/metrics", MetricsPort));
        Assert.Equal(HttpStatusCode.OK, scrape.StatusCode);
        var body = await scrape.Content.ReadAsStringAsync();
        Assert.Contains("applytrack_llm_duration_seconds", body);
        Assert.Contains("applytrack_llm_errors", body);
        Assert.Contains("error_type=\"unavailable\"", body);
        Assert.Contains("applytrack_agent_runs", body);
        Assert.DoesNotContain("secret-company", body);
        Assert.DoesNotContain("tenant_id", body);

        // The app's own port answers 404 — and a forged Host header does not open it.
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get("/metrics"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await client.SendAsync(Get("/metrics", host: $"localhost:{MetricsPort}"))).StatusCode);

        // And the metrics port serves nothing but the scrape: not the SPA, the API or the probes.
        foreach (var path in new[] { "/", "/health", "/api/auth/me", "/api/poll/status" })
            Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get(path, MetricsPort))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Get("/health"))).StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("metrics")]
    public void A_bad_metrics_port_is_refused_at_boot(string port)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Metrics:Port"] = port }).Build();
        Assert.Throws<InvalidOperationException>(() => TelemetryOptions.From(config));
    }

    [Fact]
    public void Otlp_is_on_only_with_an_endpoint()
    {
        TelemetryOptions From(params (string, string?)[] kv) => TelemetryOptions.From(
            new ConfigurationBuilder().AddInMemoryCollection(kv.ToDictionary(p => p.Item1, p => p.Item2)).Build());

        Assert.False(From().Enabled);
        Assert.False(From(("OTEL_EXPORTER_OTLP_ENDPOINT", "  ")).Otlp);
        var on = From(("OTEL_EXPORTER_OTLP_ENDPOINT", "http://collector:4317"));
        Assert.True(on.Otlp);
        Assert.False(on.Prometheus);
    }

    [Theory]
    [InlineData("http://+:8080", null, "http://+:8080;http://+:9464")]
    [InlineData(null, "8080", "http://*:8080;http://+:9464")]
    [InlineData(null, null, "http://localhost:5000;http://+:9464")]
    public void The_metrics_port_is_added_beside_the_configured_addresses(string? urls, string? ports, string expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["urls"] = urls;
        builder.Configuration["http_ports"] = ports;
        Telemetry.ListenAlsoOn(builder, MetricsPort);
        Assert.Equal(expected, builder.WebHost.GetSetting(WebHostDefaults.ServerUrlsKey));
    }

    [Fact]
    public void Library_metrics_drop_hosts_and_connection_strings()
    {
        Assert.DoesNotContain("server.address", Telemetry.TagAllowlist("System.Net.Http")!);
        Assert.DoesNotContain("db.client.connection.pool.name", Telemetry.TagAllowlist("Npgsql")!);
        Assert.Null(Telemetry.TagAllowlist(AppMetrics.MeterName));
    }

    [Fact]
    public void Traces_keep_the_route_and_the_host_but_never_a_path_or_query()
    {
        using var outbound = new Activity("out");
        Telemetry.ScrubOutbound(outbound, new Uri("https://llm.example:8443/v1/sk-secret/chat/completions?key=abc"));
        Assert.Equal("https://llm.example:8443", outbound.GetTagItem("url.full"));

        Assert.True(Telemetry.IsSecretInUrl(new Uri("https://api.telegram.org/bot123:abc/sendMessage")));
        Assert.False(Telemetry.IsSecretInUrl(new Uri("https://boards-api.greenhouse.io/v1")));

        var ctx = new DefaultHttpContext();
        ctx.SetEndpoint(new Microsoft.AspNetCore.Routing.RouteEndpoint(_ => Task.CompletedTask,
            Microsoft.AspNetCore.Routing.Patterns.RoutePatternFactory.Parse("/api/apps/{name}"), 0, null, null));
        using var inbound = new Activity("in");
        inbound.SetTag("url.path", "/api/apps/secret-company-engineer.md");
        inbound.SetTag("url.query", "?token=abc");
        Telemetry.ScrubRequest(inbound, ctx);
        Assert.Equal("/api/apps/{name}", inbound.GetTagItem("url.path"));
        Assert.Null(inbound.GetTagItem("url.query"));

        // Scrubbed at the request's start too, before any route is known: a request that
        // throws before its response keeps neither the raw path nor the query.
        using var early = new Activity("early");
        early.SetTag("url.path", "/api/apps/secret-company-engineer.md");
        early.SetTag("url.query", "?token=abc");
        Telemetry.ScrubRequest(early, new DefaultHttpContext());
        Assert.Null(early.GetTagItem("url.path"));
        Assert.Null(early.GetTagItem("url.query"));
    }

    [Fact]
    public async Task An_llm_call_is_timed_and_its_failure_counted_by_kind()
    {
        var seen = new List<(string Name, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (i, l) =>
        {
            if (i.Meter.Name == AppMetrics.MeterName && i.Name.StartsWith("applytrack.llm", StringComparison.Ordinal))
                l.EnableMeasurementEvents(i);
        };
        void Keep(Instrument i, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var d = new Dictionary<string, object?>();
            foreach (var t in tags) d[t.Key] = t.Value;
            lock (seen) seen.Add((i.Name, d));
        }
        listener.SetMeasurementEventCallback<double>((i, _, tags, _) => Keep(i, tags));
        listener.SetMeasurementEventCallback<long>((i, _, tags, _) => Keep(i, tags));
        listener.Start();

        var client = new OpenAiCompatibleLlmClient(new FailingFactory(), NullLogger<OpenAiCompatibleLlmClient>.Instance);
        await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            client.CompleteAsync("s", "u", new EffectiveLlmConfig("https://observed.llm.example/v1", "m", null, 30)));

        lock (seen)
        {
            Assert.Contains(seen, m => m.Name == "applytrack.llm.duration"
                && (string?)m.Tags["endpoint"] == "operator" && (string?)m.Tags["outcome"] == "error");
            Assert.Contains(seen, m => m.Name == "applytrack.llm.errors" && (string?)m.Tags["error.type"] == "unavailable");
            Assert.All(seen, m => Assert.DoesNotContain(m.Tags.Values, v => v?.ToString()?.Contains("observed") == true));
        }
    }

    private sealed class FailingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Failing());

        private sealed class Failing : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent("{}") });
        }
    }

    // ---- GET /api/poll/status ----------------------------------------------------------

    private async Task<(HttpClient Client, long Tenant)> SignedInAsync()
    {
        var (tenant, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = Factory().CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        return (client, tenant);
    }

    private async Task RunAsync(long t, int minutesAgo, bool atsOnly, int leads, string errors)
    {
        await using var conn = new NpgsqlConnection(pg.ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync(
            """
            INSERT INTO poll_runs (tenant_id, started_at, finished_at, ats_only, leads_added, errors)
            VALUES (@t, now() - make_interval(mins => @m + 1), now() - make_interval(mins => @m), @atsOnly, @leads, @errors::jsonb)
            """,
            new { t, m = minutesAgo, atsOnly, leads, errors });
    }

    private static async Task<JsonElement> StatusAsync(HttpClient client)
    {
        var res = await client.GetAsync("/api/poll/status");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;
    }

    private static string[] Failing(JsonElement status) =>
        status.GetProperty("failing").EnumerateArray().Select(e => e.GetProperty("source").GetString()!).Order().ToArray();

    [Fact]
    public async Task Poll_status_is_empty_before_the_first_pass_and_needs_a_session()
    {
        var (client, _) = await SignedInAsync();
        var status = await StatusAsync(client);
        Assert.Equal(JsonValueKind.Null, status.GetProperty("last_polled_at").ValueKind);
        Assert.Empty(Failing(status));

        var anonymous = Factory().CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/poll/status")).StatusCode);
    }

    [Fact]
    public async Task Poll_status_takes_the_boards_from_a_newer_fast_lane_pass_and_the_rest_from_the_full_one()
    {
        var (client, t) = await SignedInAsync();
        var (_, other) = await SignedInAsync();
        await RunAsync(t, 60, atsOnly: false, leads: 4,
            """[{"source":"remotive","error":"ConnectError"},{"source":"greenhouse:acme","error":"HTTPStatusError: 404"},{"source":"lever:globex","error":"x"}]""");
        await RunAsync(t, 12, atsOnly: true, leads: 2, """[{"source":"lever:globex","error":"ReadTimeout"}]""");
        await RunAsync(other, 1, atsOnly: false, leads: 9, """[{"source":"rss:https://other.example/feed","error":"x"}]""");

        var status = await StatusAsync(client);

        // The fast lane is the latest pass; the aggregator failure still stands from the full
        // one, the fixed board is gone, the other tenant's feed never appears.
        Assert.Equal(2, status.GetProperty("last_leads_added").GetInt32());
        var at = status.GetProperty("last_polled_at").GetDateTime();
        Assert.InRange(DateTime.UtcNow - at.ToUniversalTime(), TimeSpan.FromMinutes(11), TimeSpan.FromMinutes(13));
        Assert.Equal(new[] { "lever:globex", "remotive" }, Failing(status));
        Assert.Equal("ReadTimeout", status.GetProperty("failing").EnumerateArray()
            .Single(e => e.GetProperty("source").GetString() == "lever:globex").GetProperty("error").GetString());

        // A newer full pass answers for everything again.
        await RunAsync(t, 1, atsOnly: false, leads: 0, "[]");
        Assert.Empty(Failing(await StatusAsync(client)));
    }

    [Theory]
    [InlineData("greenhouse:acme", true)]
    [InlineData("rss:https://feed.example/x", false)]
    [InlineData("remotive", false)]
    [InlineData("linkedin", false)]
    public void Only_provider_slug_keys_are_boards(string source, bool board) =>
        Assert.Equal(board, PollRunRepo.IsBoard(source));
}
