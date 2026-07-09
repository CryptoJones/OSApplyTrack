// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Scrape;

namespace ApplyTrack.Api.Llm;

/// <summary>
/// Validation for tenant-supplied LLM endpoints. Operator-configured instance
/// defaults are allowed to point at local/private models; tenant overrides are not.
/// The runtime client also validates resolved IPs at connect time to close DNS
/// rebinding between save and use.
/// </summary>
public static class LlmEndpointPolicy
{
    public static void ValidateTenantBaseUrl(string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return;

        if (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            || uri.Host.Length == 0)
        {
            throw new AppValidationException("LLM base URL must be an absolute http(s) URL");
        }

        var host = uri.Host.Trim('[', ']').ToLowerInvariant();
        if (host is "localhost" or "host.docker.internal"
            || host.EndsWith(".localhost", StringComparison.Ordinal)
            || host.EndsWith(".local", StringComparison.Ordinal)
            || host.EndsWith(".internal", StringComparison.Ordinal)
            || host.EndsWith(".lan", StringComparison.Ordinal)
            || host.EndsWith(".home", StringComparison.Ordinal))
        {
            throw new AppValidationException("tenant LLM base URL may not point at a local or internal host");
        }

        if (IPAddress.TryParse(host, out var ip) && JobPageFetcher.IsBlockedAddress(ip))
            throw new AppValidationException("tenant LLM base URL may not point at a private or internal address");
    }
}
