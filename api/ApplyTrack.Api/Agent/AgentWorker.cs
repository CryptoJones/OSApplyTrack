// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Text.Json;
using ApplyTrack.Api.Agent.Browser;
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
    /// <summary>Real LinkedIn Easy Apply submissions per tenant per day (#278). It is a person's own
    /// account, and the same one the poller discovers with: well under anything LinkedIn would notice.</summary>
    public const int LinkedInEasyDailyCap = 10;
    // When each Workday row was last asked whether it still exists (#277): twice a day is plenty.
    private readonly Dictionary<string, DateTime> _workdayChecked = [];
    private static readonly TimeSpan WorkdayRecheck = TimeSpan.FromHours(12);
    // Requests whose run met new questions and answered them: worth one more dry run (#278).
    private readonly HashSet<long> _dryAgain = [];
    /// <summary>Runs whose new questions came from behind an email sign-in: they go round again as
    /// real runs, since a dry run stops at the sign-in and would never see them (#280).</summary>
    private readonly HashSet<long> _realAgain = [];

    private readonly NpgsqlDataSource _db;
    private readonly AgentOptions _options;
    private readonly LlmOptions _llmOptions;
    private readonly SecretProtector _protector;
    private readonly LeadEvaluator _evaluator;
    private readonly PacketBuilder _packets;
    private readonly PacketReadyNotifier _notifier;
    private readonly BrowserOptions _browser;
    private readonly IBrowserSubmitter _submitter;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<AgentWorker> _log;
    private readonly ISecurityCodeSource? _codes;
    private readonly INotifier? _telegram;
    private readonly IPortalSessionRenewer? _portal;
    private readonly ILinkedInSessionRenewer? _linkedin;
    private readonly IHandshakeSessionRenewer? _handshake;
    // When each tenant's LinkedIn renewal was last tried (#233): one that stopped on the
    // app tap or a PIN waits before the next.
    private readonly Dictionary<long, DateTime> _linkedinAttempts = [];
    // When each tenant's Handshake renewal was last tried (#267): the school's sign-in
    // refuses a bad password hard, so the next try waits.
    private readonly Dictionary<long, DateTime> _handshakeAttempts = [];
    // When each tenant's MyGreenhouse renewal was last tried: a failed try emailed a code,
    // and the next one waits (#221).
    private readonly Dictionary<long, DateTime> _portalAttempts = [];

    /// <summary>A MyGreenhouse session is renewed this long before it runs out.</summary>
    public static readonly TimeSpan PortalRenewAhead = TimeSpan.FromDays(2);
    /// <summary>After a renewal attempt, the next for the same tenant waits this long.</summary>
    public static readonly TimeSpan PortalRetry = TimeSpan.FromHours(6);
    // The three renewals run on the pass's clock and, when someone pressed Sign in now, on
    // the submit lane's; never both at once — two sign-ins to one account is what LinkedIn
    // looks for.
    private readonly SemaphoreSlim _renewing = new(1, 1);

    /// <summary>
    /// Is this tenant's session on <paramref name="host"/> to be renewed on this call? Yes when
    /// the person pressed Sign in now (the request is taken and cleared here, so a failed try
    /// does not go round again unasked); otherwise not within <see cref="PortalRetry"/> of the
    /// last try — a try that stopped on the LinkedIn app's tap or a mailed PIN waits, so the
    /// moo is not sent every pass. Either way the try is stamped.
    /// </summary>
    /// <param name="onlyRequested">The submit lane's mode: only a Sign in now is taken, never a
    /// renewal the pass would make on its own clock — so the request is not queued behind a
    /// MyGreenhouse sign-in waiting minutes on its emailed code.</param>
    private static async Task<bool> DueAsync(BoardAccountRepo accounts, string host, Dictionary<long, DateTime> attempts, long tenantId, bool onlyRequested)
    {
        var asked = await accounts.TakeRenewalRequestAsync(host);
        if (onlyRequested && !asked)
            return false;
        if (!asked && attempts.TryGetValue(tenantId, out var last) && DateTime.UtcNow - last < PortalRetry)
            return false;
        attempts[tenantId] = DateTime.UtcNow;
        return true;
    }

    /// <summary>
    /// One renewal of this tenant's session on <paramref name="host"/> across every worker: the
    /// attempt runs under a Postgres advisory lock on <paramref name="conn"/>, which is the
    /// tenant's own for the one iteration and released with it (the pool's reset on close
    /// drops advisory locks; so does Postgres if the worker dies). Two workers signing in to
    /// one account at once is what LinkedIn looks for, and the in-process clock
    /// (<see cref="DueAsync"/>) cannot see another container's. False when another holds it.
    /// </summary>
    private static async Task<bool> TryLockRenewalAsync(NpgsqlConnection conn, long tenantId, string host) =>
        await conn.ExecuteScalarAsync<bool>("SELECT pg_try_advisory_lock(hashtext(@key))",
            new { key = $"applytrack:renew:{host}:{tenantId}" });

    /// <summary>The three signed-in sources' renewals, one lane at a time. A lane that finds
    /// the other mid-renewal skips: the pass's next tick, or the submit lane's, comes soon.</summary>
    public async Task RenewSessionsAsync(CancellationToken ct, bool onlyRequested = false)
    {
        if (!await _renewing.WaitAsync(0, ct))
            return;
        try
        {
            await RenewLinkedInSessionsAsync(ct, onlyRequested);
            await RenewHandshakeSessionsAsync(ct, onlyRequested);
            await RenewPortalSessionsAsync(ct, onlyRequested);
        }
        finally { _renewing.Release(); }
    }

    private async Task<bool> AnyRenewalRequestedAsync(CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(BoardAccountRepo.AnyRenewalRequestedSql);
    }
    // This container's name: the quadlet/compose service name, or the pod's hostname.
    private readonly string _workerId = Environment.MachineName;

    public AgentWorker(
        string connectionString, AgentOptions options, LlmOptions llmOptions,
        SecretProtector protector, LeadEvaluator evaluator, PacketBuilder packets,
        PacketReadyNotifier notifier, BrowserOptions browser, IBrowserSubmitter submitter,
        ILoggerFactory loggers, ISecurityCodeSource? codes = null, INotifier? telegram = null,
        IPortalSessionRenewer? portal = null, ILinkedInSessionRenewer? linkedin = null,
        IHandshakeSessionRenewer? handshake = null)
    {
        _codes = codes;
        _telegram = telegram;
        _portal = portal ?? (browser.IsConfigured ? new PortalSessionRenewer(browser, loggers.CreateLogger<PortalSessionRenewer>()) : null);
        _linkedin = linkedin ?? (browser.IsConfigured ? new LinkedInSessionRenewer(browser, loggers.CreateLogger<LinkedInSessionRenewer>()) : null);
        _handshake = handshake ?? (browser.IsConfigured ? new HandshakeSessionRenewer(browser, loggers.CreateLogger<HandshakeSessionRenewer>()) : null);
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
        _log.LogInformation("agent worker started; pass every {Interval}, submit queue every {Submit}s, heartbeat every {Beat}s",
            interval, _options.SubmitPollSeconds, _options.HeartbeatSeconds);
        await HeartbeatAsync(stoppingToken);
        // Three cadences: the slow judging pass, a fast lane draining the human's Submit
        // clicks so a click never waits for the next pass, and the heartbeat on a timer of
        // its own — tied to neither lane, so a worker without a browser (no submit lane)
        // is not read as absent for two minutes out of every five (#184).
        await Task.WhenAll(
            PassLoopAsync(interval, stoppingToken),
            SubmitLoopAsync(stoppingToken),
            HeartbeatLoopAsync(stoppingToken));
    }

    private async Task PassLoopAsync(TimeSpan interval, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval);
        do
        {
            try
            {
                await RenewSessionsAsync(stoppingToken);
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
                // A Sign in now is taken on this lane's clock — seconds, not the pass's minutes —
                // because the person is standing by with their phone for the tap.
                if (await AnyRenewalRequestedAsync(stoppingToken))
                    await RenewSessionsAsync(stoppingToken, onlyRequested: true);
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

    private async Task HeartbeatLoopAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Max(5, _options.HeartbeatSeconds)));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await HeartbeatAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutting down
        }
    }

    /// <summary>
    /// Stamp this worker's <c>agent_workers</c> row: it is here, and whether it drives a
    /// browser. The api process reads it to decide whether a Submit click has anything
    /// to land on — on every shipped deployment shape the browser is wired to the agent
    /// container only, so the api cannot tell from its own configuration (#159).
    /// Best-effort: a heartbeat that fails must never stop a pass.
    /// </summary>
    public async Task HeartbeatAsync(CancellationToken ct)
    {
        try
        {
            await using var conn = await _db.OpenConnectionAsync(ct);
            await AgentWorkerRegistry.HeartbeatAsync(conn, _workerId, _browser.IsConfigured);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "heartbeat failed");
        }
    }

    /// <summary>Drain the submit queue: every claimed request gets one browser run. Public for tests.</summary>
    public async Task<int> DrainSubmitsAsync(CancellationToken ct)
    {
        await HeartbeatAsync(ct);
        var done = 0;
        while (!ct.IsCancellationRequested)
        {
            SubmitRequest? req;
            await using (var conn = await _db.OpenConnectionAsync(ct))
                req = await SubmitQueue.ClaimNextAsync(conn);
            if (req is null)
                break;
            var promote = false;
            try
            {
                promote = await RunSubmitAsync(req, ct);
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
            // A run that met new questions and got them answered goes round again as a dry run —
            // after the completion, for the same reason the promotion below waits for it (#278).
            if (_dryAgain.Remove(req.Id))
            {
                await using var conn = await _db.OpenConnectionAsync(CancellationToken.None);
                if (await new SubmitRequestRepo(conn, req.TenantId).EnqueueAsync(req.ApplicationName, dryRun: true))
                    _log.LogInformation("{Name}: new questions answered, queued for another dry run", req.ApplicationName);
            }
            else if (_realAgain.Remove(req.Id))
            {
                await using var conn = await _db.OpenConnectionAsync(CancellationToken.None);
                if (await new SubmitRequestRepo(conn, req.TenantId).EnqueueAsync(req.ApplicationName, dryRun: false))
                    _log.LogInformation("{Name}: the form behind the sign-in is answered, queued to sign in and submit", req.ApplicationName);
            }
            // Promote AFTER the completion above, never from inside RunSubmitAsync. The queue
            // is keyed on (tenant, application) and CompleteAsync stamps done_at on that row,
            // so an enqueue from inside the run would be marked done without ever running.
            if (promote)
            {
                await using var conn = await _db.OpenConnectionAsync(CancellationToken.None);
                if (await new SubmitRequestRepo(conn, req.TenantId).EnqueueAsync(req.ApplicationName, dryRun: false))
                    _log.LogInformation("{Name}: clean dry run, queued for real submission", req.ApplicationName);
            }
            done++;
        }
        return done;
    }

    /// <summary>Everything a packet build for one tenant draws on, loaded once per tenant or per prepare.</summary>
    private sealed record TenantWork(
        AgentSettings Settings, EffectiveLlmConfig Cfg, Resume Resume, string Email,
        PacketInputs Inputs, PacketScope Scope, NotificationSettingsRepo Notifications);

    private async Task<TenantWork> LoadTenantWorkAsync(NpgsqlConnection conn, long tenantId, AgentSettings settings)
    {
        var apps = new ApplicationRepo(conn, tenantId);
        var events = new AgentEventRepo(conn, tenantId);
        var llmSettings = new LlmSettingsRepo(conn, tenantId, _protector, _loggers.CreateLogger<LlmSettingsRepo>());
        var cfg = EffectiveLlmConfig.Resolve(_llmOptions, await llmSettings.GetOverrideAsync());
        var resume = await new ResumeRepo(conn, tenantId, _protector).GetAsync();
        var (_, _, _, lettersEnabled) = await llmSettings.GetViewAsync();
        var email = (await new UserRepo(conn).GetAsync(tenantId))?.Email ?? "";
        var accounts = await new BoardAccountRepo(conn, tenantId, _protector, _log).TargetsAsync();
        var inputs = new PacketInputs(
            resume, settings, email, await llmSettings.GetCoverLetterSignatureAsync(), lettersEnabled, cfg, accounts);
        var scope = new PacketScope(apps, new CoverLetterRepo(conn, tenantId, _protector),
            new AgentPacketRepo(conn, tenantId, _protector), events, new AnswerBankRepo(conn, tenantId, _protector));
        var notifications = new NotificationSettingsRepo(
            conn, tenantId, _protector, _loggers.CreateLogger<NotificationSettingsRepo>());
        return new TenantWork(settings, cfg, resume, email, inputs, scope, notifications);
    }

    /// <summary>A verdict as the audit row recorded it, or null when there is none.</summary>
    private static Verdict? RecordedVerdict(AgentEvent? recorded)
    {
        if (recorded is null || !recorded.Detail.TryGetProperty("decision", out var d))
            return null;
        return new Verdict(
            d.GetString() ?? Verdict.Skip,
            recorded.Detail.TryGetProperty("confidence", out var c) && c.TryGetInt32(out var ci) ? ci : 0,
            recorded.Detail.TryGetProperty("rationale", out var r) ? r.GetString() ?? "" : "",
            [], [], recorded.Detail.TryGetProperty("model_consulted", out var m) && m.ValueKind == JsonValueKind.True);
    }

    /// <summary>
    /// The human's Prepare, drained where the browser is (#183): judge (reusing the
    /// recorded verdict — the person asked for this packet, so a recorded skip does not
    /// stop it), build with form discovery, park in Ready. Returns the fresh record, or
    /// null after recording why it could not be built.
    /// </summary>
    private async Task<(AppRecord Rec, AgentPacket Packet)?> PrepareAsync(
        NpgsqlConnection conn, long tenantId, AppRecord rec, AgentSettings settings, CancellationToken ct)
    {
        var events = new AgentEventRepo(conn, tenantId);
        var work = await LoadTenantWorkAsync(conn, tenantId, settings);
        if (!work.Cfg.IsConfigured)
        {
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "prepare failed: no LLM endpoint configured (Settings · AI or the instance default)", rec.Fields.Company, rec.Fields.Role });
            return null;
        }
        var criteria = await new CriteriaRepo(conn, tenantId).GetAsync();
        var verdict = RecordedVerdict(await events.LatestVerdictAsync(rec.Name))
            ?? await _evaluator.EvaluateAsync(rec, work.Resume, criteria, settings, work.Cfg, events, ct);
        if (verdict is null)
        {
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "prepare failed: the model did not reach a verdict", rec.Fields.Company, rec.Fields.Role });
            return null;
        }
        try
        {
            var packet = await _packets.BuildAsync(rec, verdict, work.Inputs, work.Scope, ct);
            var fresh = await work.Scope.Apps.GetAsync(rec.Name) ?? rec;
            return (fresh, packet);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "{Name}: packet build failed", rec.Name);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "packet build failed: " + ex.Message, rec.Fields.Company, rec.Fields.Role });
            return null;
        }
    }

    /// <summary>
    /// Run one queued submission. Returns true when this was a DRY run that came back
    /// completely clean AND the tenant has turned dry-run off, meaning the caller should
    /// re-queue it for real. A real run always returns false, which is what terminates the
    /// promotion chain after exactly one hop.
    /// </summary>
    private async Task<bool> RunSubmitAsync(SubmitRequest req, CancellationToken ct)
    {
        await using var conn = await _db.OpenConnectionAsync(ct);
        var t = req.TenantId;
        var apps = new ApplicationRepo(conn, t);
        var packets = new AgentPacketRepo(conn, t, _protector);
        var events = new AgentEventRepo(conn, t);
        var evidence = new AgentEvidenceRepo(conn, t, _protector);
        var rec = await apps.GetAsync(req.ApplicationName);
        if (rec is null)
        {
            // Record it. This used to return silently, which made a dropped submission
            // indistinguishable from one that never got requested: the queue row still
            // reads claimed+done, and nothing lands in agent_evidence, so the run leaves
            // no trace anywhere the user can see. That matters most for the very case
            // this branch catches — a submission that never happened, and therefore an
            // "applied" flag that was never set.
            await events.RecordAsync(AgentEventRepo.Kinds.Error, req.ApplicationName,
                new { reason = "submit dropped: application missing" });
            _log.LogInformation("{Name}: submit request dropped (application missing)", req.ApplicationName);
            return false;
        }
        var settings = await new AgentSettingsRepo(conn, t).GetAsync();
        var notifications = new NotificationSettingsRepo(conn, t, _protector, _loggers.CreateLogger<NotificationSettingsRepo>());

        AgentPacket? packet;
        if (req.Prepare)
        {
            // Rebuild first, here, where discovery can see the real form. A packet whose
            // ATS the browser cannot drive is still prepared — and then it is the human's,
            // by copy-and-open, exactly as the pass would have left it.
            var prepared = await PrepareAsync(conn, t, rec, settings, ct);
            if (prepared is null)
                return false;
            (rec, packet) = prepared.Value;
            if (rec.Fields.Link.Length == 0 || !AtsProvider.BrowserCanSubmit(packet.Provider, settings.LongTail, settings.LinkedInEasy))
            {
                await _notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, ct);
                return false;
            }
        }
        else
        {
            packet = await packets.GetAsync(req.ApplicationName);
        }
        if (packet is null)
        {
            await events.RecordAsync(AgentEventRepo.Kinds.Error, req.ApplicationName,
                new { reason = "submit dropped: packet missing" });
            _log.LogInformation("{Name}: submit request dropped (packet missing)", req.ApplicationName);
            return false;
        }
        // The provider is a fact of the link, not of the packet: one built before its board
        // was taught (#180) still says "unknown", and would be driven at the posting page
        // instead of its form. Read it from the link every run, and keep the packet honest (#203).
        var detected = AtsProvider.Detect(rec.Fields.Link, rec.Fields.Source);
        if (packet.Provider != detected)
        {
            packet.Provider = detected;
            await packets.UpdateProviderAsync(rec.Name, detected);
        }
        // The tenant's dry-run switch wins over the request: the agent never submits
        // for an account that has not turned dry-run off.
        var dryRun = req.DryRun || settings.DryRun;
        if (!dryRun && packet.BlockingReview().Any())
            dryRun = true;
        // The candidate's own sign-ins on account-only ATSs (SAP SuccessFactors, #216), and the
        // kept LinkedIn session the browser starts Easy Apply from (#278).
        var boardAccounts = await new BoardAccountRepo(conn, t, _protector, _log).TargetsAsync();
        // No kept LinkedIn session, no Easy Apply run: signed out, LinkedIn serves the posting
        // with "Sign in" and "Join now" and nothing can be attempted — twenty-five packets each
        // spent a browser trip and a retry finding that out between 2026-09-21 and 09-22, while
        // the renewal waited on a tap in the LinkedIn app that came at no hour anyone chose. The
        // run stands down here, says what it waits on, and is queued again by the renewal that
        // keeps a session — not by the reconciler's clock, and not against its failure count.
        if (detected == AtsProvider.LinkedInEasy && BoardAccount.For(boardAccounts, "www.linkedin.com") is not { Session.Length: > 0 })
        {
            const string waits = "LinkedIn Easy Apply needs the agent signed in as you, and no session is kept — the sign-in waits on a tap in the LinkedIn app: "
                + "tap Yes when the 🔐 moo arrives, or press Sign in now under Settings · Agent · Board accounts when you have your phone. "
                + "Nothing was attempted; this run goes again by itself once the session is kept";
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, rec.Fields.Link, "",
                new { reason = waits, dry_run = dryRun, transient = false, awaiting_session = "linkedin.com", mapped = Array.Empty<string>(), unmapped = Array.Empty<string>() }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "browser: " + waits, rec.Fields.Company, rec.Fields.Role });
            _log.LogInformation("{Name}: stood down — no LinkedIn session to run Easy Apply with", rec.Name);
            return false;
        }
        // One LinkedIn run per tenant at a time, across every worker (#278): two browsers signed
        // in to one personal account at once is exactly what LinkedIn looks for, and it is also
        // what makes the daily cap below a real one rather than a race between two readers of the
        // same count. Held on this connection for the run; released after it, and by Postgres
        // itself if the worker dies.
        var linkedInLock = detected == AtsProvider.LinkedInEasy ? $"applytrack:linkedin-easy:{t}" : null;
        if (linkedInLock is not null)
            await conn.ExecuteAsync("SELECT pg_advisory_lock(hashtext(@key))", new { key = linkedInLock });
        async Task ReleaseLinkedInAsync()
        {
            if (linkedInLock is null) return;
            try { await conn.ExecuteAsync("SELECT pg_advisory_unlock(hashtext(@key))", new { key = linkedInLock }); }
            catch (Exception ex) when (ex is not OperationCanceledException) { _log.LogDebug(ex, "linkedin lock release failed"); }
        }
        // Over the daily cap, the run stands down — it does NOT quietly become a dry run (#302).
        // A downgrade there produced a clean dry run, which promoted itself to a real run, which
        // was downgraded again: an unbounded loop that opened LinkedIn's dialog every half minute
        // and never sent anything. Nothing is attempted, nothing is promoted, and the reconciler
        // queues it again on a later pass — tomorrow, when the window has moved.
        if (!dryRun && detected == AtsProvider.LinkedInEasy
            && await evidence.SubmittedSinceAsync("linkedin.com", DailyWindow) >= LinkedInEasyDailyCap)
        {
            const string capped = "LinkedIn Easy Apply: {0} already sent in the last day — nothing was attempted; this one goes out once the day's window has moved";
            var reason = string.Format(capped, LinkedInEasyDailyCap);
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, rec.Fields.Link, "",
                new { reason, dry_run = false, transient = false, mapped = Array.Empty<string>(), unmapped = Array.Empty<string>() }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason, rec.Fields.Company, rec.Fields.Role });
            _log.LogInformation("{Name}: stood down — LinkedIn Easy Apply daily cap reached", rec.Name);
            await ReleaseLinkedInAsync();
            return false;
        }

        var resumes = new ResumeRepo(conn, t, _protector);
        var resume = await resumes.GetAsync();
        var pdf = await resumes.GetPdfAsync();
        // The same résumé as text, for a board whose uploader will not take the file.
        var resumeText = resume.Summary;
        // The drafted letter, for a form whose cover letter is a required file field.
        var coverLetter = await new CoverLetterRepo(conn, t, _protector).GetBodyAsync(rec.Name) ?? "";
        // The standard fields — name, email, phone, links — come from the profile as it is
        // NOW, not as it was when the packet was built. When the account email changed, all
        // 35 built packets had to be patched by hand (#189).
        var email = (await new UserRepo(conn).GetAsync(t))?.Email ?? "";
        // ...except where the person has pinned one in the answer bank (#200).
        var pinned = await new AnswerBankRepo(conn, t, _protector).PinnedAsync();
        if (PacketBuilder.RefreshStandardAnswers(packet, new AnswerContext(resume, settings, email, coverLetter, packet.PostingExcerpt), pinned))
        {
            packet = await packets.UpdateAnswersAsync(rec.Name, packet.Answers, null);
            _log.LogInformation("{Name}: standard answers refreshed from the profile", rec.Name);
        }
        // Phase two of a real click: the board emailed the candidate a security code. Record
        // it, and wait — up to eight minutes, holding the browser session the code is good
        // for — reading the candidate's own mailbox for the mail when one is configured, and
        // polling the queue row a human's paste lands on either way. The moo goes out only
        // when there is no mailbox to read: with one, the human never needs to know. Once
        // it has gone out, a reply to it with the code counts the same as the paste — the
        // phone-in-hand path for anyone whose mail the app cannot read (#168).
        // The same park serves a board that signs the candidate in by email before it shows
        // its form (join.com, #210): there the reply is the emailed code or the emailed link,
        // the moo says so, and the mailbox is read for the board's mail rather than the
        // company's.
        async Task<string?> AwaitSecurityCodeAsync(CodeRequest ask, CancellationToken token)
        {
            var recipient = ask.Recipient;
            var boardHost = ask.SignIn && Uri.TryCreate(rec.Fields.Link, UriKind.Absolute, out var posting) ? posting.Host : "";
            var since = DateTimeOffset.UtcNow.AddMinutes(-1);
            var mailbox = _codes is null ? null
                : await new MailboxSettingsRepo(conn, t, _protector, _loggers.CreateLogger<MailboxSettingsRepo>()).GetTargetAsync();
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.AwaitingCode, rec.Fields.Link, "",
                new { recipient, dry_run = false, mailbox = mailbox is not null, sign_in = ask.SignIn }, null);
            await events.RecordAsync(AgentEvidenceRepo.Kinds.AwaitingCode, rec.Name,
                new { recipient, mailbox = mailbox is not null, sign_in = ask.SignIn, rec.Fields.Company, rec.Fields.Role });
            TelegramCodeReplies? replies = null;
            async Task MooAndListenAsync()
            {
                // Replies dated before the moo (a 15 s grace for clock skew) are never a code.
                var mooed = DateTimeOffset.UtcNow.AddSeconds(-15);
                await _notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, token,
                    ask.SignIn ? PacketReadyNotifier.Moment.SignIn : PacketReadyNotifier.Moment.Code, recipient);
                var target = _telegram is null ? null : await notifications.GetTargetAsync();
                replies = target is null ? null : new TelegramCodeReplies(_telegram!, target, mooed, _log);
            }
            if (mailbox is null)
                await MooAndListenAsync();
            _log.LogInformation("{Name}: parked — the board emailed a {What} to {Recipient}{How}", rec.Name,
                ask.SignIn ? "sign-in code or link" : "security code", recipient,
                mailbox is not null ? "; reading the mailbox for it" : replies is not null ? "; mooed, reading Telegram replies for it" : "; mooed");
            var wait = Math.Max(1, _options.SecurityCodeWaitSeconds);
            var deadline = DateTime.UtcNow.AddSeconds(wait);
            var poll = TimeSpan.FromSeconds(Math.Min(5, wait));
            var tick = 0;
            while (DateTime.UtcNow < deadline && !token.IsCancellationRequested)
            {
                await Task.Delay(poll, token);
                await using (var pollConn = await _db.OpenConnectionAsync(token))
                {
                    var pasted = await SubmitQueue.SecurityCodeAsync(pollConn, req.Id);
                    if (pasted.Length > 0) return pasted;
                }
                if (replies is not null)
                {
                    try
                    {
                        var code = await replies.PollAsync(token);
                        if (code is not null)
                        {
                            _log.LogInformation("{Name}: security code read from a Telegram reply", rec.Name);
                            return code;
                        }
                    }
                    catch (NotificationFailedException ex)
                    {
                        // A bot that cannot be read (a webhook set on it, Telegram down) must not
                        // end the run: the paste still works. Say so once and stop asking.
                        _log.LogWarning("{Name}: Telegram replies unreadable: {Reason}", rec.Name, ex.Message);
                        await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name, new { reason = "telegram: " + ex.Message });
                        replies = null;
                    }
                }
                if (mailbox is not null && tick++ % 3 == 0)
                {
                    try
                    {
                        var code = await _codes!.FindCodeAsync(mailbox, recipient, rec.Fields.Company, since, token, boardHost);
                        if (code is not null)
                        {
                            _log.LogInformation("{Name}: {What} read from the mailbox", rec.Name, ask.SignIn ? "sign-in code or link" : "security code");
                            return code;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // A mailbox that will not open must not end the run: the human can still paste.
                        _log.LogWarning("{Name}: mailbox read failed: {Reason}", rec.Name, ex.Message);
                        await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name, new { reason = "mailbox: " + ex.Message });
                        mailbox = null;
                        await MooAndListenAsync();
                    }
                }
            }
            return null;
        }

        SubmitOutcome outcome;
        try
        {
            outcome = await _submitter.RunAsync(rec.Fields.Link, packet, pdf, dryRun, ct, resumeText, coverLetter,
                dryRun ? null : AwaitSecurityCodeAsync, boardAccounts);
        }
        // Catch EVERYTHING except cancellation. This filter used to name three types --
        // AppValidationException, PlaywrightException, TimeoutException -- which quietly
        // assumed the browser only ever fails in ways Playwright itself models. It does
        // not. On a live instance all 13 submissions died on
        // System.ComponentModel.Win32Exception (13) -- errno EACCES out of fork/exec,
        // thrown before Playwright's own driver was even up -- matching none of the three.
        // Every one escaped to the drain loop's generic handler, which logs and marks the
        // queue row done WITHOUT writing evidence, so a submission that crashed looked
        // exactly like one that was never requested. A failure the user cannot see is the
        // worst outcome available here: it is indistinguishable from success until they
        // notice the application was never sent. The type name goes into the reason so an
        // infrastructure crash is still tellable from a validation refusal.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var reason = ex is AppValidationException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            // A crash is worth another try; a refusal the submitter reasoned its way to is
            // not. Said here, where the difference is known, so ReadyReconciler need not
            // guess it from the wording (#274).
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, rec.Fields.Link, "",
                new { reason, dry_run = dryRun, transient = ex is not AppValidationException }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "browser: " + reason, rec.Fields.Company, rec.Fields.Role });
            _log.LogWarning(ex, "{Name}: browser submission failed", rec.Name);
            await ReleaseLinkedInAsync();
            return false;
        }
        await ReleaseLinkedInAsync();

        if (outcome.Closed)
        {
            // The posting closed between discovery and now — the apply URL redirected to the
            // board. This is an expired lead, not a submission that failed: retire it the way the
            // human's Pass button does, record why, and do not leave a misleading dry-run behind.
            await apps.UpdateStructuredAsync(rec.Name, rec.Fields with { Status = "passed" }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "posting closed before submission — lead marked passed", rec.Fields.Company, rec.Fields.Role });
            _log.LogInformation("{Name}: posting closed, marked passed", rec.Name);
            return false;
        }

        if (outcome.Captcha)
        {
            // A captcha is not a defect to retry and not something to solve: the form is asking
            // for a person. Park the packet in the ready queue with the answers already drafted,
            // tell the human it is theirs, and stop spending runs on it.
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, outcome.Url, "",
                new { captcha = true, outcome.Mapped, outcome.Unmapped, error = outcome.Error }, outcome.Screenshot);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name, new
            {
                reason = "captcha on the form — prepared for Copy answers and open",
                captcha = true, rec.Fields.Company, rec.Fields.Role,
            });
            _log.LogInformation("{Name}: captcha on the form, left for the human", rec.Name);
            return false;
        }

        var kind = outcome.Submitted ? AgentEvidenceRepo.Kinds.Submitted
            : outcome.Filled && dryRun && outcome.Error.Length == 0 ? AgentEvidenceRepo.Kinds.DryRun
            : AgentEvidenceRepo.Kinds.Failed;
        // What still needs the person after this fill: the required questions nobody could
        // answer, and the required fields no answer could be typed into — by label, since
        // that is what the person will be looking for on the form (#190).
        var labels = packet.Questions.ToDictionary(q => q.Id, q => q.Label.Length > 0 ? q.Label : q.Id);
        var needsYou = packet.BlockingReview().Select(r => r.Id).Concat(outcome.Unmapped)
            .Distinct().Select(id => labels.GetValueOrDefault(id, id)).ToList();
        // Discovery -> now, the headline posting->applied latency. Recorded on the event so
        // the win is provable per-application (the agent_latency view aggregates the same
        // timestamps) and logged so it is visible live. A best-effort read: a null age never
        // blocks the submission.
        var latency = await DiscoveryAgeSecondsAsync(conn, t, rec.Name);
        await evidence.RecordAsync(rec.Name, kind, outcome.Url, outcome.Confirmation,
            new { dry_run = dryRun, outcome.Mapped, outcome.Unmapped, error = outcome.Error, needs_you = needsYou }, outcome.Screenshot);
        await events.RecordAsync(kind, rec.Name, new
        {
            dry_run = dryRun, mapped = outcome.Mapped.Count, unmapped = outcome.Unmapped,
            error = outcome.Error, latency_seconds = latency, rec.Fields.Company, rec.Fields.Role,
        });

        // Easy Apply's questions only exist once their step is reached (#278): the run hands back
        // what it met for the first time, the packet takes them on and answers them, and — when
        // nothing is left for the person — it goes round again. It cannot loop: a question is
        // only ever "discovered" once, and a run that discovers none asks for no re-run.
        if (outcome.Discovered is { Count: > 0 } met)
        {
            var work = await LoadTenantWorkAsync(conn, t, settings);
            if (work.Cfg.IsConfigured)
            {
                var (answered, extended) = await _packets.ExtendAsync(rec, packet, met, work.Inputs, work.Scope, ct);
                // Behind an email sign-in only a real run can reach the form, and only a run the
                // person asked for as real signs in at all — so it goes again as what it was. It
                // cannot loop: the next run knows every question this one met.
                if (answered > 0 && !extended.BlockingReview().Any())
                    (outcome.BehindSignIn && !req.DryRun ? _realAgain : _dryAgain).Add(req.Id);
            }
        }

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
                PacketReadyNotifier.Moment.Filled, string.Join("; ", needsYou));
        }
        _log.LogInformation("{Name}: browser {Kind} ({Mapped} mapped, {Unmapped} unmapped){Latency}{Error}",
            rec.Name, kind, outcome.Mapped.Count, outcome.Unmapped.Count,
            latency is { } secs ? $", {secs:0}s since discovery" : "",
            outcome.Error.Length > 0 ? ": " + outcome.Error : "");

        // Promote a clean dry run to a real submission. The dry run IS the safety check the
        // human click used to be: it proves the form was reachable, every required field was
        // mapped, and nothing errored. Requiring a person to look at the screenshot only
        // helps if they actually look — and an application that is never sent is not a safe
        // outcome, it is a guaranteed miss. Every condition below has to hold:
        //   - this run was a dry run that came back clean (kind == DryRun)
        //   - the run was REQUESTED as a dry run (req.DryRun). A run asked for as real that
        //     ran as a dry run anyway was downgraded somewhere above — by the daily cap, or
        //     by a blocking review item the fill then cleared. Promoting it re-queues the
        //     same real run, which is downgraded again: the unbounded loop of #302. A
        //     promotion that comes back as a dry run must not re-promote.
        //   - something was actually filled (Mapped.Count > 0). A run that mapped nothing
        //     has proved nothing about the form, whatever else it reports (#302).
        //   - the tenant has explicitly turned dry-run off (settings.DryRun == false)
        //   - no required field went unmapped
        //   - no REQUIRED question is still waiting on the user (an optional one the model
        //     declined is left blank on purpose and must not park the packet forever)
        // The caller re-queues with dryRun:false, and that real run cannot promote again
        // because its kind will never be DryRun. A packet filled while the switch was still
        // on is picked up later by ReadyPromoter — on the flip, or on the next pass (#185).
        return kind == AgentEvidenceRepo.Kinds.DryRun
            && req.DryRun
            && outcome.Mapped.Count > 0
            && !settings.DryRun
            && outcome.Unmapped.Count == 0
            && !packet.BlockingReview().Any();
    }

    /// <summary>Seconds between a lead's discovery (<c>applications.created_at</c>) and now,
    /// or null when the row is gone. Never throws: instrumentation must not fail a submission.</summary>
    private static async Task<double?> DiscoveryAgeSecondsAsync(NpgsqlConnection conn, long tenantId, string name)
    {
        try
        {
            return await conn.ExecuteScalarAsync<double?>(
                "SELECT EXTRACT(EPOCH FROM (now() - created_at))::double precision FROM applications "
                + "WHERE tenant_id = @t AND name = @n",
                new { t = tenantId, n = Slug.Normalize(name) });
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>One pass over every enabled tenant. Public so tests can drive it directly.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await HeartbeatAsync(ct);
        long[] tenants;
        await using (var conn = await _db.OpenConnectionAsync(ct))
        {
            // The one privileged, cross-tenant query — the same fan-out shape as the
            // poller's _active_tenant_ids. Everything below runs through tenant-scoped repos.
            // Enabled by the tenant AND allowed by the operator: an account outside the
            // allowlist is never judged, whatever it switched on.
            tenants = (await conn.QueryAsync<long>(
                "SELECT s.tenant_id FROM agent_settings s JOIN agent_allowlist a ON a.tenant_id = s.tenant_id "
                + "WHERE s.enabled ORDER BY s.tenant_id")).ToArray();
        }

        var judged = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            judged += await RunTenantAsync(tenantId, ct);
        }
        return judged;
    }

    /// <summary>
    /// Keep every tenant's MyGreenhouse session alive for the poller (#221): where the kept
    /// session is missing, cleared by a bounce, or due to run out, the browser signs in as
    /// the board account (the security code read from the tenant's mailbox) and the fresh
    /// cookie is sealed onto the account row. Needs a browser and a mailbox reader; a tenant
    /// is tried at most once per <see cref="PortalRetry"/>. Public for tests.
    /// </summary>
    public async Task<int> RenewPortalSessionsAsync(CancellationToken ct, bool onlyRequested = false)
    {
        if (_portal is null || _codes is null) return 0;
        long[] tenants;
        await using (var conn = await _db.OpenConnectionAsync(ct))
            tenants = (await conn.QueryAsync<long>(BoardAccountRepo.PortalRenewalDueSql,
                new { hosts = BoardAccountRepo.PortalHosts, ahead = PortalRenewAhead })).ToArray();
        var renewed = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            await using var conn = await _db.OpenConnectionAsync(ct);
            var accounts = new BoardAccountRepo(conn, tenantId, _protector, _loggers.CreateLogger<BoardAccountRepo>());
            var events = new AgentEventRepo(conn, tenantId);
            var portal = await accounts.PortalAsync();
            if (portal is null) continue;
            if (!await DueAsync(accounts, portal.Host, _portalAttempts, tenantId, onlyRequested) || !await TryLockRenewalAsync(conn, tenantId, portal.Host))
                continue;
            var mailbox = await new MailboxSettingsRepo(conn, tenantId, _protector, _loggers.CreateLogger<MailboxSettingsRepo>()).GetTargetAsync();
            if (mailbox is null)
            {
                _log.LogWarning("tenant {TenantId}: MyGreenhouse session for {Email} needs renewing and there is no mailbox to read the code from", tenantId, portal.Username);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = $"MyGreenhouse: the session for {portal.Username} needs renewing and there is no mailbox to read the security code from — save one under Settings · Notifications" });
                continue;
            }
            try
            {
                var fresh = await _portal.RenewAsync(portal.Username,
                    (since, token) => _codes.FindCodeAsync(mailbox, portal.Username, "Greenhouse", since, token, "my.greenhouse.io"), ct);
                await accounts.SavePortalSessionAsync(portal.Host, fresh.Cookie, fresh.ExpiresAt);
                await events.RecordAsync(AgentEventRepo.Kinds.PortalSession, "",
                    new { host = portal.Host, username = portal.Username, expires_at = fresh.ExpiresAt });
                _log.LogInformation("tenant {TenantId}: MyGreenhouse session renewed for {Email} until {Until:u}", tenantId, portal.Username, fresh.ExpiresAt);
                renewed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("tenant {TenantId}: MyGreenhouse session renewal failed: {Reason}", tenantId, ex.Message);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = "MyGreenhouse session renewal failed: " + ex.Message });
            }
        }
        return renewed;
    }

    /// <summary>
    /// Keep every tenant's LinkedIn session alive for the poller (#233): where the kept
    /// session is missing, cleared by a bounce, or due to run out, the browser signs in with
    /// the board account's password (a PIN read from the tenant's mailbox, a tap in the
    /// LinkedIn app asked for by a moo) and the fresh cookies are sealed onto the account
    /// row. Needs a browser; a tenant is tried at most once per <see cref="PortalRetry"/>.
    /// Public for tests.
    /// </summary>
    public async Task<int> RenewLinkedInSessionsAsync(CancellationToken ct, bool onlyRequested = false)
    {
        if (_linkedin is null) return 0;
        long[] tenants;
        await using (var conn = await _db.OpenConnectionAsync(ct))
            tenants = (await conn.QueryAsync<long>(BoardAccountRepo.PortalRenewalDueSql,
                new { hosts = BoardAccountRepo.LinkedInHosts, ahead = PortalRenewAhead })).ToArray();
        var renewed = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            await using var conn = await _db.OpenConnectionAsync(ct);
            var accounts = new BoardAccountRepo(conn, tenantId, _protector, _loggers.CreateLogger<BoardAccountRepo>());
            var events = new AgentEventRepo(conn, tenantId);
            var account = await accounts.LinkedInAsync();
            if (account is null) continue;
            if (!await DueAsync(accounts, account.Host, _linkedinAttempts, tenantId, onlyRequested) || !await TryLockRenewalAsync(conn, tenantId, account.Host))
                continue;
            var target = BoardAccount.For(await accounts.TargetsAsync(), account.Host);
            if (target is null || target.Password.Length == 0)
            {
                _log.LogWarning("tenant {TenantId}: LinkedIn session for {User} needs renewing and the board account has no password", tenantId, account.Username);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = $"LinkedIn: the session for {account.Username} needs renewing and the board account has no password — save it under Settings · Agent · Board accounts" });
                continue;
            }
            var mailbox = await new MailboxSettingsRepo(conn, tenantId, _protector, _loggers.CreateLogger<MailboxSettingsRepo>()).GetTargetAsync();
            var notifications = new NotificationSettingsRepo(conn, tenantId, _protector, _loggers.CreateLogger<NotificationSettingsRepo>());
            Func<DateTimeOffset, CancellationToken, Task<string?>>? readCode = mailbox is null || _codes is null ? null
                : (since, token) => _codes.FindCodeAsync(mailbox, account.Username, "LinkedIn", since, token, "linkedin.com");
            try
            {
                var fresh = await _linkedin.RenewAsync(target.Username, target.Password, readCode,
                    (text, token) => _notifier.SendAsync(notifications, text, token), ct);
                await accounts.SavePortalSessionAsync(account.Host, fresh.Cookie, fresh.ExpiresAt);
                await events.RecordAsync(AgentEventRepo.Kinds.PortalSession, "",
                    new { host = account.Host, username = account.Username, expires_at = fresh.ExpiresAt });
                _log.LogInformation("tenant {TenantId}: LinkedIn session renewed for {User} until {Until:u}", tenantId, account.Username, fresh.ExpiresAt);
                renewed++;
                // The Easy Apply runs that stood down for want of this session go again now, not
                // on the reconciler's six-hour clock and not against its failure count (#278).
                var again = await ReadyReconciler.RequeueAwaitingSessionAsync(conn, tenantId, _protector, account.Host);
                if (again.Count > 0)
                    _log.LogInformation("tenant {TenantId}: {Count} Easy Apply run(s) that waited on the LinkedIn session queued again", tenantId, again.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("tenant {TenantId}: LinkedIn session renewal failed: {Reason}", tenantId, ex.Message);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = "LinkedIn session renewal failed: " + ex.Message });
            }
        }
        return renewed;
    }

    /// <summary>
    /// Keep every tenant's Handshake session alive for the poller (#267): where the kept
    /// session is missing, cleared by a bounce, or due to run out, the browser signs in with
    /// the board account's school email and password and the fresh cookie is sealed onto the
    /// account row. Handshake emails no code, so this needs no mailbox. Needs a browser; a
    /// tenant is tried at most once per <see cref="PortalRetry"/>. Public for tests.
    /// </summary>
    public async Task<int> RenewHandshakeSessionsAsync(CancellationToken ct, bool onlyRequested = false)
    {
        if (_handshake is null) return 0;
        long[] tenants;
        await using (var conn = await _db.OpenConnectionAsync(ct))
            tenants = (await conn.QueryAsync<long>(BoardAccountRepo.PortalRenewalDueSql,
                new { hosts = BoardAccountRepo.HandshakeHosts, ahead = PortalRenewAhead })).ToArray();
        var renewed = 0;
        foreach (var tenantId in tenants)
        {
            ct.ThrowIfCancellationRequested();
            await using var conn = await _db.OpenConnectionAsync(ct);
            var accounts = new BoardAccountRepo(conn, tenantId, _protector, _loggers.CreateLogger<BoardAccountRepo>());
            var events = new AgentEventRepo(conn, tenantId);
            var account = await accounts.HandshakeAsync();
            if (account is null) continue;
            if (!await DueAsync(accounts, account.Host, _handshakeAttempts, tenantId, onlyRequested) || !await TryLockRenewalAsync(conn, tenantId, account.Host))
                continue;
            var target = BoardAccount.For(await accounts.TargetsAsync(), account.Host);
            if (target is null || target.Password.Length == 0)
            {
                _log.LogWarning("tenant {TenantId}: Handshake session for {User} needs renewing and the board account has no password", tenantId, account.Username);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = $"Handshake: the session for {account.Username} needs renewing and the board account has no password — save it under Settings · Agent · Board accounts" });
                continue;
            }
            try
            {
                var fresh = await _handshake.RenewAsync(target.Username, target.Password, ct);
                await accounts.SavePortalSessionAsync(account.Host, fresh.Cookie, fresh.ExpiresAt);
                await events.RecordAsync(AgentEventRepo.Kinds.PortalSession, "",
                    new { host = account.Host, username = account.Username, expires_at = fresh.ExpiresAt });
                _log.LogInformation("tenant {TenantId}: Handshake session renewed for {User} until {Until:u}", tenantId, account.Username, fresh.ExpiresAt);
                renewed++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("tenant {TenantId}: Handshake session renewal failed: {Reason}", tenantId, ex.Message);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = "Handshake session renewal failed: " + ex.Message });
            }
        }
        return renewed;
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

            // Listings on a dead-end aggregator are retired before anything is spent on them (#281).
            var deadEnds = await ReadyReconciler.RetireDeadEndsAsync(conn, tenantId);
            if (deadEnds.Count > 0)
                _log.LogInformation("tenant {TenantId}: {Count} dead-end aggregator listing(s) marked passed: {Names}",
                    tenantId, deadEnds.Count, string.Join(", ", deadEnds));

            // A Workday row is never opened by the browser, so nothing ever found one closed: CVS
            // Health's and Voya's sat in Ready for days after the postings were taken down (#277).
            var gone = await RetireClosedWorkdayAsync(conn, tenantId, ct);
            if (gone.Count > 0)
                _log.LogInformation("tenant {TenantId}: {Count} Workday posting(s) no longer exist, marked passed: {Names}",
                    tenantId, gone.Count, string.Join(", ", gone));

            // Ready rows nothing else will ever revisit: one with no packet, one whose last
            // dry run died on something transient. Bounded, and never a captcha, a sign-in
            // wall or a submit that may already have landed (#274).
            if (_browser.IsConfigured)
            {
                var healed = await ReadyReconciler.ReconcileAsync(conn, tenantId, _protector, settings.LongTail, settings.LinkedInEasy);
                if (healed.Count > 0)
                    _log.LogInformation("tenant {TenantId}: {Prepared} stuck ready row(s) queued for a prepare, {Retried} for a retry: {Names}",
                        tenantId, healed.Prepared.Count, healed.Retried.Count, string.Join(", ", healed.Prepared.Concat(healed.Retried)));
            }

            // Ready packets that proved themselves in a dry run while the switch was still
            // on: with dry-run off now and a browser here, queue the real click. This is
            // the pass's half of #185 — the save that flips the switch does the same at
            // once, but a flip the api never saw (or saw with no browser fresh) lands here.
            if (_browser.IsConfigured && !settings.DryRun)
            {
                var promoted = await ReadyPromoter.PromoteCleanAsync(conn, tenantId, _protector, settings.LongTail, dryRun: false, settings.LinkedInEasy);
                if (promoted.Count > 0)
                    _log.LogInformation("tenant {TenantId}: {Count} clean dry run(s) queued for real submission: {Names}",
                        tenantId, promoted.Count, string.Join(", ", promoted));
            }

            var work = await LoadTenantWorkAsync(conn, tenantId, settings);
            if (!work.Cfg.IsConfigured)
            {
                _log.LogWarning("tenant {TenantId}: agent enabled but no LLM endpoint configured", tenantId);
                await events.RecordAsync(AgentEventRepo.Kinds.Error, "",
                    new { reason = "no LLM endpoint configured (Settings · AI or the instance default)" });
                return 0;
            }
            var apps = work.Scope.Apps;
            var criteria = await new CriteriaRepo(conn, tenantId).GetAsync();

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
                var verdict = await _evaluator.EvaluateAsync(rec, work.Resume, criteria, settings, work.Cfg, events, ct);
                judged++;
                if (verdict is { IsProceed: true })
                {
                    // Cheap liveness pre-check before the packet build. Competitive postings
                    // close within hours, so a lead can already be dead by the time it is
                    // judged; a single capped GET of the apply page catches that here, before
                    // any tokens are spent drafting answers and a cover letter. A closed lead
                    // is retired exactly the way the browser run and the human's Pass button
                    // do it — status `passed`, an audit row — and never reaches the builder.
                    var provider = AtsProvider.Detect(rec.Fields.Link, rec.Fields.Source);
                    if (await _evaluator.PostingClosedAsync(rec.Fields.Link, provider, ct))
                    {
                        await apps.UpdateStructuredAsync(rec.Name, rec.Fields with { Status = "passed" }, null);
                        await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name, new
                        {
                            reason = "posting closed before packet build — lead marked passed, no LLM spend",
                            rec.Fields.Company, rec.Fields.Role,
                        });
                        _log.LogInformation("{Name}: posting closed at judge time, marked passed", rec.Name);
                        continue;
                    }

                    // Step 3: prepare the packet, park it in `ready`, and moo. A failed
                    // build is recorded and must not stop the pass.
                    try
                    {
                        var packet = await _packets.BuildAsync(rec, verdict, work.Inputs, work.Scope, ct);
                        // With a browser that may drive this ATS, the moo waits for the
                        // dry-run fill (the submit lane sends it after the screenshot);
                        // otherwise this is it and the human applies by hand. An aggregator's
                        // listing page has no form on it, so it never gets a run (#191).
                        if (_browser.IsConfigured && rec.Fields.Link.Length > 0
                            && AtsProvider.BrowserCanSubmit(packet.Provider, settings.LongTail, settings.LinkedInEasy))
                            await new SubmitRequestRepo(conn, tenantId).EnqueueAsync(rec.Name, dryRun: true);
                        else
                            await _notifier.NotifyAsync(work.Notifications, work.Scope.Packets, events,
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

    /// <summary>
    /// Ask Workday whether each of the tenant's lead/ready Workday postings still exists, a few per
    /// pass and each at most twice a day, and retire the ones that answer 404. Anything other than
    /// a plain "gone" leaves the row alone.
    /// </summary>
    private async Task<List<string>> RetireClosedWorkdayAsync(NpgsqlConnection conn, long tenantId, CancellationToken ct)
    {
        var retired = new List<string>();
        var rows = await conn.QueryAsync<(string Name, string Link)>(
            "SELECT name, link FROM applications WHERE tenant_id = @t AND status IN ('lead', 'ready') AND link LIKE '%myworkday%' ORDER BY name",
            new { t = tenantId });
        var apps = new ApplicationRepo(conn, tenantId);
        var events = new AgentEventRepo(conn, tenantId);
        var asked = 0;
        foreach (var (name, link) in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (AtsProvider.WorkdayJobApi(link) is null) continue;
            var key = $"{tenantId}:{name}";
            if (_workdayChecked.TryGetValue(key, out var at) && DateTime.UtcNow - at < WorkdayRecheck) continue;
            if (asked++ >= 5) break;
            _workdayChecked[key] = DateTime.UtcNow;
            if (!await _evaluator.PostingClosedAsync(link, AtsProvider.Workday, ct)) continue;
            var rec = await apps.GetAsync(name);
            if (rec is null) continue;
            // At the version just read: a row the person is editing this moment is theirs, and the
            // next pass will find it again.
            try { await apps.UpdateStructuredAsync(name, rec.Fields with { Status = "passed" }, rec.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
            catch (AppConflictException) { continue; }
            await events.RecordAsync(AgentEventRepo.Kinds.Error, name, new
            {
                reason = "the Workday posting no longer exists — marked passed",
                rec.Fields.Company, rec.Fields.Role,
            });
            retired.Add(name);
        }
        return retired;
    }

    public override void Dispose()
    {
        _db.Dispose();
        base.Dispose();
    }
}
