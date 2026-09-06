// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using ApplyTrack.Api.Agent.Browser;
using ApplyTrack.Api.Crypto;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Notifications;
using Dapper;
using Microsoft.Playwright;
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
    private readonly BrowserOptions _browser;
    private readonly BrowserSubmitter _submitter;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AgentWorker> _log;

    public AgentWorker(
        string connectionString, AgentOptions options, LlmOptions llmOptions,
        SecretProtector protector, LeadEvaluator evaluator, PacketBuilder packets,
        PacketReadyNotifier notifier, BrowserOptions browser, BrowserSubmitter submitter,
        ILoggerFactory loggers)
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
        _browser = browser;
        _submitter = submitter;
        _loggers = loggers;
        _log = loggers.CreateLogger<AgentWorker>();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(15, _options.IntervalSeconds));
        _log.LogInformation("agent worker started; pass every {Interval}, submit queue every {Submit}s",
            interval, _options.SubmitPollSeconds);
        // Two cadences: the slow judging pass, and a fast lane draining the human's
        // Submit clicks so a click never waits for the next pass.
        await Task.WhenAll(PassLoopAsync(interval, stoppingToken), SubmitLoopAsync(stoppingToken));
    }

    private async Task PassLoopAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
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

    private async Task SubmitLoopAsync(CancellationToken stoppingToken)
    {
        if (!_browser.IsConfigured)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.SubmitPollSeconds)));
        do
        {
            try
            {
                await DrainSubmitsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "submit drain failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Drain the submit queue: every claimed request gets one browser run. Public for tests.</summary>
    public async Task<int> DrainSubmitsAsync(CancellationToken ct)
    {
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            SubmitRequest? req;
            await using (var conn = await _db.OpenConnectionAsync(ct))
                req = await SubmitQueue.ClaimNextAsync(conn);
            if (req is null)
                break;
            try
            {
                await RunSubmitAsync(req, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogError(ex, "{Name}: submit run failed", req.ApplicationName);
            }
            finally
            {
                await using var conn = await _db.OpenConnectionAsync(CancellationToken.None);
                await SubmitQueue.CompleteAsync(conn, req.Id);
            }
            done++;
        }
        return done;
    }

    private async Task RunSubmitAsync(SubmitRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var t = req.TenantId;
        var apps = new ApplicationRepo(conn, t);
        var packets = new AgentPacketRepo(conn, t);
        var events = new AgentEventRepo(conn, t);
        var evidence = new AgentEvidenceRepo(conn, t);
        var rec = await apps.GetAsync(req.ApplicationName);
        var packet = await packets.GetAsync(req.ApplicationName);
        if (rec is null || packet is null)
        {
            _log.LogInformation("{Name}: submit request for a missing application/packet; dropped", req.ApplicationName);
            return;
        }
        // The tenant's dry-run switch wins over the request: the agent never submits
        // for an account that has not turned dry-run off.
        var settings = await new AgentSettingsRepo(conn, t).GetAsync();
        var dryRun = req.DryRun || settings.DryRun;
        if (!dryRun && packet.NeedsReview.Count > 0)
            dryRun = true;

        var pdf = await new ResumeRepo(conn, t).GetPdfAsync();
        SubmitOutcome outcome;
        try
        {
            outcome = await _submitter.RunAsync(rec.Fields.Link, packet, pdf, dryRun, ct);
        }
        catch (Exception ex) when (ex is AppValidationException or PlaywrightException or TimeoutException)
        {
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, rec.Fields.Link, "",
                new { reason = ex.Message, dry_run = dryRun }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "browser: " + ex.Message, rec.Fields.Company, rec.Fields.Role });
            return;
        }

        var kind = outcome.Submitted ? AgentEvidenceRepo.Kinds.Submitted
            : outcome.Filled && dryRun && outcome.Error.Length == 0 ? AgentEvidenceRepo.Kinds.DryRun
            : AgentEvidenceRepo.Kinds.Failed;
        await evidence.RecordAsync(rec.Name, kind, outcome.Url, outcome.Confirmation,
            new { dry_run = dryRun, outcome.Mapped, outcome.Unmapped, error = outcome.Error }, outcome.Screenshot);
        await events.RecordAsync(kind, rec.Name, new
        {
            dry_run = dryRun, mapped = outcome.Mapped.Count, unmapped = outcome.Unmapped,
            error = outcome.Error, rec.Fields.Company, rec.Fields.Role,
        });

        var notifications = new NotificationSettingsRepo(conn, t, _protector, _loggers.CreateLogger<NotificationSettingsRepo>());
        if (outcome.Submitted)
        {
            // The human's job is done: mark it applied the way the Mark-applied button does.
            var today = MarkdownCodec.Today();
            await apps.UpdateStructuredAsync(rec.Name, rec.Fields with
            {
                Status = "applied", Applied = today,
                Followup = DateOnly.Parse(today).AddDays(7).ToString("yyyy-MM-dd"),
            }, null);
            await _notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, ct,
                PacketReadyNotifier.Moment.Submitted);
        }
        else if (kind == AgentEvidenceRepo.Kinds.DryRun)
        {
            await _notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, ct,
                PacketReadyNotifier.Moment.Filled);
        }
        _log.LogInformation("{Name}: browser {Kind} ({Mapped} mapped, {Unmapped} unmapped){Error}",
            rec.Name, kind, outcome.Mapped.Count, outcome.Unmapped.Count,
            outcome.Error.Length > 0 ? ": " + outcome.Error : "");
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
                        // With a browser, the moo waits for the dry-run fill (the submit
                        // lane sends it after the screenshot); without one, this is it.
                        if (_browser.IsConfigured && rec.Fields.Link.Length > 0)
                            await new SubmitRequestRepo(conn, tenantId).EnqueueAsync(rec.Name, dryRun: true);
                        else
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
