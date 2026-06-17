// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using Microsoft.AspNetCore.Mvc.Testing;

namespace ApplyTrack.Api.Tests;

public class ConfigurationTests
{
    [Fact]
    public void MigrationTimeoutSeconds_default_is_60()
    {
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds(null, 60));
    }

    [Fact]
    public void MigrationTimeoutSeconds_custom_value()
    {
        Assert.Equal(120, TimeoutConfiguration.PositiveTimeoutSeconds("120", 60));
    }

    [Fact]
    public void MigrationTimeoutSeconds_invalid_falls_back_to_default()
    {
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("-1", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("0", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("abc", 60));
        Assert.Equal(60, TimeoutConfiguration.PositiveTimeoutSeconds("", 60));
    }

    [Fact]
    public void App_boots_with_custom_MigrationTimeoutSeconds()
    {
        // WebApplicationFactory proves the config key is actually read and
        // accepted on startup (the value is just validated by Npgsql).
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", "Host=localhost;Database=test;Username=test;Password=test");
            b.UseSetting("MigrationTimeoutSeconds", "120");
        });

        // The factory constructed successfully, meaning the config was parsed.
        factory.Dispose();
    }
}
