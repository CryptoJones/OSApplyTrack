// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.RegularExpressions;
using ApplyTrack.Api.Data;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Exceptions;

namespace ApplyTrack.Api.Materials;

/// <summary>
/// Best-effort importer for text-based resume PDFs. The PDF remains user-supplied
/// source material: we extract selectable text, infer the obvious structured fields,
/// and keep the full extracted text in Summary so the drafter still has the facts
/// when the PDF layout is too irregular to split cleanly.
/// </summary>
public static partial class ResumePdfImporter
{
    public const long MaxPdfBytes = 5L * 1024 * 1024;

    private const int MaxSummaryChars = 20_000;

    private static readonly string[] SummaryHeaders =
        ["summary", "professional summary", "profile", "career profile", "objective", "about"];

    private static readonly string[] SkillHeaders =
        ["skills", "technical skills", "core skills", "core competencies", "technologies"];

    private static readonly string[] CertificationHeaders =
        ["certifications", "certificates", "licenses", "licensure"];

    private static readonly string[] ExperienceHeaders =
        ["experience", "professional experience", "work experience", "employment", "employment history"];

    private static readonly HashSet<string> KnownHeaders = new(
        SummaryHeaders.Concat(SkillHeaders).Concat(CertificationHeaders).Concat(ExperienceHeaders)
            .Concat(["education", "projects", "selected projects", "links", "contact", "awards", "publications"]),
        StringComparer.OrdinalIgnoreCase);

    public static Resume FromPdf(byte[] bytes)
    {
        if (!LooksLikePdf(bytes))
            throw new AppValidationException("resume upload must be a PDF file");

        string text;
        try
        {
            using var document = PdfDocument.Open(bytes);
            var sb = new StringBuilder();
            foreach (var page in document.GetPages())
            {
                var pageText = NormalizeText(ContentOrderTextExtractor.GetText(page));
                if (pageText.Length > 0)
                    sb.AppendLine(pageText).AppendLine();
            }
            text = NormalizeText(sb.ToString());
        }
        catch (PdfDocumentEncryptedException)
        {
            throw new AppValidationException("encrypted PDF resumes are not supported yet");
        }
        catch (PdfDocumentFormatException)
        {
            throw new AppValidationException("could not read that PDF resume");
        }

        if (text.Length == 0)
            throw new AppValidationException(
                "could not find selectable text in that PDF resume; scanned image PDFs are not supported yet");

        return FromText(text);
    }

    internal static Resume FromText(string text)
    {
        var lines = Lines(text).ToList();
        var (name, nameIndex) = GuessName(lines);
        var headline = GuessHeadline(lines, nameIndex);
        var summary = Section(lines, SummaryHeaders);
        var experience = Section(lines, ExperienceHeaders);

        return new Resume
        {
            FullName = name,
            Headline = headline,
            Location = GuessLocation(lines),
            Summary = BuildSummary(text, summary),
            Experience = BuildExperience(experience),
            Skills = CleanList(Section(lines, SkillHeaders), splitPhrases: true),
            Certifications = CleanList(Section(lines, CertificationHeaders), splitPhrases: false),
            Links = ExtractLinks(text),
        };
    }

    private static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length >= 5
        && bytes[0] == (byte)'%'
        && bytes[1] == (byte)'P'
        && bytes[2] == (byte)'D'
        && bytes[3] == (byte)'F'
        && bytes[4] == (byte)'-';

    private static string BuildSummary(string fullText, IReadOnlyList<string> summaryLines)
    {
        var text = summaryLines.Count > 0
            ? string.Join("\n", summaryLines) + "\n\nExtracted resume text:\n" + fullText
            : fullText;
        return text.Length <= MaxSummaryChars
            ? text
            : text[..MaxSummaryChars].TrimEnd() + "\n[truncated]";
    }

    private static List<ResumeExperience> BuildExperience(IReadOnlyList<string> experienceLines)
    {
        var highlights = CleanList(experienceLines, splitPhrases: false)
            .Where(s => s.Length <= 240)
            .Take(40)
            .ToList();
        return highlights.Count == 0
            ? []
            : [new ResumeExperience("", "Experience from uploaded resume", "", highlights)];
    }

    private static (string Name, int Index) GuessName(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < Math.Min(lines.Count, 12); i++)
        {
            var line = CleanLine(lines[i]);
            if (line.Length is < 3 or > 80 || LooksLikeContact(line) || IsKnownHeader(line))
                continue;

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length is >= 2 and <= 5 && words.All(ContainsLetter))
                return (line, i);
        }
        return ("", -1);
    }

    private static string GuessHeadline(IReadOnlyList<string> lines, int nameIndex)
    {
        var start = nameIndex >= 0 ? nameIndex + 1 : 0;
        for (var i = start; i < Math.Min(lines.Count, start + 5); i++)
        {
            var line = CleanLine(lines[i]);
            if (line.Length is < 3 or > 120 || LooksLikeContact(line) || IsKnownHeader(line))
                continue;
            return line;
        }
        return "";
    }

    private static string GuessLocation(IReadOnlyList<string> lines)
    {
        foreach (var line in lines.Take(10).Select(CleanLine))
        {
            var location = LocationRegex().Match(line);
            if (location.Success)
                return location.Value;
        }
        return "";
    }

    private static IReadOnlyList<string> Section(IReadOnlyList<string> lines, IReadOnlyList<string> headers)
    {
        var found = false;
        var values = new List<string>();
        foreach (var raw in lines)
        {
            var line = CleanLine(raw);
            if (line.Length == 0)
                continue;

            if (TryHeader(line, headers, out var inline))
            {
                found = true;
                if (inline.Length > 0)
                    values.Add(inline);
                continue;
            }

            if (found && IsKnownHeader(line))
                break;
            if (found)
                values.Add(line);
        }
        return values;
    }

    private static bool TryHeader(string line, IReadOnlyList<string> headers, out string inline)
    {
        inline = "";
        var normalized = NormalizeHeader(line);
        foreach (var header in headers)
        {
            if (normalized == header)
                return true;
            if (normalized.StartsWith(header + ": ", StringComparison.OrdinalIgnoreCase))
            {
                inline = line[(line.IndexOf(':') + 1)..].Trim();
                return true;
            }
        }
        return false;
    }

    private static bool IsKnownHeader(string line)
    {
        var normalized = NormalizeHeader(line);
        return KnownHeaders.Contains(normalized)
            || (line.EndsWith(':') && line.Length <= 40 && KnownHeaders.Contains(normalized.TrimEnd(':')));
    }

    private static string NormalizeHeader(string line) =>
        SpaceRegex().Replace(CleanBullet(line).Trim().TrimEnd(':').ToLowerInvariant(), " ");

    private static List<string> CleanList(IEnumerable<string> lines, bool splitPhrases)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var cleaned = CleanBullet(line);
            var parts = splitPhrases
                ? ListSplitRegex().Split(cleaned)
                : [cleaned];

            foreach (var part in parts.Select(CleanLine))
            {
                if (part.Length == 0 || part.Length > 120 || IsKnownHeader(part) || LooksLikeContact(part))
                    continue;
                if (seen.Add(part))
                    result.Add(part);
            }
        }
        return result;
    }

    private static List<ResumeLink> ExtractLinks(string text)
    {
        var links = new List<ResumeLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in UrlRegex().Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ')', ']');
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;
            if (!seen.Add(url))
                continue;
            links.Add(new ResumeLink(LinkLabel(url), url));
        }
        return links;
    }

    private static string LinkLabel(string url)
    {
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : url.ToLowerInvariant();
        if (host.Contains("github")) return "GitHub";
        if (host.Contains("linkedin")) return "LinkedIn";
        if (host.Contains("gitlab")) return "GitLab";
        return "Portfolio";
    }

    private static IEnumerable<string> Lines(string text) =>
        NormalizeText(text)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CleanLine)
            .Where(s => s.Length > 0);

    private static string NormalizeText(string text) =>
        SpaceRegex().Replace(text.Replace("\r\n", "\n").Replace('\r', '\n'), " ").Trim();

    private static string CleanLine(string line) =>
        SpaceRegex().Replace(CleanBullet(line), " ").Trim();

    private static string CleanBullet(string line) =>
        BulletRegex().Replace(line.Trim(), "");

    private static bool LooksLikeContact(string line) =>
        line.Contains('@')
        || UrlRegex().IsMatch(line)
        || PhoneRegex().IsMatch(line);

    private static bool ContainsLetter(string value) => value.Any(char.IsLetter);

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"^[\-*•·‣▪▫◦]+\s*")]
    private static partial Regex BulletRegex();

    [GeneratedRegex(@"(?:https?://|www\.|linkedin\.com/|github\.com/|gitlab\.com/)[^\s<>""]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\+?\d[\d\s().-]{7,}\d")]
    private static partial Regex PhoneRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z .'-]+,\s*(?:[A-Z]{2}|[A-Za-z][A-Za-z .'-]+)")]
    private static partial Regex LocationRegex();

    [GeneratedRegex(@"[,;|]| {2,}")]
    private static partial Regex ListSplitRegex();
}
