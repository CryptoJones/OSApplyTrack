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
        return new Resume
        {
            Summary = BuildResumeText(text),
        };
    }

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
