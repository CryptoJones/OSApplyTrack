// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.Json;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;

namespace ApplyTrack.Api.Agent.Greenhouse;

/// <summary>A Greenhouse job as the public Job Board API describes it: the title, the
/// description HTML, and the typed application form.</summary>
public sealed record GreenhouseJob(string Title, string ContentHtml, List<PacketQuestion> Questions);

/// <summary>
/// Reads a job's application form from Greenhouse's public Job Board API —
/// <c>GET boards-api.greenhouse.io/v1/boards/{board}/jobs/{id}?questions=true</c>
/// returns <c>questions[]</c> with <c>required</c>, <c>label</c> and <c>fields[]</c>
/// (name, type, values), and Greenhouse documents that "Job Board data is publicly
/// available, so authentication is not required for any GET endpoints". So a
/// Greenhouse packet gets a real field map with no browser and no employer key. The
/// host is pinned in code and only a validated board token and numeric job id reach
/// the path, so no SSRF surface.
/// </summary>
public sealed partial class GreenhouseBoard
{
    public const string ClientName = "greenhouse";
    private const int MaxResponseBytes = 2 * 1024 * 1024;

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._-]{0,99}$", RegexOptions.IgnoreCase)]
    private static partial Regex BoardToken();

    [GeneratedRegex(@"^\d{1,20}$")]
    private static partial Regex JobId();

    // Greenhouse's standard field names — everything else is a screening question.
    private static readonly HashSet<string> StandardFields =
        ["first_name", "last_name", "email", "phone", "resume", "cover_letter", "location", "country", "linkedin_profile", "website"];

    private readonly IHttpClientFactory _factory;
    private readonly ILogger<GreenhouseBoard> _log;

    public GreenhouseBoard(IHttpClientFactory factory, ILogger<GreenhouseBoard> log)
    {
        _factory = factory;
        _log = log;
    }

    /// <summary>The job, or null when Greenhouse doesn't know it (closed, private, wrong
    /// board) or can't be reached — the caller falls back to the standard form.</summary>
    public async Task<GreenhouseJob?> GetJobAsync(string board, string jobId, CancellationToken ct = default)
    {
        if (!BoardToken().IsMatch(board) || !JobId().IsMatch(jobId))
            return null;
        var client = _factory.CreateClient(ClientName);
        try
        {
            using var res = await client.GetAsync($"v1/boards/{board}/jobs/{jobId}?questions=true", ct);
            if (!res.IsSuccessStatusCode)
            {
                _log.LogInformation("greenhouse {Board}/{Job}: HTTP {Status}", board, jobId, (int)res.StatusCode);
                return null;
            }
            if (res.Content.Headers.ContentLength > MaxResponseBytes)
                return null;
            var json = await res.Content.ReadAsStringAsync(ct);
            if (json.Length > MaxResponseBytes)
                return null;
            return Parse(json);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.LogInformation("greenhouse {Board}/{Job}: {Reason}", board, jobId, ex.Message);
            return null;
        }
    }

    /// <summary>Parse a job document. Public for tests.</summary>
    public static GreenhouseJob Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var questions = new List<PacketQuestion>();
        foreach (var name in new[] { "questions", "location_questions" })
            if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var q in arr.EnumerateArray())
                    AddQuestion(questions, q, eeo: false);
        if (root.TryGetProperty("compliance", out var compliance) && compliance.ValueKind == JsonValueKind.Array)
            foreach (var block in compliance.EnumerateArray())
                if (block.TryGetProperty("questions", out var cq) && cq.ValueKind == JsonValueKind.Array)
                    foreach (var q in cq.EnumerateArray())
                        AddQuestion(questions, q, eeo: true);
        // The rendered form asks for more than the API admits: a job with location questions
        // renders a required Country picker that appears nowhere in the document. Carry it in
        // the packet so there is an answer to give it at submit time — optional here, because
        // whether THIS form renders one is only known in the browser, and the submitter's
        // check of the rendered form is what refuses the click if it does and stays empty.
        var at = questions.FindIndex(q => q.Id == "location");
        if (at >= 0 && !questions.Any(q => q.Id == "country"))
            questions.Insert(at, new PacketQuestion("country", "Country", false, PacketQuestion.Select, [], PacketQuestion.Standard)
            {
                Help = "Country of residence; the form's own list is matched at submit time",
            });
        return new GreenhouseJob(Str(root, "title"), Str(root, "content"), questions);
    }

    private static void AddQuestion(List<PacketQuestion> into, JsonElement q, bool eeo)
    {
        var label = Str(q, "label");
        var required = q.TryGetProperty("required", out var r) && r.ValueKind == JsonValueKind.True;
        if (!q.TryGetProperty("fields", out var fields) || fields.ValueKind != JsonValueKind.Array)
            return;
        // A question may carry several fields (résumé = file + pasted text); the first
        // is the primary one and the one the form actually wants.
        JsonElement? primary = null;
        foreach (var f in fields.EnumerateArray())
        {
            var t = Str(f, "type");
            if (t == "input_hidden") continue;
            primary ??= f;
            // Prefer the file field for the résumé/letter pair over the pasted-text twin.
            if (t == "input_file") { primary = f; break; }
        }
        if (primary is null) return;
        var field = primary.Value;
        var name = Str(field, "name");
        var type = Str(field, "type") switch
        {
            "input_text" => PacketQuestion.Text,
            "textarea" => PacketQuestion.Textarea,
            "input_file" => PacketQuestion.File,
            "multi_value_single_select" => PacketQuestion.Select,
            "multi_value_multi_select" => PacketQuestion.MultiSelect,
            _ => PacketQuestion.Text,
        };
        var options = new List<string>();
        if (field.TryGetProperty("values", out var values) && values.ValueKind == JsonValueKind.Array)
            foreach (var v in values.EnumerateArray())
            {
                var l = Str(v, "label");
                if (l.Length > 0) options.Add(l);
            }
        var kind = eeo ? PacketQuestion.Eeo
            : StandardFields.Contains(name) ? PacketQuestion.Standard
            : PacketQuestion.Custom;
        if (name.Length == 0) name = "q:" + into.Count;
        // `description` is where Greenhouse puts the qualifiers that decide what a correct
        // answer is — units, currency, period, "only if X". Ignoring it is how an annual
        // USD figure ended up in a field asking for gross monthly EUR. It is HTML.
        into.Add(new PacketQuestion(name, label, required && !eeo, type, options, kind)
        {
            Help = StripHtml(Str(q, "description")),
        });
    }

    /// <summary>Flatten the description HTML to the sentence a human would read.</summary>
    private static string StripHtml(string html)
    {
        if (html.Length == 0) return "";
        var text = System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Str(JsonElement obj, string key) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? (v.GetString() ?? "").Trim()
            : "";
}
