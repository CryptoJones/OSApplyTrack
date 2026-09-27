// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Account-level self-service: export everything as one JSON document, import that
/// document on another instance, or delete the whole account. These replace billing in
/// the OSS build — the point is portability, so nobody is locked to one instance: you
/// can walk your data to a host with better features/pricing. All are under <c>/api</c>,
/// so the tenancy choke-point already requires a session and the repos arrive
/// pre-scoped — these only ever touch the caller's own tenant.
///
/// Two export flavours, told apart by their <c>format</c> field: the private migration
/// snapshot (everything, re-imported into your own account) and the shared opportunity
/// list (public posting facts only, handed to a peer who imports it as fresh leads).
/// </summary>
public static class AccountEndpoints
{
    // The format discriminators. Import branches on these: the peer-facing list takes
    // the non-destructive insert-if-absent path; our own snapshot (or a pre-1.3 file
    // with no format field) takes the overwrite-by-slug migration path.
    private const string SharedFormat = "applytrack-shared";
    private const string ExportFormat = "applytrack-export";

    // Upper bound on items accepted in one import. The whole load runs in a single
    // transaction (applications in batches of a thousand, #351), so an unbounded file
    // would still mean a long-held, lock-holding load — cap it. A self-host account is
    // small; this is generous headroom, not a real-world limit.
    private const int MaxImportItems = 10_000;
    // Status history runs to a handful of events per application.
    private const int MaxImportEvents = 10 * MaxImportItems;

    /// <summary>The import body cap, enforced before binding (Program.cs, #344).</summary>
    public const long MaxImportBytes = 10L * 1024 * 1024;

    // The export is a single private migration snapshot. snake_case + indented so it
    // round-trips byte-compatibly with the /api/* shapes and a human can read it.
    private static readonly JsonSerializerOptions ExportJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
    };

    /// <summary>The spreadsheet export's route; an API token may read it like the JSON one.</summary>
    public const string CsvPath = "/api/account/export.csv";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        // A full personal snapshot: every application (all fields + its slug, so apply links
        // survive a move) with its history and interviews, the search criteria and blacklist,
        // and since v2 (#357) the résumé with its source PDF, the cover letters, the person's
        // own answers, and the agent, LLM and notification settings, and the contacts, their
        // links and the notes log (#360) — as one downloadable JSON. No secret travels: not the LLM key, the Telegram token, the mailbox or
        // job-board passwords, sessions or API tokens; those are re-entered after a move.
        // An API token's export leaves out the LLM and notification settings too, as it
        // can't read those routes (TenantMiddleware.TokenMayReach). Built in memory — a
        // self-host account is small.
        app.MapGet("/api/account/export", async (
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist, StatusEventRepo history,
            AppEventRepo interviews, ResumeRepo resume, CoverLetterRepo letters, AnswerBankRepo answers,
            AgentSettingsRepo agent, LlmSettingsRepo llm, NotificationSettingsRepo notifications,
            ContactRepo contacts, AppNoteRepo notes, TenantContext tenant, HttpContext http) =>
        {
            // The whole account at a fixed URL: no browser or proxy may keep a copy.
            http.Response.Headers.CacheControl = "no-store";
            var records = await apps.ExportAllAsync();
            var session = tenant.TokenScope is null;
            var (people, links) = await contacts.ExportAllAsync();
            var doc = new ExportDoc(
                Applications: records.Select(ApplicationExport.From).ToList(),
                Criteria: await criteria.GetAsync(),
                Blacklist: await blacklist.ListAsync(),
                StatusEvents: await history.ExportAllAsync(),
                Interviews: await interviews.ExportAllAsync(),
                Resume: await ExportResumeAsync(resume),
                CoverLetters: await letters.ExportAllAsync(),
                AnswerBank: await answers.ExportHumanAsync(),
                AgentSettings: await agent.GetAsync(),
                LlmSettings: session ? await ExportLlmAsync(llm) : null,
                NotificationSettings: session ? await ExportNotificationsAsync(notifications) : null,
                Contacts: people,
                ApplicationContacts: links,
                Notes: await notes.ExportAllAsync());

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, ExportJson);
            var filename = $"applytrack-export-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", filename);
        });

        // The applications alone as a spreadsheet (#357): RFC 4180 CSV, one row per app, the
        // export's columns. Not a backup and never imported — a view for Excel or Sheets.
        app.MapGet(CsvPath, async (ApplicationRepo apps, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var records = await apps.ExportAllAsync();
            var bytes = ApplicationsCsv.Write(records.Select(ApplicationExport.From));
            var filename = $"applytrack-applications-{DateTime.UtcNow:yyyy-MM-dd}.csv";
            return Results.File(bytes, "text/csv; charset=utf-8", filename);
        });

        // The peer-facing counterpart: a curated opportunity list to hand to someone
        // else ("here's where I found 80 places hiring — work it too"). Only the facts
        // of the posting itself leave the account — slug (so the importer can de-dup),
        // company, role, link, location, source. Everything personal — status, notes,
        // contact, dates, score, salary — is stripped at the source, not trusted to a
        // client-side filter.
        app.MapGet("/api/account/export/shared", async (ApplicationRepo apps) =>
        {
            var records = await apps.ExportAllAsync();
            var doc = new SharedExportDoc(
                Applications: records.Select(SharedApplicationExport.From).ToList());

            var bytes = JsonSerializer.SerializeToUtf8Bytes(doc, ExportJson);
            var filename = $"applytrack-shared-{DateTime.UtcNow:yyyy-MM-dd}.json";
            return Results.File(bytes, "application/json", filename);
        });

        // Import a snapshot produced by export (this or another instance). Overwrite by
        // slug — an incoming app replaces a matching local one, a new slug is added,
        // untouched local apps stay; re-importing is idempotent. The whole load runs in
        // one transaction so a mid-import failure leaves the account untouched.
        app.MapPost("/api/account/import", async (
            ImportDoc body, IDbConnection conn, TenantContext tenant,
            ApplicationRepo apps, CriteriaRepo criteria, BlacklistRepo blacklist, StatusEventRepo history,
            AppEventRepo interviews, ResumeRepo resume, CoverLetterRepo letters, AnswerBankRepo answers,
            AgentSettingsRepo agent, LlmSettingsRepo llm, NotificationSettingsRepo notifications,
            ContactRepo contacts, AppNoteRepo notes) =>
        {
            var hasApps = body.Applications is { Count: > 0 };

            // A shared opportunity list imports differently from a migration snapshot:
            // every entry lands as a fresh lead with personal state blank, and a slug
            // the importer already tracks is skipped, never overwritten — a peer's
            // list must not clobber the importer's own pipeline. Whatever personal
            // fields a doctored file carries are dropped here, by construction.
            if (body.Format == SharedFormat)
            {
                if (!hasApps)
                    throw new AppValidationException("no importable data in file");
                if (body.Applications!.Count > MaxImportItems)
                    throw new AppValidationException(
                        $"too many applications to import (max {MaxImportItems})");

                var sharedDb = (DbConnection)conn;
                if (sharedDb.State != ConnectionState.Open)
                    await sharedDb.OpenAsync();
                await using var sharedTx = await sharedDb.BeginTransactionAsync();

                var added = await apps.InsertManyIfAbsentAsync(
                    body.Applications!.Select(a => (a.Name, a.ToSharedLeadFields())), sharedTx);
                var skipped = body.Applications!.Count - added;
                await sharedTx.CommitAsync();

                return Results.Ok(new
                {
                    imported_applications = added,
                    skipped_applications = skipped,
                });
            }

            // Only our own snapshot (or a pre-1.3 file with no discriminator) may take
            // the overwrite-by-slug path. Reject anything else — a typo, a foreign
            // exporter, or a future "applytrack-shared" variant — rather than silently
            // clobbering the importer's apps with a near-miss format string.
            if (!string.IsNullOrEmpty(body.Format) && body.Format != ExportFormat)
                throw new AppValidationException($"unrecognized import format \"{body.Format}\"");

            var hasCriteria = body.Criteria is { ValueKind: JsonValueKind.Object };
            var hasBlacklist = body.Blacklist is { Count: > 0 };
            // The v2 sections (#357). An API token can't change the LLM or notification
            // settings through their own routes, so it can't through a file either: a token's
            // import skips those two sections.
            var session = tenant.TokenScope is null;
            var hasResume = body.Resume is { ValueKind: JsonValueKind.Object };
            var hasAgent = body.AgentSettings is { ValueKind: JsonValueKind.Object };
            var hasLlm = session && body.LlmSettings is not null;
            var hasNotifications = session && body.NotificationSettings is not null;
            if (!hasApps && !hasCriteria && !hasBlacklist && !hasResume && !hasAgent && !hasLlm && !hasNotifications
                && body.AnswerBank is not { Count: > 0 } && body.Contacts is not { Count: > 0 })
                throw new AppValidationException("no importable data in file");
            if ((body.Applications?.Count ?? 0) > MaxImportItems
                || (body.Blacklist?.Count ?? 0) > MaxImportItems
                || (body.StatusEvents?.Count ?? 0) > MaxImportEvents
                || (body.Interviews?.Count ?? 0) > MaxImportEvents
                || (body.CoverLetters?.Count ?? 0) > MaxImportItems
                || (body.AnswerBank?.Count ?? 0) > MaxImportItems
                || (body.Contacts?.Count ?? 0) > MaxImportItems
                || (body.ApplicationContacts?.Count ?? 0) > MaxImportEvents
                || (body.Notes?.Count ?? 0) > MaxImportEvents)
                throw new AppValidationException($"import too large (max {MaxImportItems} items)");

            // Everything that can refuse the file is checked before the transaction opens.
            var importedResume = hasResume ? Resume.FromJson(body.Resume!.Value) : null;
            var importedPdf = hasResume ? ResumePdfFrom(body.Resume!.Value) : null;
            var importedAgent = hasAgent ? AgentSettings.FromJson(body.AgentSettings!.Value) : null;
            if (importedAgent is not null)
            {
                // The agent's switches never travel: a file can't start it applying on an
                // instance, or take it out of dry run. The standing answers and limits do.
                var current = await agent.GetAsync();
                importedAgent.Enabled = current.Enabled;
                importedAgent.DryRun = current.DryRun;
            }
            var llmPlan = hasLlm ? await PlanLlmAsync(body.LlmSettings!, llm) : null;
            if (hasNotifications)
                ValidateNotifications(body.NotificationSettings!);
            // Contacts (#360) are checked as the contacts routes check them.
            var importedContacts = (body.Contacts ?? [])
                .Select(c => (c.Id, new ContactFields(c.Name, c.Email, c.Phone, c.Role, c.Company, c.Linkedin, c.Notes).Normalized()))
                .ToList();
            // A link must name a contact the file carries: otherwise restoring the links would
            // clear the apps' people and put nobody back.
            var fileContacts = importedContacts.Select(c => c.Id).ToHashSet();
            if (body.ApplicationContacts?.FirstOrDefault(l => !fileContacts.Contains(l.Contact)) is { } orphan)
                throw new AppValidationException($"application_contacts names contact {orphan.Contact}, which the file's contacts don't carry");

            var db = (DbConnection)conn;
            if (db.State != ConnectionState.Open)
                await db.OpenAsync();
            await using var tx = await db.BeginTransactionAsync();

            var importedApps = body.Applications?.Count ?? 0;
            await apps.UpsertManyAsync((body.Applications ?? []).Select(a => (a.Name, a.ToFields())), tx);
            // A snapshot's status_events is the whole history of the apps it brings (#353): it
            // replaces theirs, "entered at import time" rows included, and an app it gives no
            // events ends up with none. Only those apps — another app's history is never
            // touched. A file with no status_events field (pre-1.59) keeps the import's rows.
            if (body.StatusEvents is not null)
                await history.ReplaceAsync((body.Applications ?? []).Select(a => a.Name), body.StatusEvents, tx);
            // Interviews (#354) likewise: the file's are the whole set for the apps it brings;
            // a file without the field (pre-1.60) leaves every app's interviews alone.
            if (body.Interviews is not null)
                await interviews.ReplaceAsync((body.Applications ?? []).Select(a => a.Name), body.Interviews, tx);
            // The notes log and contacts (#360), the same way: the file's notes and contact links
            // are the whole set for the apps it brings; a file without the field (pre-1.65)
            // leaves them alone. A contact matching one here by name and email overwrites it.
            var importedNotes = body.Notes is null ? 0
                : await notes.ReplaceAsync((body.Applications ?? []).Select(a => a.Name), body.Notes, tx);
            var contactIds = importedContacts.Count > 0 ? await contacts.MergeAsync(importedContacts, tx) : [];
            if (body.ApplicationContacts is not null)
                await contacts.ReplaceLinksAsync((body.Applications ?? []).Select(a => a.Name), body.ApplicationContacts, contactIds, tx);

            if (hasCriteria)
                await criteria.UpsertAsync(Criteria.FromJson(body.Criteria!.Value), tx);

            var importedBlacklist = 0;
            foreach (var company in body.Blacklist ?? [])
                if (await blacklist.AddAsync(company, tx))
                    importedBlacklist++;

            // A letter needs its application, so only the apps the file brings get theirs.
            var importedLetters = 0;
            var fileApps = (body.Applications ?? []).Select(a => Slug.Normalize(a.Name)).ToHashSet(StringComparer.Ordinal);
            foreach (var letter in body.CoverLetters ?? [])
            {
                var text = (letter.Body ?? "").Trim();
                if (text.Length == 0 || !fileApps.Contains(Slug.Normalize(letter.Application ?? "")))
                    continue;
                InputLimits.Text("cover_letter", text, InputLimits.Notes);
                InputLimits.Text("model", letter.Model, InputLimits.LlmModel);
                await letters.UpsertAsync(letter.Application!, text, (letter.Model ?? "").Trim(), tx);
                importedLetters++;
            }

            var importedAnswers = await answers.ImportAsync(body.AnswerBank ?? [], tx);

            // A résumé in the file replaces this one only when it says something; an empty
            // one (an account that never set one) leaves the importer's alone.
            var resumeApplied = false;
            if (importedResume is { IsEmpty: false })
            {
                await resume.UpsertAsync(importedResume, tx);
                resumeApplied = true;
            }
            if (importedPdf is { } pdf)
            {
                await resume.StorePdfAsync(pdf.Bytes, pdf.Name, tx);
                resumeApplied = true;
            }

            if (importedAgent is not null)
                await agent.UpsertAsync(importedAgent, tx);

            if (llmPlan is { } plan)
                await llm.UpsertAsync(plan.BaseUrl, plan.Model, plan.ClearKey, null,
                    body.LlmSettings!.CoverLettersEnabled, plan.Signature, tx);

            if (hasNotifications)
            {
                var n = body.NotificationSettings!;
                await notifications.UpsertPreferencesAsync(n.EmailEnabled, n.NotifyPacketReady,
                    n.NotifySecurityCode, n.NotifySubmitFailed, n.NotifyFollowupDue, tx);
                if (n.ReminderHour is { } hour)
                    await notifications.UpsertReminderHourAsync(hour, tx);
                if (n.DigestEnabled is not null || n.DigestInsults is not null || n.DigestHour is not null)
                    await notifications.UpsertDigestAsync(n.DigestEnabled, n.DigestInsults, n.DigestHour, tx);
            }

            await tx.CommitAsync();

            return Results.Ok(new
            {
                imported_applications = importedApps,
                imported_blacklist = importedBlacklist,
                criteria_applied = hasCriteria,
                imported_cover_letters = importedLetters,
                imported_answers = importedAnswers,
                resume_applied = resumeApplied,
                agent_settings_applied = importedAgent is not null,
                llm_settings_applied = llmPlan is not null,
                notification_settings_applied = hasNotifications,
                imported_contacts = importedContacts.Count,
                imported_notes = importedNotes,
            });
        }).RequireRateLimiting("upload");

        // Delete the account. The FK cascades (0005/0006/0009) drop every per-tenant row
        // in one statement; clearing the cookie tidies the now-dangling session client-side.
        app.MapDelete("/api/account", async (
            HttpContext ctx, TenantContext tenant, UserRepo users) =>
        {
            await users.DeleteAsync(tenant.TenantId);
            ctx.Response.Cookies.Delete(AuthCookie.Name, AuthCookie.DeleteOptions(ctx.Request.IsHttps));
            return Results.NoContent();
        });

        // Where am I signed in (#346): this account's live sessions — created, last used,
        // expiry, browser — with the one this request came from flagged "current".
        app.MapGet("/api/account/sessions", async (TenantContext tenant, SessionRepo sessions) =>
            Results.Ok(await sessions.ListAsync(tenant.TenantId, tenant.SessionId)));

        // Sign out everywhere else: revoke every session but this browser's. Logging this
        // one out too is POST /api/auth/logout.
        app.MapDelete("/api/account/sessions", async (TenantContext tenant, SessionRepo sessions) =>
            Results.Ok(new { revoked = await sessions.DeleteOthersAsync(tenant.TenantId, tenant.SessionId) }));

        // Revoke one listed session. Revoking this browser's own also clears its cookie.
        app.MapDelete("/api/account/sessions/{id}", async (
            string id, HttpContext ctx, TenantContext tenant, SessionRepo sessions) =>
        {
            if (!await sessions.DeleteByIdAsync(tenant.TenantId, id))
                throw new AppNotFoundException("session not found");
            if (id == tenant.SessionId)
                ctx.Response.Cookies.Delete(AuthCookie.Name, AuthCookie.DeleteOptions(ctx.Request.IsHttps));
            return Results.NoContent();
        });

        // Personal API tokens (#356): for scripts and clippers that can't carry the cookie. The
        // secret is in the create answer only; the list never has it. A token can't reach
        // these routes itself (TenantMiddleware.TokenMayReach), so only a session makes them.
        app.MapGet("/api/account/tokens", async (ApiTokenRepo tokens) => Results.Ok(await tokens.ListAsync()));

        app.MapPost("/api/account/tokens", async (NewTokenRequest body, ApiTokenRepo tokens) =>
        {
            var made = await tokens.CreateAsync(body.Name, body.Scope);
            return Results.Created($"/api/account/tokens/{made.Id}", made);
        });

        app.MapDelete("/api/account/tokens/{id:long}", async (long id, ApiTokenRepo tokens) =>
            await tokens.RevokeAsync(id) ? Results.NoContent() : throw new AppNotFoundException("token not found"));
    }

    public sealed record NewTokenRequest(string? Name, string? Scope);

    // The résumé as the /api/resume shape plus its source PDF, base64 — or null when the
    // account has neither. A PDF sealed under a key this instance lost is left out.
    private static async Task<JsonObject?> ExportResumeAsync(ResumeRepo repo)
    {
        var profile = await repo.GetAsync();
        (byte[] Bytes, string Name)? pdf = null;
        try { pdf = await repo.GetPdfAsync(); }
        catch (CryptographicException) { }
        if (profile.IsEmpty && pdf is null)
            return null;
        var node = JsonSerializer.SerializeToNode(profile, ExportJson)!.AsObject();
        node["source_pdf"] = pdf is { } p ? Convert.ToBase64String(p.Bytes) : null;
        node["source_pdf_name"] = pdf?.Name ?? "";
        return node;
    }

    private static async Task<LlmSettingsExport> ExportLlmAsync(LlmSettingsRepo repo)
    {
        var (baseUrl, model, _, enabled) = await repo.GetViewAsync();
        return new LlmSettingsExport(baseUrl, model, enabled, await repo.GetCoverLetterSignatureAsync());
    }

    private static async Task<NotificationSettingsExport> ExportNotificationsAsync(NotificationSettingsRepo repo)
    {
        var v = await repo.GetViewAsync();
        var ev = v.Events ?? new NotificationEvents();
        var d = await repo.GetDigestAsync();
        return new NotificationSettingsExport(v.EmailEnabled, ev.PacketReady, ev.SecurityCode, ev.SubmitFailed,
            ev.FollowupDue, await repo.GetReminderHourAsync(), d.Enabled, d.Insults, d.Hour);
    }

    // The résumé file from an import, checked as the upload is: base64 that decodes to a PDF
    // no bigger than an upload may be. Null when the file carries none.
    private static (byte[] Bytes, string Name)? ResumePdfFrom(JsonElement resume)
    {
        if (!resume.TryGetProperty("source_pdf", out var el) || el.ValueKind != JsonValueKind.String
            || el.GetString() is not { Length: > 0 } b64)
            return null;
        if (b64.Length > (ResumePdfImporter.MaxPdfBytes + 2) / 3 * 4)
            throw new AppValidationException("resume PDF is too large (max 5 MB)");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(b64); }
        catch (FormatException) { throw new AppValidationException("resume source_pdf is not base64"); }
        if (bytes.Length > ResumePdfImporter.MaxPdfBytes)
            throw new AppValidationException("resume PDF is too large (max 5 MB)");
        if (!bytes.AsSpan().StartsWith("%PDF-"u8))
            throw new AppValidationException("resume source_pdf is not a PDF");
        var name = resume.TryGetProperty("source_pdf_name", out var n) && n.ValueKind == JsonValueKind.String
            ? Path.GetFileName((n.GetString() ?? "").Trim()) : "";
        if (name.Length > 255) name = name[..255];
        return (bytes, name.Length > 0 ? name : "resume.pdf");
    }

    private sealed record LlmPlan(string BaseUrl, string Model, bool ClearKey, string? Signature);

    // The LLM settings a file asks for, validated as the PUT validates them. A field the file
    // leaves out keeps its value. The stored key is kept for the same endpoint only: a key
    // entered for one base URL is never sent to another a file names.
    private static async Task<LlmPlan> PlanLlmAsync(LlmSettingsExport s, LlmSettingsRepo repo)
    {
        var (currentUrl, currentModel, hasKey, _) = await repo.GetViewAsync();
        var baseUrl = s.BaseUrl?.Trim() ?? currentUrl;
        var model = s.Model?.Trim() ?? currentModel;
        var signature = s.CoverLetterSignature?.Trim();
        InputLimits.Text("base_url", baseUrl, InputLimits.LlmBaseUrl);
        InputLimits.Text("model", model, InputLimits.LlmModel);
        InputLimits.Text("cover_letter_signature", signature, InputLimits.CoverLetterSignature);
        LlmEndpointPolicy.ValidateTenantBaseUrl(baseUrl);
        return new LlmPlan(baseUrl, model, hasKey && !string.Equals(baseUrl, currentUrl, StringComparison.Ordinal), signature);
    }

    private static void ValidateNotifications(NotificationSettingsExport n)
    {
        if (n.ReminderHour is < 0 or > 23)
            throw new AppValidationException("the reminder hour is a number from 0 to 23 (UTC)");
        if (n.DigestHour is < 0 or > 23)
            throw new AppValidationException("the digest hour is a number from 0 to 23 (UTC)");
    }

    /// <summary>The export envelope — a versioned, self-describing migration snapshot.
    /// Version 2 (#357) added everything after <c>interviews</c>; import takes either. The
    /// contacts, their links and the notes log (#360) are later v2 sections, additive: an older
    /// release's import ignores them.</summary>
    private sealed record ExportDoc(
        IReadOnlyList<ApplicationExport> Applications,
        Criteria Criteria,
        IReadOnlyList<string> Blacklist,
        IReadOnlyList<StatusEventExport> StatusEvents,
        IReadOnlyList<InterviewExport> Interviews,
        JsonObject? Resume,
        IReadOnlyList<CoverLetterExport> CoverLetters,
        IReadOnlyList<AnswerExport> AnswerBank,
        AgentSettings AgentSettings,
        LlmSettingsExport? LlmSettings,
        NotificationSettingsExport? NotificationSettings,
        IReadOnlyList<ContactExport> Contacts,
        IReadOnlyList<ApplicationContactExport> ApplicationContacts,
        IReadOnlyList<AppNoteExport> Notes)
    {
        [JsonPropertyOrder(-3)] public string Format => ExportFormat;
        [JsonPropertyOrder(-2)] public int Version => 2;
        [JsonPropertyOrder(-1)] public DateTime ExportedAt => DateTime.UtcNow;
    }

    /// <summary>
    /// The shared-list envelope — same self-describing shape as <see cref="ExportDoc"/>
    /// but a distinct <c>format</c>, so import can tell the two apart. No criteria, no
    /// blacklist: those are personal settings and never leave the account this way.
    /// </summary>
    private sealed record SharedExportDoc(
        IReadOnlyList<SharedApplicationExport> Applications)
    {
        [JsonPropertyOrder(-3)] public string Format => SharedFormat;
        [JsonPropertyOrder(-2)] public int Version => 1;
        [JsonPropertyOrder(-1)] public DateTime ExportedAt => DateTime.UtcNow;
    }
}

/// <summary>
/// One application reduced to the facts of the posting itself: the slug (the importer's
/// de-dup key) plus company, role, link, location and source. Everything that describes
/// the exporter rather than the job — status, contact, dates, score, salary, notes —
/// has no field here, so it cannot leak by accident.
/// </summary>
public sealed record SharedApplicationExport(
    string Name, string Company, string Role, string Link, string Location, string Source)
{
    public static SharedApplicationExport From(AppRecord r) =>
        new(r.Name, r.Fields.Company, r.Fields.Role, r.Fields.Link, r.Fields.Location, r.Fields.Source);
}

/// <summary>
/// One application flattened for export/import: the slug <c>name</c> alongside the 15
/// structured fields, in the snake_case shape the rest of the API uses. Bridges the
/// repo's <see cref="AppRecord"/> and the <see cref="AppFields"/> the importer writes.
/// </summary>
public sealed record ApplicationExport(
    string Name, string Company, string Role, string Lane, string Status, string Link,
    string Location, string Salary, string Source, string Contact, string ContactEmail,
    string Applied, string Followup, string Created, string Score, string Notes)
{
    public static ApplicationExport From(AppRecord r)
    {
        var f = r.Fields;
        return new ApplicationExport(
            r.Name, f.Company, f.Role, f.Lane, f.Status, f.Link, f.Location, f.Salary,
            f.Source, f.Contact, f.ContactEmail, f.Applied, f.Followup, f.Created, f.Score, f.Notes);
    }

    public AppFields ToFields() => new()
    {
        Company = Company, Role = Role, Lane = Lane, Status = Status, Link = Link,
        Location = Location, Salary = Salary, Source = Source, Contact = Contact,
        ContactEmail = ContactEmail, Applied = Applied, Followup = Followup,
        Created = Created, Score = Score, Notes = Notes,
    };

    /// <summary>
    /// The shared-list import view: only the public posting facts cross accounts.
    /// Status falls back to the <see cref="AppFields"/> default (<c>lead</c>) and every
    /// personal field stays blank, whatever the incoming document claims.
    /// </summary>
    public AppFields ToSharedLeadFields() => new()
    {
        Company = Company, Role = Role, Link = Link, Location = Location, Source = Source,
    };
}

/// <summary>The LLM settings in the account export (#357): endpoint, model, the cover-letter
/// switch and signature — never the API key. Null on import leaves a value alone.</summary>
public sealed record LlmSettingsExport(
    string? BaseUrl, string? Model, bool? CoverLettersEnabled, string? CoverLetterSignature);

/// <summary>The notification preferences in the account export (#357): the email switch, the
/// per-event toggles, the reminder and digest. Telegram stays behind — its bot token is a
/// secret, and a chat id without it sends nothing. Null on import leaves a value alone.</summary>
public sealed record NotificationSettingsExport(
    bool? EmailEnabled, bool? NotifyPacketReady, bool? NotifySecurityCode, bool? NotifySubmitFailed,
    bool? NotifyFollowupDue, int? ReminderHour, bool? DigestEnabled, bool? DigestInsults, int? DigestHour);

/// <summary>
/// The applications as RFC 4180 CSV (#357): a header row, CRLF line ends, every field quoted
/// when it holds a comma, quote or line break, quotes doubled. A cell a spreadsheet would run
/// as a formula — one starting <c>= + - @</c>, or a tab or carriage return — gets a leading
/// <c>'</c> so it reads as text. A UTF-8 byte-order mark leads, so Excel reads accents right.
/// </summary>
public static class ApplicationsCsv
{
    public static readonly string[] Columns =
    [
        "name", "company", "role", "lane", "status", "link", "location", "salary", "source",
        "contact", "contact_email", "applied", "followup", "created", "score", "notes",
    ];

    public static byte[] Write(IEnumerable<ApplicationExport> apps)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(string.Join(',', Columns)).Append("\r\n");
        foreach (var a in apps)
        {
            string[] row =
            [
                a.Name, a.Company, a.Role, a.Lane, a.Status, a.Link, a.Location, a.Salary, a.Source,
                a.Contact, a.ContactEmail, a.Applied, a.Followup, a.Created, a.Score, a.Notes,
            ];
            sb.Append(string.Join(',', row.Select(Cell))).Append("\r\n");
        }
        return [.. System.Text.Encoding.UTF8.GetPreamble(), .. System.Text.Encoding.UTF8.GetBytes(sb.ToString())];
    }

    /// <summary>One field, defused and quoted as needed. Public for tests.</summary>
    public static string Cell(string? value)
    {
        var v = value ?? "";
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            v = "'" + v;
        return v.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

/// <summary>
/// The posted import body. Every part is optional so a hand-trimmed file (just apps,
/// say) still imports; the endpoint rejects a file with nothing usable. <c>Criteria</c>
/// stays a raw <see cref="JsonElement"/> so it runs through <see cref="Criteria.FromJson"/>
/// — the same normalization the <c>/api/criteria</c> PUT applies. <c>Format</c> selects
/// the import semantics: <c>applytrack-shared</c> gets the leads-only path, anything
/// else (including absent — pre-1.3 exports) the migration upsert.
/// </summary>
public sealed record ImportDoc(
    string? Format,
    List<ApplicationExport>? Applications,
    JsonElement? Criteria,
    List<string>? Blacklist,
    List<StatusEventExport>? StatusEvents = null,
    List<InterviewExport>? Interviews = null,
    JsonElement? Resume = null,
    List<CoverLetterExport>? CoverLetters = null,
    List<AnswerExport>? AnswerBank = null,
    JsonElement? AgentSettings = null,
    LlmSettingsExport? LlmSettings = null,
    NotificationSettingsExport? NotificationSettings = null,
    List<ContactExport>? Contacts = null,
    List<ApplicationContactExport>? ApplicationContacts = null,
    List<AppNoteExport>? Notes = null);
