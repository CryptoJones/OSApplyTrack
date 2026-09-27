// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Data;
using System.Globalization;
using System.Text;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Notifications;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Follow-ups and interviews (#354): an application's interviews
/// (<c>GET/POST /api/apps/{name}/interviews</c>, <c>DELETE …/{id}</c>), what is due
/// (<c>GET /api/due</c>, the SPA's Due chip and view), and the calendar feed — a
/// revocable, feed-only secret link (<c>GET/POST/DELETE /api/calendar/feed</c>) to
/// <c>GET /api/calendar.ics?token=</c>, the one route here a calendar client reads
/// without a session, since it can't send the cookie.
/// </summary>
public static class RemindersEndpoints
{
    /// <summary>The feed's path; the tenancy middleware lets it through without a session.</summary>
    public const string FeedPath = "/api/calendar.ics";

    /// <summary>How far back the feed keeps interviews.</summary>
    public static readonly TimeSpan FeedHistory = TimeSpan.FromDays(90);

    /// <summary>Interviews the Due view still shows after they started.</summary>
    public static readonly TimeSpan DueGrace = TimeSpan.FromHours(12);

    public sealed record NewInterview(string? At, string? Note);

    public static void MapRemindersEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/apps/{name}/interviews", async (string name, AppEventRepo events) =>
            Results.Ok(await events.ListAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'")));

        app.MapPost("/api/apps/{name}/interviews", async (string name, NewInterview body, AppEventRepo events) =>
        {
            var interview = await events.AddAsync(name, ParseInstant(body.At), body.Note);
            return Results.Created($"/api/apps/{Uri.EscapeDataString(name)}/interviews/{interview.Id}", interview);
        });

        app.MapDelete("/api/apps/{name}/interviews/{id:long}", async (string name, long id, AppEventRepo events) =>
            await events.DeleteAsync(name, id)
                ? Results.NoContent()
                : throw new AppNotFoundException("interview not found"));

        // What needs the person: follow-ups due on or before ?today= (the client's own date;
        // the UTC date when absent) and interviews from twelve hours ago to a week ahead.
        app.MapGet("/api/due", async (string? today, AppEventRepo events) =>
        {
            var now = DateTime.UtcNow;
            var day = DateOnly.FromDateTime(now);
            if (!string.IsNullOrWhiteSpace(today))
                day = AppEventRepo.ParseDay(today) ?? throw new AppValidationException("today must be a date (YYYY-MM-DD)");
            return Results.Ok(await events.DueAsync(day, now - DueGrace, now + AppEventRepo.InterviewHorizon));
        });

        app.MapGet("/api/calendar/feed", async (ApiTokenRepo tokens) => Results.Ok(await tokens.GetCalendarAsync()));

        // A new link — the old one, if any, stops working. The token is in the answer once.
        app.MapPost("/api/calendar/feed", async (ApiTokenRepo tokens, IConfiguration config) =>
        {
            var token = await tokens.CreateCalendarAsync();
            var path = $"{FeedPath}?token={Uri.EscapeDataString(token)}";
            var baseUrl = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
            var view = await tokens.GetCalendarAsync();
            return Results.Ok(new
            {
                enabled = true,
                path,
                url = baseUrl.Length > 0 ? baseUrl + path : null,
                created_at = view.CreatedAt,
            });
        }).RequireRateLimiting("notify");

        app.MapDelete("/api/calendar/feed", async (ApiTokenRepo tokens) =>
        {
            await tokens.RevokeCalendarAsync();
            return Results.NoContent();
        });

        app.MapGet(FeedPath, async (HttpContext ctx, string? token, IDbConnection conn, IConfiguration config) =>
        {
            var tenant = await ApiTokenRepo.ResolveAsync(conn, token, ApiTokenRepo.CalendarScope)
                ?? throw new AppNotFoundException("calendar feed not found");
            var now = DateTime.UtcNow;
            var (followups, interviews) = await new AppEventRepo(conn, tenant).CalendarAsync(now - FeedHistory);
            var baseUrl = (config["App:PublicBaseUrl"] ?? "").Trim().TrimEnd('/');
            ctx.Response.Headers.CacheControl = "private, no-cache";
            return Results.Text(CalendarFeed.Build(followups, interviews, now, baseUrl), "text/calendar; charset=utf-8");
        }).RequireRateLimiting("calendar");
    }

    /// <summary>An ISO instant; one without an offset is taken as UTC.</summary>
    public static DateTime ParseInstant(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
            throw new AppValidationException("at must be a date and time (ISO 8601, e.g. 2026-10-01T15:00:00Z)");
        if (at.Year is < 2000 or > 2100)
            throw new AppValidationException("at is out of range");
        return at.UtcDateTime;
    }
}

/// <summary>
/// The ICS (RFC 5545) text of a tenant's calendar feed: each follow-up as an all-day event
/// on its date, each interview as an hour from its start. UIDs are stable per application
/// and per interview, so a calendar updates an event in place rather than duplicating it.
/// Pure, for tests.
/// </summary>
public static class CalendarFeed
{
    public static readonly TimeSpan InterviewLength = TimeSpan.FromHours(1);

    public static string Build(IReadOnlyList<DueFollowup> followups, IReadOnlyList<DueInterview> interviews,
        DateTime now, string publicBaseUrl)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(Fold(s)).Append("\r\n");
        var stamp = Stamp(now);
        Line("BEGIN:VCALENDAR");
        Line("VERSION:2.0");
        Line("PRODID:-//OSApplyTrack//Reminders//EN");
        Line("CALSCALE:GREGORIAN");
        Line("METHOD:PUBLISH");
        Line("X-WR-CALNAME:OSApplyTrack");
        foreach (var f in followups)
        {
            if (AppEventRepo.ParseDay(f.Followup) is not { } day) continue;
            var link = PacketReadyNotifier.DeepLink(publicBaseUrl, f.Name);
            Line("BEGIN:VEVENT");
            Line($"UID:followup-{f.Id}@osapplytrack");
            Line($"DTSTAMP:{stamp}");
            Line($"DTSTART;VALUE=DATE:{day:yyyyMMdd}");
            Line($"DTEND;VALUE=DATE:{day.AddDays(1):yyyyMMdd}");
            Line("SUMMARY:" + Escape($"Follow up: {Who(f.Company, f.Role)}"));
            Line("DESCRIPTION:" + Escape($"#{f.Id} · {f.Status}" + (link is null ? "" : "\n" + link)));
            if (link is not null) Line("URL:" + link);
            Line("TRANSP:TRANSPARENT");
            Line("END:VEVENT");
        }
        foreach (var i in interviews)
        {
            var link = PacketReadyNotifier.DeepLink(publicBaseUrl, i.Name);
            var note = i.Note.Trim();
            var start = AppEventRepo.Utc(i.At);
            Line("BEGIN:VEVENT");
            Line($"UID:interview-{i.Id}@osapplytrack");
            Line($"DTSTAMP:{stamp}");
            Line($"DTSTART:{Stamp(start)}");
            Line($"DTEND:{Stamp(start + InterviewLength)}");
            Line("SUMMARY:" + Escape($"Interview: {Who(i.Company, i.Role)}"));
            var description = string.Join("\n", new[] { note, link ?? "" }.Where(s => s.Length > 0));
            if (description.Length > 0) Line("DESCRIPTION:" + Escape(description));
            if (link is not null) Line("URL:" + link);
            Line("END:VEVENT");
        }
        Line("END:VCALENDAR");
        return sb.ToString();
    }

    private static string Who(string company, string role)
    {
        var who = string.Join(" · ", new[] { company.Trim(), role.Trim() }.Where(s => s.Length > 0));
        return who.Length > 0 ? who : "an application";
    }

    private static string Stamp(DateTime at) =>
        AppEventRepo.Utc(at).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A TEXT value: backslash, semicolon and comma escaped, line breaks as \n.</summary>
    public static string Escape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
            .Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\\n");

    /// <summary>Fold a content line at 75 octets, never inside a UTF-8 sequence.</summary>
    public static string Fold(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) <= 75) return line;
        var sb = new StringBuilder();
        var octets = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (octets + n > limit)
            {
                sb.Append("\r\n ");
                octets = 0;
                limit = 74; // the leading space counts
            }
            sb.Append(rune.ToString());
            octets += n;
        }
        return sb.ToString();
    }
}
