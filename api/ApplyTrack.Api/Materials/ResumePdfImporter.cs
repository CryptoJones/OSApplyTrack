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
/// source material: we extract selectable text and store it as the résumé brief
/// rather than guessing at lossy structured fields.
/// </summary>
public static partial class ResumePdfImporter
{
    public const long MaxPdfBytes = 5L * 1024 * 1024;

    private const int MaxResumeTextChars = 100_000;

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
        var summary = BuildResumeText(text);
        return new Resume
        {
            Summary = summary,
            Education = EducationFrom(summary),
        };
    }

    /// <summary>
    /// The schools under a résumé's EDUCATION heading, for forms that ask for them row by row
    /// (UKG, #277). A school is the line before a degree line ("Bachelor of Science, Computer
    /// Science"); a trailing date range on the school's line is its dates. Lines that name no
    /// school ("Graduate &amp; Professional Certificates") are left for the person. Public for tests.
    /// </summary>
    public static List<ResumeEducation> EducationFrom(string text)
    {
        var lines = text.Split('\n').Select(l => l.Trim()).ToList();
        var start = lines.FindIndex(l => EducationHeading().IsMatch(l));
        var found = new List<ResumeEducation>();
        if (start < 0) return found;
        string? school = null, dates = "";
        for (var i = start + 1; i < lines.Count; i++)
        {
            var line = lines[i];
            if (line.Length == 0) continue;
            if (SectionHeading().IsMatch(line)) break;
            if (DegreeWords().IsMatch(line))
            {
                if (school is null) continue;
                var comma = line.IndexOf(',');
                var inWord = Regex.Match(line, @"^(.+?)\s+in\s+(.+)$", RegexOptions.IgnoreCase);
                var (degree, field) = comma > 0 ? (line[..comma].Trim(), line[(comma + 1)..].Trim())
                    : inWord.Success ? (inWord.Groups[1].Value.Trim(), inWord.Groups[2].Value.Trim()) : (line, "");
                found.Add(new ResumeEducation(school, degree, field, dates ?? ""));
                school = null;
                continue;
            }
            var d = TrailingDates().Match(line);
            school = d.Success ? line[..d.Index].Trim() : line;
            dates = d.Success ? d.Value.Trim() : "";
        }
        return found;
    }

    [GeneratedRegex(@"^education(\s+(and|&)\s+\w+)?:?$", RegexOptions.IgnoreCase)]
    private static partial Regex EducationHeading();

    [GeneratedRegex(@"^[A-Z][A-Z &/,-]{3,}$")]
    private static partial Regex SectionHeading();

    [GeneratedRegex(@"\b(bachelor|master|micromaster|associate|doctor|ph\.?d|mba|b\.?s\.?c?|m\.?s\.?c?|b\.?a|m\.?a|diploma|degree)\b|’s in|'s in", RegexOptions.IgnoreCase)]
    private static partial Regex DegreeWords();

    [GeneratedRegex(@"\s+((19|20)\d{2}|[A-Z][a-z]{2,8}\.? (19|20)\d{2})\s*([–-]\s*((19|20)\d{2}|[A-Z][a-z]{2,8}\.? (19|20)\d{2}|present|current))?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDates();

    private static bool LooksLikePdf(byte[] bytes) =>
        bytes.Length >= 5
        && bytes[0] == (byte)'%'
        && bytes[1] == (byte)'P'
        && bytes[2] == (byte)'D'
        && bytes[3] == (byte)'F'
        && bytes[4] == (byte)'-';

    private static string NormalizeText(string text) =>
        string.Join("\n", text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n')
            .Select(line => SpaceRegex().Replace(line, " ").TrimEnd()))
            .Trim();

    private static string BuildResumeText(string text)
    {
        var normalized = NormalizeText(text);
        return normalized.Length <= MaxResumeTextChars
            ? normalized
            : normalized[..MaxResumeTextChars].TrimEnd() + "\n[truncated]";
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex SpaceRegex();
}
