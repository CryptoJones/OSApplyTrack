// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net.Http.Json;
using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// The "moo": <c>POST https://api.telegram.org/bot{token}/sendMessage</c>. The host
/// is pinned in code by the named client's BaseAddress and tenant input only lands
/// in the path (a validated token) and the JSON body, so there is no
/// tenant-controlled URL and no SSRF surface — unlike the LLM and scrape fetchers.
/// The URI does carry the token, which is why the named client is registered with
/// its request logging removed and why no message or log line here includes the URI.
/// </summary>
public sealed class TelegramNotifier : INotifier
{
    public const string ClientName = "telegram";
    private const int MaxResponseBytes = 64 * 1024;
    private const int MaxDescriptionChars = 200;

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<TelegramNotifier> _log;

    public TelegramNotifier(IHttpClientFactory factory, ILogger<TelegramNotifier> log)
    {
        _factory = factory;
        _log = log;
    }

    public async Task SendAsync(string botToken, string chatId, string text, CancellationToken ct = default)
    {
        var client = _factory.CreateClient(ClientName);
        HttpResponseMessage res;
        try
        {
            // No parse_mode: company and role names need no escaping, and a bare URL
            // still auto-links in Telegram.
            // Root-relative on purpose: a relative "bot123:abc/…" would parse "bot123" as a
            // URI scheme and never reach api.telegram.org.
            res = await client.PostAsJsonAsync($"/bot{botToken}/sendMessage",
                new { chat_id = chatId, text, disable_web_page_preview = true }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
        {
            _log.LogWarning("Telegram unreachable: {Type}", ex.GetType().Name);
            throw new NotificationFailedException("could not reach Telegram");
        }

        using (res)
        {
            if (res.Content.Headers.ContentLength > MaxResponseBytes)
                throw new NotificationFailedException("Telegram returned a response that is too large");
            var body = await res.Content.ReadAsStringAsync(ct);
            if (body.Length > MaxResponseBytes)
                throw new NotificationFailedException("Telegram returned a response that is too large");

            var ok = false;
            var description = "";
            try
            {
                using var doc = JsonDocument.Parse(body);
                ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    description = d.GetString() ?? "";
            }
            catch (JsonException)
            {
                // Not JSON — treated as a refusal below.
            }
            if (res.IsSuccessStatusCode && ok)
                return;

            if (description.Length > MaxDescriptionChars)
                description = description[..MaxDescriptionChars];
            _log.LogWarning("Telegram refused the message: HTTP {Status}", (int)res.StatusCode);
            throw new NotificationFailedException(description.Length > 0
                ? $"Telegram refused the message ({description})"
                : $"Telegram refused the message (HTTP {(int)res.StatusCode})");
        }
    }
}
