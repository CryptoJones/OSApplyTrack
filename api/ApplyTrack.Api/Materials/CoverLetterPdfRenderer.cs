// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;
using System.Text;
using System.Globalization;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using UglyToad.PdfPig.Core;

namespace ApplyTrack.Api.Materials;

/// <summary>Renders a generated letter into a small, portable text PDF.</summary>
public static partial class CoverLetterPdfRenderer
{
    [GeneratedRegex(@"[*_`#]", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownMarkers();

    public static byte[] Render(string body)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        var lines = Wrap(ToPdfText(MarkdownMarkers().Replace(body.Replace("\r\n", "\n"), "")));
        const double left = 54;
        const double top = 770;
        const double lineHeight = 16;
        const double bottom = 54;
        var page = builder.AddPage(PageSize.A4);
        var y = top;

        foreach (var line in lines)
        {
            if (y < bottom)
            {
                page = builder.AddPage(PageSize.A4);
                y = top;
            }

            if (line.Length > 0)
                page.AddText(line, 11, new PdfPoint(left, y), font);
            y -= lineHeight;
        }

        return builder.Build();
    }

    private static IReadOnlyList<string> Wrap(string text)
    {
        var result = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            if (paragraph.Length == 0)
            {
                result.Add("");
                continue;
            }

            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = "";
            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : $"{line} {word}";
                if (candidate.Length > 88 && line.Length > 0)
                {
                    result.Add(line);
                    line = word;
                }
                else
                {
                    line = candidate;
                }
            }
            result.Add(line);
        }
        return result;
    }

    // PdfPig's Standard 14 fonts are intentionally ASCII-only. Fold accented
    // Latin characters and replace any remaining non-ASCII glyphs safely.
    private static string ToPdfText(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var chars = normalized
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .Select(c => c <= 127 ? c : '?')
            .ToArray();
        return new string(chars);
    }
}
