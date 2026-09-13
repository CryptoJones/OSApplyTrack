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
    // This container's name: the quadlet/compose service name, or the pod's hostname.
    private readonly string _workerId = Environment.MachineName;

    public AgentWorker(
        string connectionString, AgentOptions options, LlmOptions llmOptions,
        SecretProtector protector, LeadEvaluator evaluator, PacketBuilder packets,
        PacketReadyNotifier notifier, BrowserOptions browser, IBrowserSubmitter submitter,
        ILoggerFactory loggers, ISecurityCodeSource? codes = null, INotifier? telegram = null)
    {
        _codes = codes;
        _telegram = telegram;
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
        var inputs = new PacketInputs(
            resume, settings, email, await llmSettings.GetCoverLetterSignatureAsync(), lettersEnabled, cfg);
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
            if (rec.Fields.Link.Length == 0 || !AtsProvider.BrowserCanSubmit(packet.Provider, settings.LongTail))
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
        // The tenant's dry-run switch wins over the request: the agent never submits
        // for an account that has not turned dry-run off.
        var dryRun = req.DryRun || settings.DryRun;
        if (!dryRun && packet.BlockingReview().Any())
            dryRun = true;

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
        if (PacketBuilder.RefreshStandardAnswers(packet, new AnswerContext(resume, settings, email, coverLetter, packet.PostingExcerpt)))
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
        async Task<string?> AwaitSecurityCodeAsync(string recipient, CancellationToken token)
        {
            var since = DateTimeOffset.UtcNow.AddMinutes(-1);
            var mailbox = _codes is null ? null
                : await new MailboxSettingsRepo(conn, t, _protector, _loggers.CreateLogger<MailboxSettingsRepo>()).GetTargetAsync();
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.AwaitingCode, rec.Fields.Link, "",
                new { recipient, dry_run = false, mailbox = mailbox is not null }, null);
            await events.RecordAsync(AgentEvidenceRepo.Kinds.AwaitingCode, rec.Name,
                new { recipient, mailbox = mailbox is not null, rec.Fields.Company, rec.Fields.Role });
            TelegramCodeReplies? replies = null;
            async Task MooAndListenAsync()
            {
                // Replies dated before the moo (a 15 s grace for clock skew) are never a code.
                var mooed = DateTimeOffset.UtcNow.AddSeconds(-15);
                await _notifier.NotifyAsync(notifications, packets, events, rec.Name, rec.Fields.Company, rec.Fields.Role, token,
                    PacketReadyNotifier.Moment.Code, recipient);
                var target = _telegram is null ? null : await notifications.GetTargetAsync();
                replies = target is null ? null : new TelegramCodeReplies(_telegram!, target, mooed, _log);
            }
            if (mailbox is null)
                await MooAndListenAsync();
            _log.LogInformation("{Name}: parked — the board emailed a security code to {Recipient}{How}", rec.Name, recipient,
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
                        var code = await _codes!.FindCodeAsync(mailbox, recipient, rec.Fields.Company, since, token);
                        if (code is not null)
                        {
                            _log.LogInformation("{Name}: security code read from the mailbox", rec.Name);
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
                dryRun ? null : AwaitSecurityCodeAsync);
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
            await evidence.RecordAsync(rec.Name, AgentEvidenceRepo.Kinds.Failed, rec.Fields.Link, "",
                new { reason, dry_run = dryRun }, null);
            await events.RecordAsync(AgentEventRepo.Kinds.Error, rec.Name,
                new { reason = "browser: " + reason, rec.Fields.Company, rec.Fields.Role });
            _log.LogWarning(ex, "{Name}: browser submission failed", rec.Name);
            return false;
        }

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
        //   - the tenant has explicitly turned dry-run off (settings.DryRun == false)
        //   - no required field went unmapped
        //   - no REQUIRED question is still waiting on the user (an optional one the model
        //     declined is left blank on purpose and must not park the packet forever)
        // The caller re-queues with dryRun:false, and that real run cannot promote again
        // because its kind will never be DryRun. A packet filled while the switch was still
        // on is picked up later by ReadyPromoter — on the flip, or on the next pass (#185).
        return kind == AgentEvidenceRepo.Kinds.DryRun
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

            // Ready packets that proved themselves in a dry run while the switch was still
            // on: with dry-run off now and a browser here, queue the real click. This is
            // the pass's half of #185 — the save that flips the switch does the same at
            // once, but a flip the api never saw (or saw with no browser fresh) lands here.
            if (_browser.IsConfigured && !settings.DryRun)
            {
                var promoted = await ReadyPromoter.PromoteCleanAsync(conn, tenantId, _protector, settings.LongTail, dryRun: false);
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
                            && AtsProvider.BrowserCanSubmit(packet.Provider, settings.LongTail))
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

    public override void Dispose()
    {
        _db.Dispose();
        base.Dispose();
    }
}
