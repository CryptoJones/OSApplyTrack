// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Llm;
using Dapper;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Tenant-scoped meter on the operator's LLM key (#346). Each interactive model request
/// that would spend the instance <c>Llm__ApiKey</c> is counted against the tenant's UTC
/// day; past <see cref="LlmOptions.DailyCapPerTenant"/> it is refused with a 429 before
/// the model is called. A tenant on its own key or endpoint is never metered.
/// </summary>
public sealed class LlmUsageRepo
{
    private readonly IDbConnection _conn;
    private readonly long _t;
    private readonly int _cap;

    public LlmUsageRepo(IDbConnection conn, long tenantId, int dailyCap)
    {
        _conn = conn;
        _t = tenantId;
        _cap = dailyCap;
    }

    /// <summary>
    /// Count one request against today's budget, or throw <see cref="AppRateLimitedException"/>
    /// when it is spent. One upsert: the increment only lands while under the cap, so two
    /// racing requests can't both take the last slot.
    /// </summary>
    public async Task ChargeAsync(EffectiveLlmConfig cfg)
    {
        if (!cfg.InstanceKey || !cfg.IsConfigured || _cap <= 0)
            return;
        var calls = await _conn.ExecuteScalarAsync<int?>(
            """
            INSERT INTO llm_usage (tenant_id, day, calls)
            VALUES (@t, (now() AT TIME ZONE 'UTC')::date, 1)
            ON CONFLICT (tenant_id, day) DO UPDATE SET calls = llm_usage.calls + 1
            WHERE llm_usage.calls < @cap
            RETURNING calls
            """,
            new { t = _t, cap = _cap });
        if (calls is null)
            throw new AppRateLimitedException(
                $"this account has used today's {_cap} requests on the instance's AI key — "
                + "try again tomorrow, or add your own endpoint/key in Settings · AI");
    }

}
