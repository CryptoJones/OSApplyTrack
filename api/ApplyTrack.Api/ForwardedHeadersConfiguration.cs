// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace ApplyTrack.Api;

/// <summary>
/// Builds a fail-closed forwarded-header policy. ASP.NET Core's loopback defaults
/// remain trusted for same-host proxies; every other proxy or network must be named
/// explicitly in configuration.
/// </summary>
public static class ForwardedHeadersConfiguration
{
    public static ForwardedHeadersOptions Create(IConfiguration configuration)
    {
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1,
        };

        foreach (var value in Values(configuration, "ForwardedHeaders:KnownProxies"))
        {
            if (!IPAddress.TryParse(value, out var address))
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownProxies contains invalid IP address '{value}'.");
            options.KnownProxies.Add(address);
        }

        foreach (var value in Values(configuration, "ForwardedHeaders:KnownNetworks"))
        {
            if (!System.Net.IPNetwork.TryParse(value, out var network))
                throw new InvalidOperationException(
                    $"ForwardedHeaders:KnownNetworks contains invalid CIDR network '{value}'.");
            options.KnownIPNetworks.Add(network);
        }

        return options;
    }

    private static IEnumerable<string> Values(IConfiguration configuration, string section) =>
        configuration.GetSection(section).GetChildren()
            .Select(item => item.Value?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!);
}
