// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Globalization;
using System.Net;
using System.Reflection;

namespace ApplyTrack.Api.Domains;

/// <summary>
/// Registrable domains from the Public Suffix List — ICANN and PRIVATE sections — so
/// "the same site" means what browsers mean by it (#343). The old two-label guess made
/// <c>careers.acme.co.uk</c> the same site as <c>evil.co.uk</c>, and one customer of
/// <c>*.github.io</c> or <c>*.azurewebsites.net</c> the same site as every other.
/// The list ships inside the assembly (<c>public_suffix_list.dat</c>, MPL-2.0, unmodified);
/// refresh it from https://publicsuffix.org/list/public_suffix_list.dat — never fetched at runtime.
/// </summary>
public static class PublicSuffix
{
    private sealed record Rules(HashSet<string> Exact, HashSet<string> Wildcard, HashSet<string> Exception);

    private static readonly Lazy<Rules> List = new(Load);

    private static Rules Load()
    {
        var exact = new HashSet<string>(StringComparer.Ordinal);
        var wildcard = new HashSet<string>(StringComparer.Ordinal);
        var exception = new HashSet<string>(StringComparer.Ordinal);
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ApplyTrack.Api.Domains.public_suffix_list.dat")
            ?? throw new InvalidOperationException("public_suffix_list.dat is not embedded");
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // A rule is the first whitespace-delimited token; comments and blanks are skipped.
            var rule = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (rule is null || rule.StartsWith("//", StringComparison.Ordinal)) continue;
            rule = rule.ToLowerInvariant();
            if (rule.StartsWith('!')) exception.Add(rule[1..]);
            else if (rule.StartsWith("*.", StringComparison.Ordinal)) wildcard.Add(rule[2..]);
            else exact.Add(rule);
        }
        return new Rules(exact, wildcard, exception);
    }

    /// <summary>The registrable domain of <paramref name="host"/> (<c>careers.acme.co.uk</c> →
    /// <c>acme.co.uk</c>), the host itself for an IP address, or null when the host is itself a
    /// public suffix (<c>co.uk</c>, <c>github.io</c>) and so belongs to no one.</summary>
    public static string? RegistrableDomain(string host)
    {
        var h = (host ?? "").Trim().TrimEnd('.').ToLowerInvariant();
        if (h.Length == 0) return null;
        if (IPAddress.TryParse(h.Trim('[', ']'), out _)) return h;
        var labels = h.Split('.');
        if (labels.Any(l => l.Length == 0)) return null;
        // The list is written in Unicode; hosts arrive as punycode.
        string[] match;
        try { match = new IdnMapping().GetUnicode(h).Split('.'); }
        catch (ArgumentException) { match = labels; }
        if (match.Length != labels.Length) match = labels;

        var rules = List.Value;
        var suffixLabels = 1;   // the default rule "*": an unlisted TLD is a public suffix
        for (var i = 0; i < match.Length; i++)
        {
            var candidate = string.Join('.', match[i..]);
            if (rules.Exception.Contains(candidate)) { suffixLabels = match.Length - i - 1; break; }
            if (rules.Exact.Contains(candidate)
                || (i + 1 < match.Length && rules.Wildcard.Contains(string.Join('.', match[(i + 1)..]))))
            {
                suffixLabels = match.Length - i;
                break;
            }
        }
        return labels.Length <= suffixLabels ? null : string.Join('.', labels[^(suffixLabels + 1)..]);
    }

    /// <summary>Do two hosts share a registrable domain? Never when either is a bare public suffix.</summary>
    public static bool SameSite(string a, string b) =>
        RegistrableDomain(a) is { } ra && string.Equals(ra, RegistrableDomain(b), StringComparison.Ordinal);

    /// <summary>Is <paramref name="host"/> <paramref name="parent"/> or beneath it, where
    /// <paramref name="parent"/> is somebody's domain and not a public suffix? <c>github.io</c>
    /// is the parent of every Pages site and vouches for none of them.</summary>
    public static bool WithinDomain(string host, string parent) =>
        host == parent
        || (host.EndsWith("." + parent, StringComparison.Ordinal) && RegistrableDomain(parent) is not null);
}
