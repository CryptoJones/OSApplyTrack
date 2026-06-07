// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Data;

/// <summary>
/// The single naming control — C# heir to the Python <c>safe_name</c> /
/// <c>filename_for</c> / <c>_norm_company</c>. A row's <c>name</c> is the slug the
/// SPA uses as the public key in <c>/api/apps/{name}</c>; there is no filesystem
/// here, but the same validation keeps names safe and stable.
/// </summary>
public static partial class Slug
{
    [GeneratedRegex(@"[\\/:*?""<>|\x00-\x1f]")]
    private static partial Regex IllegalFilenameChars();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    /// <summary>Build the slug filename from company + role (heir to filename_for).</summary>
    public static string FilenameFor(string company, string role)
    {
        var stem = $"{company} {role}".Trim();
        if (stem.Length == 0) stem = company ?? "";
        var cleaned = IllegalFilenameChars().Replace(stem, " ").Trim();
        cleaned = Whitespace().Replace(cleaned, "-").ToLowerInvariant();
        if (cleaned.Length == 0)
            throw new AppValidationException("company/role produces an empty filename");
        return cleaned + ".md";
    }

    /// <summary>
    /// Validate/normalize a user-supplied name: reject empty, '.'/'..', and path
    /// separators; ensure the '.md' suffix (heir to safe_name, minus the disk).
    /// </summary>
    public static string Normalize(string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0 || n is "." or "..")
            throw new AppValidationException("empty or invalid name");
        if (n.Contains('/') || n.Contains('\\'))
            throw new AppValidationException($"name may not contain path separators: '{name}'");
        if (n.Split('/', '\\').Contains(".."))
            throw new AppValidationException($"name may not contain '..': '{name}'");
        if (!n.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            n += ".md";
        return n;
    }

    /// <summary>Drop the '.md' suffix for display fallbacks (heir to app.js stem()).</summary>
    public static string NameStem(string name) =>
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? name[..^3] : name;

    /// <summary>Normalized blacklist key, spacing/punctuation-insensitive (heir to _norm_company).</summary>
    public static string NormCompany(string? company) =>
        NonAlnum().Replace((company ?? "").ToLowerInvariant(), "-").Trim('-');
}
