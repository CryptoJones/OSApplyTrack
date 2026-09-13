// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net.Http.Json;
using System.Text.Json;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Notifications;

/// <summary>
/// The "moo": <c>POST https://api.telegram.org/bot{token}/sendMessage</c> — and the reply,
/// <c>GET …/getUpdates</c>, for the security code a person sends back. The host
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
        // No parse_mode: company and role names need no escaping, and a bare URL
        // still auto-links in Telegram.
        // Root-relative on purpose: a relative "bot123:abc/…" would parse "bot123" as a
        // URI scheme and never reach api.telegram.org.
        using var doc = await CallAsync(
            c => c.PostAsJsonAsync($"/bot{botToken}/sendMessage",
                new { chat_id = chatId, text, disable_web_page_preview = true }, ct),
            "the message", ct);
    }

    /// <summary>
    /// <c>getUpdates</c>, the pull side of the Bot API: no webhook, no public URL, the same
    /// pinned host. A short poll (<c>timeout=0</c>) so a parked run's 5-second loop stays a
    /// loop; <c>allowed_updates</c> keeps everything but plain messages off the wire.
    /// </summary>
    public async Task<IReadOnlyList<TelegramReply>> ReadRepliesAsync(string botToken, long? offset, CancellationToken ct = default)
    {
        var query = "timeout=0&limit=50&allowed_updates=%5B%22message%22%5D" + (offset is { } o ? $"&offset={o}" : "");
        using var doc = await CallAsync(c => c.GetAsync($"/bot{botToken}/getUpdates?{query}", ct), "the poll", ct);
        var replies = new List<TelegramReply>();
        if (!doc.RootElement.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Array)
            return replies;
        foreach (var u in result.EnumerateArray())
        {
            if (!u.TryGetProperty("update_id", out var idEl) || !idEl.TryGetInt64(out var id)) continue;
            if (!u.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            var chat = m.TryGetProperty("chat", out var c) && c.TryGetProperty("id", out var cid) ? cid.ToString() : "";
            var date = m.TryGetProperty("date", out var d) && d.TryGetInt64(out var unix)
                ? DateTimeOffset.FromUnixTimeSeconds(unix) : DateTimeOffset.MinValue;
            var text = m.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            replies.Add(new TelegramReply(id, chat, date, text));
        }
        return replies;
    }

    /// <summary>One Bot API call: transport failure and refusal both become a
    /// <see cref="NotificationFailedException"/> that never carries the token; a success
    /// hands back the parsed envelope.</summary>
    private async Task<JsonDocument> CallAsync(Func<HttpClient, Task<HttpResponseMessage>> send, string what, CancellationToken ct)
    {
        var client = _factory.CreateClient(ClientName);
        HttpResponseMessage res;
        try
        {
            res = await send(client);
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

            JsonDocument? doc = null;
            var ok = false;
            var description = "";
            try
            {
                doc = JsonDocument.Parse(body);
                ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True;
                if (doc.RootElement.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String)
                    description = d.GetString() ?? "";
            }
            catch (JsonException)
            {
                // Not JSON — treated as a refusal below.
            }
            if (res.IsSuccessStatusCode && ok && doc is not null)
                return doc;
            doc?.Dispose();

            if (description.Length > MaxDescriptionChars)
                description = description[..MaxDescriptionChars];
            _log.LogWarning("Telegram refused {What}: HTTP {Status}", what, (int)res.StatusCode);
            throw new NotificationFailedException(description.Length > 0
                ? $"Telegram refused {what} ({description})"
                : $"Telegram refused {what} (HTTP {(int)res.StatusCode})");
        }
    }
}
