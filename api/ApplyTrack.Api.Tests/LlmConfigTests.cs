// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Tests;

public class LlmConfigTests
{
    private static readonly LlmOptions Instance = new()
    {
        BaseUrl = "https://api.instance.example/v1",
        Model = "instance-model",
        ApiKey = "instance-key",
        TimeoutSeconds = 42,
    };

    [Fact]
    public void Tenant_url_only_does_not_inherit_the_instance_key()
    {
        var cfg = EffectiveLlmConfig.Resolve(
            Instance,
            new LlmOverride("https://tenant.example/v1", "", null));

        Assert.Equal("https://tenant.example/v1", cfg.BaseUrl);
        Assert.Equal("instance-model", cfg.Model);
        Assert.Null(cfg.ApiKey);
        Assert.True(cfg.TenantBaseUrl);
    }

    [Fact]
    public void Tenant_model_only_inherits_the_instance_url_and_key()
    {
        var cfg = EffectiveLlmConfig.Resolve(
            Instance,
            new LlmOverride("", "tenant-model", null));

        Assert.Equal("https://api.instance.example/v1", cfg.BaseUrl);
        Assert.Equal("tenant-model", cfg.Model);
        Assert.Equal("instance-key", cfg.ApiKey);
        Assert.False(cfg.TenantBaseUrl);
    }

    [Fact]
    public void Tenant_key_only_replaces_the_instance_key_for_the_instance_url()
    {
        var cfg = EffectiveLlmConfig.Resolve(
            Instance,
            new LlmOverride("", "", "tenant-key"));

        Assert.Equal("https://api.instance.example/v1", cfg.BaseUrl);
        Assert.Equal("instance-model", cfg.Model);
        Assert.Equal("tenant-key", cfg.ApiKey);
        Assert.False(cfg.TenantBaseUrl);
    }

    [Fact]
    public void Full_tenant_override_uses_only_tenant_values()
    {
        var cfg = EffectiveLlmConfig.Resolve(
            Instance,
            new LlmOverride("https://tenant.example/v1", "tenant-model", "tenant-key"));

        Assert.Equal("https://tenant.example/v1", cfg.BaseUrl);
        Assert.Equal("tenant-model", cfg.Model);
        Assert.Equal("tenant-key", cfg.ApiKey);
        Assert.True(cfg.TenantBaseUrl);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://api.openai.com/v1")]
    [InlineData("http://93.184.216.34/v1")]
    public void Tenant_endpoint_policy_accepts_blank_or_public_urls(string url)
    {
        LlmEndpointPolicy.ValidateTenantBaseUrl(url);
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("ftp://example.com/v1")]
    [InlineData("http://localhost:11434/v1")]
    [InlineData("http://host.docker.internal:11434/v1")]
    [InlineData("http://model.local/v1")]
    [InlineData("http://model.internal/v1")]
    [InlineData("http://127.0.0.1:11434/v1")]
    [InlineData("http://10.0.0.5/v1")]
    [InlineData("http://172.16.0.10/v1")]
    [InlineData("http://192.168.1.10/v1")]
    [InlineData("http://169.254.169.254/latest/meta-data")]
    [InlineData("http://[::1]:11434/v1")]
    [InlineData("http://[fd00::1]/v1")]
    public void Tenant_endpoint_policy_rejects_invalid_or_internal_urls(string url)
    {
        Assert.Throws<AppValidationException>(() => LlmEndpointPolicy.ValidateTenantBaseUrl(url));
    }
}
