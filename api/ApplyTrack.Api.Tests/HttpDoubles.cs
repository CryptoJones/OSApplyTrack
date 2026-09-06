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
