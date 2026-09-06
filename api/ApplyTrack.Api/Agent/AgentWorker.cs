// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Notifications;
using Dapper;
using Npgsql;

namespace ApplyTrack.Api.Agent;

/// <summary>
/// The agent worker — the first background service in the API project. On each pass
/// it fans out over the tenants that have switched the agent on, takes a per-tenant
/// advisory lock (its own prefix, so it never contends with the Python poller's
/// <c>applytrack:tenant-poll:</c>), and judges that tenant's best unjudged leads. In
/// this increment it <b>evaluates and records, but stages nothing</b>: the only
/// write is an <c>agent_events</c> row per lead, which is what makes it safe to run
/// against real leads.
///
/// It owns a separate, small <see cref="NpgsqlDataSource"/>: a pass holds a
/// connection across multi-minute model calls, and the request pool must never be
/// starved by it. The intended deployment is a second container from the same image
/// with <c>Agent__Enabled=true</c> and no published port.
/// </summary>
public sealed class AgentWorker : BackgroundService
{
    private const string LockPrefix = "applytrack:agent:";
    private static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);

    private readonly NpgsqlDataSource _db;
    private readonly AgentOptions _options;
    private readonly LlmOptions _llmOptions;
    private readonly SecretProtector _protector;
    private readonly LeadEvaluator _evaluator;
    private readonly PacketBuilder _packets;
    private readonly PacketReadyNotifier _notifier;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AgentWorker> _log;

    public AgentWorker(
        string connectionString, AgentOptions options, LlmOptions llmOptions,
        SecretProtector protector, LeadEvaluator evaluator, PacketBuilder packets,
        PacketReadyNotifier notifier, ILoggerFactory loggers)
    {
        var csb = new NpgsqlConnectionStringBuilder(connectionString)
        {
            MaxPoolSize = Math.Max(1, options.MaxPoolSize),
            ApplicationName = "applytrack-agent",
        };
        _db = NpgsqlDataSource.Create(csb.ConnectionString);
        _options = options;
        _llmOptions = llmOptions;
        _protector = protector;
        _evaluator = evaluator;
        _packets = packets;
        _notifier = notifier;
        _loggers = loggers;
        _log = loggers.CreateLogger<AgentWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.IntervalSeconds));
        _log.LogInformation("agent worker started; pass every {Interval}", interval);
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A pass must never kill the worker; the next tick tries again.
                _log.LogError(ex, "agent pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One pass over every enabled tenant. Public so tests can drive it directly.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        long[] tenants;
        await using (var conn = await _db.OpenConnectionAsync(ct))
        {
            // The one privileged, cross-tenant query — the same fan-out shape as the
            // poller's _active_tenant_ids. Everything below runs through tenant-scoped repos.
            tenants = (await conn.QueryAsync<long>(
                "SELECT tenant_id FROM agent_settings WHERE enabled ORDER BY tenant_id")).ToArray();
        }

        var judged = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            judged += await RunTenantAsync(tenantId, ct);
        }
        return judged;
    }

    private async Task<int> RunTenantAsync(long tenantId, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var locked = await conn.ExecuteScalarAsync<bool>(
            "SELECT pg_try_advisory_lock(hashtext(@key))", new { key = LockPrefix + tenantId });
        if (!locked)
        {
            _log.LogInformation("tenant {TenantId}: another agent pass holds the lock; skipping", tenantId);
            return 0;
        }

        try
        {
            var settings = await new AgentSettingsRepo(conn, tenantId).GetAsync();
            if (!settings.Enabled)
                return 0;
            var events = new AgentEventRepo(conn, tenantId);
            var apps = new ApplicationRepo(conn, tenantId);
            var llmSettings = new LlmSettingsRepo(conn, tenantId, _protector, _loggers.CreateLogger<LlmSettingsRepo>());
            var cfg = EffectiveLlmConfig.Resolve(_llmOptions, await llmSettings.GetOverrideAsync());
            if (!cfg.IsConfigured)
            {
                _log.LogWarning("tenant {TenantId}: agent enabled but no LLM endpoint configured", tenantId);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = "no LLM endpoint configured (Settings · AI or the instance default)" });
                return 0;
            }

            var resume = await new ResumeRepo(conn, tenantId).GetAsync();
            var criteria = await new CriteriaRepo(conn, tenantId).GetAsync();
            var (_, _, _, lettersEnabled) = await llmSettings.GetViewAsync();
            var email = (await new UserRepo(conn).GetAsync(tenantId))?.Email ?? "";
            var inputs = new PacketInputs(
                resume, settings, email, await llmSettings.GetCoverLetterSignatureAsync(), lettersEnabled, cfg);
            var scope = new PacketScope(apps, new CoverLetterRepo(conn, tenantId),
                new AgentPacketRepo(conn, tenantId), events);
            var notifications = new NotificationSettingsRepo(
                conn, tenantId, _protector, _loggers.CreateLogger<NotificationSettingsRepo>());

            var today = await events.VerdictsSinceAsync(DailyWindow);
            var budget = Math.Min(settings.MaxPerRun, settings.MaxPerDay - today);
            if (budget <= 0)
            {
                _log.LogInformation("tenant {TenantId}: daily verdict cap reached", tenantId);
                return 0;
            }

            var judged = 0;
            foreach (var rec in await apps.ListAgentCandidatesAsync(settings.MinFitScore, budget))
            {
                ct.ThrowIfCancellationRequested();
                var verdict = await _evaluator.EvaluateAsync(rec, resume, criteria, settings, cfg, events, ct);
                judged++;
                if (verdict is { IsProceed: true })
                {
                    // Step 3: prepare the packet, park it in `ready`, and moo. A failed
                    // build is recorded and must not stop the pass.
                    try
                    {
                        await _packets.BuildAsync(rec, verdict, inputs, scope, ct);
                        await _notifier.NotifyAsync(notifications, scope.Packets, events,
                            rec.Name, rec.Fields.Company, rec.Fields.Role, ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogWarning(ex, "{Name}: packet build failed", rec.Name);
                        await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                            new { reason = "packet build failed: " + ex.Message, rec.Fields.Company, rec.Fields.Role });
                    }
                }
            }
            return judged;
        }
        finally
        {
            try
            {
                await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@key))",
                    new { key = LockPrefix + tenantId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The lock dies with the connection anyway.
                _log.LogDebug(ex, "tenant {TenantId}: advisory unlock failed", tenantId);
            }
        }
    }

    public override void Dispose()
    {
        _db.Dispose();
        base.Dispose();
    }
}
