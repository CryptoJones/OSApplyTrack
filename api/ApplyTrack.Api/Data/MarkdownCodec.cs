// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Text;
using System.Text.RegularExpressions;

namespace ApplyTrack.Api.Data;

/// <summary>
/// Renders a row to the YAML-frontmatter + Markdown-body text the SPA's "Raw"
/// editor round-trips, and parses that text back into fields. Heir to the Python
/// <c>render_fields</c> / <c>parse_app</c>. The frontmatter holds the 14 structured
/// keys (in a fixed order); the body is the free-text notes.
/// </summary>
public static partial class MarkdownCodec
{
    [GeneratedRegex(@"\A---\n(.*?)\n---\n?(.*)\z", RegexOptions.Singleline)]
    private static partial Regex Frontmatter();

    public static string Render(AppFields f)
    {
        var created = string.IsNullOrEmpty(f.Created) ? Today() : f.Created;
        (string Key, string Value)[] pairs =
        [
            ("company", f.Company), ("role", f.Role), ("lane", f.Lane), ("status", f.Status),
            ("link", f.Link), ("location", f.Location), ("salary", f.Salary), ("source", f.Source),
            ("contact", f.Contact), ("contact_email", f.ContactEmail), ("applied", f.Applied),
            ("followup", f.Followup), ("created", created), ("score", f.Score),
        ];
        var sb = new StringBuilder();
        foreach (var (key, value) in pairs)
            sb.Append(key).Append(": ").Append(EmitScalar(value)).Append('\n');
        var yaml = sb.ToString().TrimEnd('\n');
        var body = (f.Notes ?? "").Trim();
        return body.Length > 0 ? $"---\n{yaml}\n---\n\n{body}\n" : $"---\n{yaml}\n---\n";
    }

    public static AppFields Parse(string md)
    {
        md = md.TrimStart('﻿');
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        string body;
        var m = Frontmatter().Match(md);
        if (m.Success)
        {
            body = m.Groups[2].Value;
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var idx = line.IndexOf(':');
                if (idx <= 0) continue;
                data[line[..idx].Trim()] = Unquote(line[(idx + 1)..].Trim());
            }
        }
        else
        {
            body = md;
        }

        string G(string key) => data.TryGetValue(key, out var v) ? v : "";
        return new AppFields
        {
            Company = G("company"),
            Role = G("role"),
            Lane = G("lane"),
            Status = G("status"),
            Link = G("link"),
            Location = G("location"),
            Salary = G("salary"),
            Source = G("source"),
            Contact = G("contact"),
            ContactEmail = G("contact_email"),
            Applied = G("applied"),
            Followup = G("followup"),
            Created = G("created"),
            Score = G("score"),
            Notes = body.Trim(),
        }.Normalized();
    }

    public static string Today() => DateTime.UtcNow.ToString("yyyy-MM-dd");

    // Emit a YAML scalar, double-quoting (with escapes) when a bare value could be
    // misread. Empty becomes '' so the key keeps a value, matching PyYAML.
    private static string EmitScalar(string v)
    {
        if (v.Length == 0) return "''";
        var reserved = new[] { "yes", "no", "true", "false", "null", "~", "on", "off" };
        var needsQuote =
            v != v.Trim()
            || v.IndexOfAny([':', '#', '\n', '\t', '"', '\'']) >= 0
            || reserved.Contains(v.ToLowerInvariant())
            || "-[]{}*&!@`%,>|".IndexOf(v[0]) >= 0
            || char.IsDigit(v[0]);
        if (!needsQuote) return v;
        return "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private static string Unquote(string v)
    {
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"')
            return v[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");
        if (v.Length >= 2 && v[0] == '\'' && v[^1] == '\'')
            return v[1..^1].Replace("''", "'");
        return v;
    }
}
