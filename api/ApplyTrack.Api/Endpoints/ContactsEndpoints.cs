// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using ApplyTrack.Api.Data;
using Microsoft.AspNetCore.Mvc;

namespace ApplyTrack.Api.Endpoints;

/// <summary>
/// Contacts and the interaction log (#360): the people behind a process
/// (<c>GET/POST /api/contacts</c>, <c>GET/PUT/DELETE /api/contacts/{id}</c>, the PUT
/// taking <c>?expected_version=</c> and answering 409 like an application's), who is on
/// an application (<c>GET/POST /api/apps/{name}/contacts</c>, <c>DELETE …/{id}</c>), and
/// its dated notes (<c>GET/POST /api/apps/{name}/notes</c>, <c>DELETE …/{id}</c>), which
/// also join the timeline at <c>/api/apps/{name}/history</c> as kind <c>note</c>.
/// </summary>
public static class ContactsEndpoints
{
    /// <summary>A link: an existing <c>contact_id</c>, or a new <c>contact</c> made on the spot,
    /// and the person's part in this process.</summary>
    public sealed record NewLink(long? ContactId, ContactFields? Contact, string? Role);

    public sealed record NewNote(string? Body, string? At);

    public static void MapContactsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/contacts", async (ContactRepo contacts) => Results.Ok(await contacts.ListAsync()));

        app.MapPost("/api/contacts", async (ContactFields body, ContactRepo contacts) =>
        {
            var made = await contacts.CreateAsync(body);
            return Results.Created($"/api/contacts/{made.Id}", made);
        });

        app.MapGet("/api/contacts/{id:long}", async (long id, ContactRepo contacts) =>
            Results.Ok(await contacts.GetAsync(id) ?? throw new AppNotFoundException("contact not found")));

        app.MapPut("/api/contacts/{id:long}", async (
            long id, ContactFields body, [FromQuery(Name = "expected_version")] string? expectedVersion, ContactRepo contacts) =>
            Results.Ok(await contacts.UpdateAsync(id, body, expectedVersion)));

        app.MapDelete("/api/contacts/{id:long}", async (long id, ContactRepo contacts) =>
            await contacts.DeleteAsync(id) ? Results.NoContent() : throw new AppNotFoundException("contact not found"));

        app.MapGet("/api/apps/{name}/contacts", async (string name, ContactRepo contacts) =>
            Results.Ok(await contacts.ForApplicationAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'")));

        app.MapPost("/api/apps/{name}/contacts", async (string name, NewLink body, ContactRepo contacts, ApplicationRepo apps) =>
        {
            long contactId;
            if (body.ContactId is { } id)
                contactId = id;
            else if (body.Contact is { } fields)
            {
                // Checked first, so a new contact is never left behind by a link to no app.
                fields.Normalized();
                _ = await apps.GetAsync(name) ?? throw new AppNotFoundException($"application not found: '{name}'");
                contactId = (await contacts.CreateAsync(fields)).Id;
            }
            else
                throw new AppValidationException("give a contact_id, or a contact to add");
            var linked = await contacts.LinkAsync(name, contactId, body.Role);
            return Results.Created($"/api/apps/{Uri.EscapeDataString(name)}/contacts/{contactId}", linked);
        });

        app.MapDelete("/api/apps/{name}/contacts/{id:long}", async (string name, long id, ContactRepo contacts) =>
            await contacts.UnlinkAsync(name, id)
                ? Results.NoContent()
                : throw new AppNotFoundException("that contact is not on this application"));

        app.MapGet("/api/apps/{name}/notes", async (string name, AppNoteRepo notes) =>
            Results.Ok(await notes.ListAsync(name)
                ?? throw new AppNotFoundException($"application not found: '{name}'")));

        app.MapPost("/api/apps/{name}/notes", async (string name, NewNote body, AppNoteRepo notes) =>
        {
            DateTime? at = string.IsNullOrWhiteSpace(body.At) ? null : RemindersEndpoints.ParseInstant(body.At);
            var note = await notes.AddAsync(name, at, body.Body);
            return Results.Created($"/api/apps/{Uri.EscapeDataString(name)}/notes/{note.Id}", note);
        });

        app.MapDelete("/api/apps/{name}/notes/{id:long}", async (string name, long id, AppNoteRepo notes) =>
            await notes.DeleteAsync(name, id) ? Results.NoContent() : throw new AppNotFoundException("note not found"));
    }

    /// <summary>The timeline with the notes merged in, in time order, a note after anything
    /// else at the same instant. Pure, for tests.</summary>
    public static IReadOnlyList<TimelineEntry> WithNotes(IReadOnlyList<TimelineEntry> timeline, IReadOnlyList<AppNote> notes)
    {
        if (notes.Count == 0)
            return timeline;
        var merged = timeline.Select((e, i) => (e, i, src: 0))
            .Concat(notes.Select((n, i) => (e: new TimelineEntry("note", null, "", n.At, n.Body), i, src: 1)))
            .OrderBy(x => x.e.At.ToUniversalTime()).ThenBy(x => x.src).ThenBy(x => x.i)
            .Select(x => x.e)
            .ToList();
        return merged;
    }
}
