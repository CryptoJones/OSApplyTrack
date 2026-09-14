// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using ApplyTrack.Api.Notifications;

namespace ApplyTrack.Api.Tests;

/// <summary>An <see cref="IHttpClientFactory"/> that hands every named client the same handler,
/// with the BaseAddress the app would have configured for that name.</summary>
internal sealed class StubHttpClientFactory(HttpMessageHandler handler, Uri? baseAddress = null) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = baseAddress };
}

/// <summary>Records every request (and its body) and answers with a scripted response.</summary>
internal sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
{
    public List<(HttpRequestMessage Request, string Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests) Requests.Add((request, body));
        return respond(request);
    }

    public static CapturingHandler Always(HttpStatusCode status, string body, string mediaType = "application/json") =>
        new(_ => new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, mediaType) });
}

/// <summary>Test double for <see cref="INotifier"/>: captures instead of sending, or fails on cue.</summary>
internal sealed class CapturingNotifier(Exception? fail = null) : INotifier
{
    private readonly List<(string BotToken, string ChatId, string Text)> _sent = [];

    public IReadOnlyList<(string BotToken, string ChatId, string Text)> Sent => _sent;

    public Task SendAsync(string botToken, string chatId, string text, CancellationToken ct = default)
    {
        if (fail is not null) throw fail;
        lock (_sent) _sent.Add((botToken, chatId, text));
        return Task.CompletedTask;
    }

    /// <summary>What the bot's inbox holds; a test fills it.</summary>
    public List<TelegramReply> Replies { get; } = [];

    /// <summary>Every offset a poll asked with, in order.</summary>
    public List<long?> Offsets { get; } = [];

    public Task<IReadOnlyList<TelegramReply>> ReadRepliesAsync(string botToken, long? offset, CancellationToken ct = default)
    {
        if (fail is not null) throw fail;
        lock (Offsets) Offsets.Add(offset);
        IReadOnlyList<TelegramReply> page = Replies.Where(r => offset is null || r.UpdateId >= offset).ToList();
        return Task.FromResult(page);
    }
}

/// <summary>A stub model that answers the fit judge, the answer drafter and the letter
/// drafter each in the shape they expect, keyed on the system prompt.</summary>
internal static class Responders
{
    public const string ProceedJson =
        "{\"decision\":\"proceed\",\"confidence\":90,\"rationale\":\"Fits.\",\"concerns\":[]}";

    public static Func<string, string, ApplyTrack.Api.Llm.EffectiveLlmConfig, string> Agent(
        string verdictJson = ProceedJson, string answersJson = "{\"answers\":[]}") =>
        (system, _, _) =>
            system.Contains("cover-letter writer") ? StubLlmClient.DefaultBody
            : system.Contains("screening questions") ? answersJson
            : verdictJson;
}

/// <summary>A browser that never opens: the worker's <see cref="ApplyTrack.Api.Agent.Browser.IBrowserSubmitter"/>
/// seam scripted per run, so the parked-on-code path and the queue's promotion rules run
/// against the test Postgres without Playwright (#192).</summary>
internal sealed class FakeSubmitter(
    Func<string, ApplyTrack.Api.Data.AgentPacket, bool, Func<ApplyTrack.Api.Agent.Browser.CodeRequest, CancellationToken, Task<string?>>?, CancellationToken,
        Task<ApplyTrack.Api.Agent.Browser.SubmitOutcome>> run) : ApplyTrack.Api.Agent.Browser.IBrowserSubmitter
{
    public List<(string Link, bool DryRun, Dictionary<string, string> Answers)> Runs { get; } = [];

    public Task<ApplyTrack.Api.Agent.Browser.SubmitOutcome> RunAsync(
        string link, ApplyTrack.Api.Data.AgentPacket packet, (byte[] Bytes, string Name)? resumePdf, bool dryRun,
        CancellationToken ct = default, string resumeText = "", string coverLetter = "",
        Func<ApplyTrack.Api.Agent.Browser.CodeRequest, CancellationToken, Task<string?>>? awaitSecurityCode = null,
        IReadOnlyList<ApplyTrack.Api.Data.BoardAccount>? accounts = null)
    {
        lock (Runs) Runs.Add((link, dryRun, new Dictionary<string, string>(packet.Answers)));
        return run(link, packet, dryRun, awaitSecurityCode, ct);
    }

    /// <summary>A dry run that filled everything: the promotion bar.</summary>
    public static ApplyTrack.Api.Agent.Browser.SubmitOutcome Clean(string link) =>
        new(true, false, link, "", null, [], ["std:first_name", "std:email"], "");

    /// <summary>A real run the board confirmed.</summary>
    public static ApplyTrack.Api.Agent.Browser.SubmitOutcome Submitted(string link) =>
        new(true, true, link, "Thank you for applying!", null, [], ["std:first_name", "std:email"], "");
}

/// <summary>The MyGreenhouse sign-in without a browser (#221): hands back a session, or throws.</summary>
internal sealed class FakePortalRenewer(Func<string, ApplyTrack.Api.Agent.Browser.PortalSession> renew) : ApplyTrack.Api.Agent.Browser.IPortalSessionRenewer
{
    public List<string> Emails { get; } = [];

    public Task<ApplyTrack.Api.Agent.Browser.PortalSession> RenewAsync(string email, Func<DateTimeOffset, CancellationToken, Task<string?>> readCode, CancellationToken ct)
    {
        Emails.Add(email);
        return Task.FromResult(renew(email));
    }
}

/// <summary>A mailbox that answers (or fails) on cue, standing in for IMAP.</summary>
internal sealed class FakeCodeSource(Func<string?> find) : ISecurityCodeSource
{
    public int Calls { get; private set; }

    public Task<string?> FindCodeAsync(ApplyTrack.Api.Data.MailboxTarget target, string recipient, string company, DateTimeOffset since, CancellationToken ct, string boardHost = "")
    {
        Calls++;
        return Task.FromResult(find());
    }

    public Task<int> TestAsync(ApplyTrack.Api.Data.MailboxTarget target, CancellationToken ct) => Task.FromResult(0);
}
