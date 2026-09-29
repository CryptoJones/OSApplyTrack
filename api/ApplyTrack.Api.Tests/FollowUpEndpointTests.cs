// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using System.Text.Json;
using ApplyTrack.Api.Auth;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Endpoints;
using ApplyTrack.Api.Llm;
using ApplyTrack.Api.Materials;
using ApplyTrack.Api.Notifications;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ApplyTrack.Api.Tests;

/// <summary>
/// Following up from the Due view (#394): the model drafts from what the account holds,
/// nothing is sent until the person sends it — and then from their own mailbox, never the
/// instance's — and a follow-up sent or made elsewhere is logged and moves the date. Off
/// with drafting: no model call.
/// </summary>
[Collection(PostgresCollection.Name)]
public class FollowUpEndpointTests(PostgresFixture pg) : IAsyncLifetime
{
    private readonly List<WebApplicationFactory<Program>> _factories = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var f in _factories) await f.DisposeAsync();
    }

    private const string Reply = "SUBJECT: Re: Platform role\n\nHello Dana,\n\nI applied on March 1 and wanted to ask where things stand with the Platform role.";

    private sealed class FakeMail : IFollowUpMail
    {
        public List<(MailboxTarget Smtp, string To, string Subject, string Body)> Sent { get; } = [];
        public IReadOnlyList<MailSnippet> Thread { get; set; } = [];
        public string? ThreadAddress { get; private set; }

        public Task<IReadOnlyList<MailSnippet>> ThreadAsync(MailboxTarget imap, string address, CancellationToken ct)
        {
            ThreadAddress = address;
            return Task.FromResult(Thread);
        }

        public Task SendAsync(MailboxTarget smtp, string to, string subject, string body, CancellationToken ct)
        {
            Sent.Add((smtp, to, subject, body));
            return Task.CompletedTask;
        }
    }

    private async Task<(HttpClient Client, StubLlmClient Llm, FakeMail Mail)> ClientAsync()
    {
        var llm = new StubLlmClient((system, _, _) =>
            system.Contains("follow-up email", StringComparison.Ordinal) ? Reply : StubLlmClient.DefaultBody);
        var mail = new FakeMail();
        var f = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:Postgres", pg.ConnectionString);
            b.UseSetting("Llm:BaseUrl", "http://stub/v1");
            b.UseSetting("Llm:Model", "stub-model");
            b.UseSetting("Secrets:Key", TestAuth.MasterKey);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<ILlmClient>();
                s.AddSingleton<ILlmClient>(llm);
                s.RemoveAll<IFollowUpMail>();
                s.AddSingleton<IFollowUpMail>(mail);
            });
        });
        _factories.Add(f);
        var (_, sid) = await TestAuth.SeedSessionAsync(pg.ConnectionString);
        var client = f.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{AuthCookie.Name}={sid}");
        await client.PutAsync("/api/resume", Json("""{"full_name":"Ada Byte","summary":"Ships .NET."}"""));
        return (client, llm, mail);
    }

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");
    private static async Task<JsonElement> ReadJson(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static async Task<string> AppliedAsync(HttpClient client, string contactEmail = "dana@aurora.example")
    {
        var res = await client.PostAsync("/api/apps", Json($$"""
            {"company":"Aurora Systems","role":"Platform Engineer","status":"applied","applied":"2026-03-01",
             "followup":"2026-03-08","contact":"Dana Reyes","contact_email":"{{contactEmail}}","notes":"Referred by Sam."}
            """));
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        return (await ReadJson(res)).GetProperty("filename").GetString()!;
    }

    private static async Task<string> FollowupOfAsync(HttpClient client, string name) =>
        (await ReadJson(await client.GetAsync($"/api/apps/{name}"))).GetProperty("fields").GetProperty("followup").GetString()!;

    private static async Task<List<string>> NotesAsync(HttpClient client, string name) =>
        (await ReadJson(await client.GetAsync($"/api/apps/{name}/notes"))).EnumerateArray()
            .Select(n => n.GetProperty("body").GetString()!).ToList();

    private static Task SmtpAsync(HttpClient client, string username = "ada@example.com") =>
        client.PutAsync("/api/notifications", Json($$"""
            {"mailbox_host":"imap.example.com","mailbox_port":993,"mailbox_username":"{{username}}",
             "mailbox_password":"app-password","mailbox_smtp_host":"smtp.example.com","mailbox_smtp_port":587}
            """));

    [Fact]
    public async Task Draft_writes_from_the_application_letter_notes_and_mail_thread()
    {
        var (client, llm, mail) = await ClientAsync();
        var name = await AppliedAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"/api/apps/{name}/draft", null)).StatusCode);
        await client.PostAsync($"/api/apps/{name}/notes", Json("""{"body":"Phone screen with Dana went well."}"""));
        await SmtpAsync(client);
        mail.Thread = [new MailSnippet(DateTimeOffset.Parse("2026-03-04T10:00:00Z"), "Dana Reyes <dana@aurora.example>",
            "Platform role", "Thanks Ada, we will be in touch next week.")];

        var res = await client.PostAsync($"/api/apps/{name}/followup/draft", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var body = await ReadJson(res);
        Assert.Equal("Re: Platform role", body.GetProperty("subject").GetString());
        Assert.StartsWith("Hello Dana,", body.GetProperty("body").GetString());
        Assert.EndsWith("Ada Byte", body.GetProperty("body").GetString());
        Assert.Equal("dana@aurora.example", body.GetProperty("to").GetString());
        Assert.True(body.GetProperty("can_send").GetBoolean());
        Assert.Equal(1, body.GetProperty("thread_messages").GetInt32());

        // What the model was given: the application, the letter, the notes, the thread.
        Assert.Equal("dana@aurora.example", mail.ThreadAddress);
        var prompt = llm.LastUserPrompt!;
        Assert.Contains("ROLE: Platform Engineer", prompt);
        Assert.Contains("APPLIED ON: 2026-03-01", prompt);
        Assert.Contains("CONTACT: Dana Reyes", prompt);
        Assert.Contains("generated cover letter produced for testing", prompt);
        Assert.Contains("Phone screen with Dana went well.", prompt);
        Assert.Contains("we will be in touch next week", prompt);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Draft_is_refused_without_a_model_call_when_drafting_is_off()
    {
        var (client, llm, _) = await ClientAsync();
        var name = await AppliedAsync(client);
        await client.PutAsync("/api/llm-settings", Json("""{"cover_letters_enabled":false}"""));

        var res = await client.PostAsync($"/api/apps/{name}/followup/draft", null);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains("drafting is turned off", (await ReadJson(res)).GetProperty("detail").GetString());
        Assert.Equal(0, llm.Calls);
    }

    [Fact]
    public async Task Send_needs_the_tenants_own_smtp_then_mails_logs_and_clears_the_date()
    {
        var (client, _, mail) = await ClientAsync();
        var name = await AppliedAsync(client);
        var send = Json("""{"to":"dana@aurora.example","subject":"Re: Platform role","body":"Hello Dana,\n\nChecking in.\n\nAda","next_followup":""}""");

        // No outgoing mail of the tenant's own: nothing is sent, and the instance relay is not used.
        var refused = await client.PostAsync($"/api/apps/{name}/followup/send", send);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("outgoing mail (SMTP)", (await ReadJson(refused)).GetProperty("detail").GetString());
        Assert.Empty(mail.Sent);
        Assert.Equal("2026-03-08", await FollowupOfAsync(client, name));

        await SmtpAsync(client);
        var res = await client.PostAsync($"/api/apps/{name}/followup/send",
            Json("""{"to":"dana@aurora.example","subject":"Re: Platform role","body":"Hello Dana,\n\nChecking in.\n\nAda","next_followup":""}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var (smtp, to, subject, text) = Assert.Single(mail.Sent);
        Assert.Equal(("smtp.example.com", 587, "ada@example.com", "app-password"), (smtp.Host, smtp.Port, smtp.Username, smtp.Password));
        Assert.Equal(("dana@aurora.example", "Re: Platform role"), (to, subject));
        Assert.Equal("Hello Dana,\n\nChecking in.\n\nAda", text);

        Assert.Equal("", await FollowupOfAsync(client, name));
        var note = Assert.Single(await NotesAsync(client, name));
        Assert.StartsWith("Follow-up emailed to dana@aurora.example — “Re: Platform role”", note);
        Assert.Contains("Checking in.", note);
    }

    [Theory]
    [InlineData("""{"to":"dana@aurora.example, eve@evil.example","subject":"Hi","body":"Hello"}""", "one email address")]
    [InlineData("""{"to":"","subject":"Hi","body":"Hello"}""", "no address to send to")]
    [InlineData("""{"to":"dana@aurora.example","subject":"Hi\nBcc: eve@evil.example","body":"Hello"}""", "subject is one line")]
    [InlineData("""{"to":"dana@aurora.example","subject":"Hi","body":""}""", "the email is empty")]
    [InlineData("""{"to":"dana@aurora.example","subject":"Hi","body":"Hello","next_followup":"next week"}""", "a date like")]
    public async Task Send_refuses_what_is_not_one_plain_message(string payload, string detail)
    {
        var (client, _, mail) = await ClientAsync();
        var name = await AppliedAsync(client);
        await SmtpAsync(client);
        var res = await client.PostAsync($"/api/apps/{name}/followup/send", Json(payload));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Contains(detail, (await ReadJson(res)).GetProperty("detail").GetString());
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Send_refuses_a_mailbox_username_that_is_not_an_address()
    {
        var (client, _, mail) = await ClientAsync();
        var name = await AppliedAsync(client);
        await SmtpAsync(client, username: "ada");
        var res = await client.PostAsync($"/api/apps/{name}/followup/send",
            Json("""{"to":"dana@aurora.example","subject":"Hi","body":"Hello"}"""));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public async Task Done_logs_a_follow_up_made_elsewhere_and_moves_the_date()
    {
        var (client, _, mail) = await ClientAsync();
        var name = await AppliedAsync(client, contactEmail: "");
        var res = await client.PostAsync($"/api/apps/{name}/followup/done",
            Json("""{"channel":"LinkedIn","body":"Messaged Dana on LinkedIn.","next_followup":"2026-10-05"}"""));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("2026-10-05", await FollowupOfAsync(client, name));
        Assert.Equal(["Followed up via LinkedIn\n\nMessaged Dana on LinkedIn."], await NotesAsync(client, name));
        Assert.Empty(mail.Sent);
    }

    [Fact]
    public void Parse_takes_the_subject_line_and_falls_back_to_a_plain_one()
    {
        var app = new AppFields { Role = "Platform Engineer" };
        Assert.Equal(new FollowUpDraft("Re: Platform role", "Hello Dana,\n\nChecking in."),
            FollowUpDrafter.Parse("SUBJECT: Re: Platform role\n\nHello Dana,\n\nChecking in.", app));
        Assert.Equal(new FollowUpDraft("Following up: Platform Engineer application", "Hello,\n\nChecking in."),
            FollowUpDrafter.Parse("Hello,\n\nChecking in.", app));
        Assert.Equal("dana@aurora.example", FollowUpEndpoints.Address(" dana@aurora.example "));
        Assert.Equal("", FollowUpEndpoints.NextFollowup(null));
    }
}
