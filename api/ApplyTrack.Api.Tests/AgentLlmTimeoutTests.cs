// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Agent;
using ApplyTrack.Api.Llm;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// The worker's model calls run under its own deadline (#410): the api's 120 s guards a
/// request someone is waiting on; the worker is a background job whose drafting call was
/// dying at exactly that deadline.
/// </summary>
public class AgentLlmTimeoutTests
{
    private static readonly LlmOptions Api = new()
    {
        BaseUrl = "http://llm.local/v1", Model = "m", ApiKey = "k", TimeoutSeconds = 120, DailyCapPerTenant = 50,
    };

    [Fact]
    public void The_worker_keeps_the_endpoint_and_takes_its_own_deadline()
    {
        var worker = AgentWorker.WorkerLlmOptions(Api, new AgentOptions { LlmTimeoutSeconds = 300 });

        Assert.Equal(300, worker.TimeoutSeconds);
        Assert.Equal(Api.BaseUrl, worker.BaseUrl);
        Assert.Equal(Api.Model, worker.Model);
        Assert.Equal(Api.ApiKey, worker.ApiKey);
        Assert.Equal(Api.DailyCapPerTenant, worker.DailyCapPerTenant);
        Assert.Equal(120, Api.TimeoutSeconds);                      // the api's singleton is untouched
        Assert.Equal(300, EffectiveLlmConfig.Resolve(worker, null).TimeoutSeconds);
    }

    [Fact]
    public void Zero_inherits_the_apis_deadline()
    {
        Assert.Same(Api, AgentWorker.WorkerLlmOptions(Api, new AgentOptions { LlmTimeoutSeconds = 0 }));
        Assert.Same(Api, AgentWorker.WorkerLlmOptions(Api, new AgentOptions { LlmTimeoutSeconds = 120 }));
    }

    [Fact]
    public void The_default_is_longer_than_a_request_deadline()
    {
        Assert.True(new AgentOptions().LlmTimeoutSeconds > new LlmOptions().TimeoutSeconds);
    }
}
