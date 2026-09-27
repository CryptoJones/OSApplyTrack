// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Diagnostics;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ApplyTrack.Api.Observability;

/// <summary>
/// Opt-in observability (#359). Off by default — nothing is registered and nothing leaves
/// the process — and each half is switched on by its own setting:
/// <list type="bullet">
/// <item><c>OTEL_EXPORTER_OTLP_ENDPOINT</c> (the standard OpenTelemetry variable): ASP.NET
/// Core, HttpClient and Npgsql traces plus the metrics below, pushed over OTLP to the
/// operator's collector. The other <c>OTEL_*</c> variables (protocol, headers, service
/// name) are honored as the SDK reads them.</item>
/// <item><c>Metrics__Port</c>: a Prometheus <c>/metrics</c> served on that port and
/// <em>only</em> that port — the app's own port answers 404 for it — so it is reachable
/// by whoever can reach that port (publish it to the scraper's network, never the
/// internet) and never through the reverse proxy in front of the app.</item>
/// </list>
/// Metrics carry no tenant data (see <see cref="AppMetrics"/>). Traces carry the route
/// template in place of the raw path and no query string; outbound spans keep only the
/// scheme and host; Telegram, whose bot token is in the URL, is not traced.
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>The OTLP collector endpoint, or empty for no export.</summary>
    public string OtlpEndpoint { get; init; } = "";

    /// <summary>The port <c>/metrics</c> is served on, or 0 for none.</summary>
    public int MetricsPort { get; init; }

    public bool Otlp => OtlpEndpoint.Length > 0;
    public bool Prometheus => MetricsPort > 0;
    public bool Enabled => Otlp || Prometheus;

    public static TelemetryOptions From(IConfiguration config)
    {
        var port = 0;
        var raw = config["Metrics:Port"];
        if (!string.IsNullOrWhiteSpace(raw)
            && (!int.TryParse(raw, out port) || port is < 1 or > 65535))
            throw new InvalidOperationException($"Metrics:Port must be a TCP port (1-65535), not '{raw}'.");
        return new TelemetryOptions
        {
            OtlpEndpoint = (config["OTEL_EXPORTER_OTLP_ENDPOINT"] ?? "").Trim(),
            MetricsPort = port,
        };
    }
}

public static class Telemetry
{
    public const string MetricsPath = "/metrics";

    /// <summary>Register the OpenTelemetry SDK when (and only when) something is configured.</summary>
    public static void AddApplyTrackTelemetry(this WebApplicationBuilder builder, TelemetryOptions options, string serviceName)
    {
        builder.Services.AddSingleton(options);
        if (!options.Enabled)
            return;

        if (options.Prometheus)
            ListenAlsoOn(builder, options.MetricsPort);

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(
                serviceName: builder.Configuration["OTEL_SERVICE_NAME"] is { Length: > 0 } named ? named : serviceName,
                serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString(3)));

        otel.WithMetrics(m =>
        {
            m.AddMeter(AppMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter("Npgsql")
                .AddView(instrument => TagAllowlist(instrument.Meter.Name) is { } keep
                    ? new MetricStreamConfiguration { TagKeys = keep }
                    : null);
            if (options.Otlp) m.AddOtlpExporter();
            if (options.Prometheus) m.AddPrometheusExporter();
        });

        // Traces go only to a collector; a scrape endpoint has nowhere to put them.
        if (options.Otlp)
            otel.WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = ctx => !ctx.Request.Path.StartsWithSegments(MetricsPath);
                    o.EnrichWithHttpResponse = (activity, response) => ScrubRequest(activity, response.HttpContext);
                })
                .AddHttpClientInstrumentation(o =>
                {
                    o.FilterHttpRequestMessage = req => !IsSecretInUrl(req.RequestUri);
                    o.EnrichWithHttpRequestMessage = (activity, req) => ScrubOutbound(activity, req.RequestUri);
                })
                .AddNpgsql()
                .AddOtlpExporter());
    }

    /// <summary>Serve <c>/metrics</c> on the metrics port only. Placed before everything else,
    /// so the scrape never meets the session gate, and a request for it on any other port
    /// falls through to a plain 404.</summary>
    public static void UseApplyTrackMetricsEndpoint(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<TelemetryOptions>();
        if (!options.Prometheus)
            return;
        app.UseOpenTelemetryPrometheusScrapingEndpoint(ctx => IsScrape(ctx, options.MetricsPort));
    }

    /// <summary>The port the request arrived on, not its Host header: a Host header is the
    /// caller's to write, so it cannot open the endpoint on the public port.</summary>
    public static bool IsScrape(HttpContext ctx, int metricsPort) =>
        ctx.Connection.LocalPort == metricsPort && ctx.Request.Path == MetricsPath;

    /// <summary>Keep Kestrel's configured addresses and add the metrics port beside them.
    /// Listening in code would replace ASPNETCORE_URLS rather than add to it.</summary>
    public static void ListenAlsoOn(WebApplicationBuilder builder, int port)
    {
        var urls = builder.Configuration["urls"];
        if (string.IsNullOrWhiteSpace(urls) && builder.Configuration["http_ports"] is { Length: > 0 } ports)
            urls = string.Join(';', ports.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(p => $"http://*:{p}"));
        if (string.IsNullOrWhiteSpace(urls))
            urls = "http://localhost:5000";
        builder.WebHost.UseUrls($"{urls};http://+:{port}");
    }

    /// <summary>
    /// The tags a library's metrics may keep. HttpClient's carry <c>server.address</c> — the
    /// host of a posting a tenant autofilled, or of a tenant's own LLM — and Npgsql's carry
    /// the pool name, which is the connection string: both are dropped, leaving only method,
    /// status, error and state. Null for the meters that need no filter (ours, and ASP.NET
    /// Core's, which tag by route template).
    /// </summary>
    public static string[]? TagAllowlist(string meter) => meter switch
    {
        "System.Net.Http" => ["http.request.method", "http.response.status_code", "error.type", "network.protocol.version"],
        "Npgsql" => ["db.client.connection.state"],
        _ => null,
    };

    /// <summary>A trace keeps the route template (<c>/api/apps/{name}</c>), not the path a
    /// tenant's slug or a feed token sits in, and never the query string.</summary>
    public static void ScrubRequest(Activity activity, HttpContext ctx)
    {
        if (ctx.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
            activity.SetTag("url.path", route.StartsWith('/') ? route : "/" + route);
        else
            activity.SetTag("url.path", null);
        activity.SetTag("url.query", null);
    }

    /// <summary>An outbound span keeps the scheme, host and port only. A tenant's LLM base URL
    /// or feed URL is free text and can carry a key in its path, like a Telegram bot token.</summary>
    public static void ScrubOutbound(Activity activity, Uri? uri) =>
        activity.SetTag("url.full", uri is { IsAbsoluteUri: true }
            ? uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            : null);

    /// <summary>Outbound calls whose URL itself is a credential: the Telegram Bot API. Not
    /// traced at all, so not even a scrubbed span says a message went out.</summary>
    public static bool IsSecretInUrl(Uri? uri) =>
        uri is not null && string.Equals(uri.Host, "api.telegram.org", StringComparison.OrdinalIgnoreCase);
}
