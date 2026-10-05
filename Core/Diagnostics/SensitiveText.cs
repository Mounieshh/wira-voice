using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WiraVoice.Core.Diagnostics;

/// <summary>
/// Detects protected credential spans before tokenizing, redacts sensitive text,
/// and scrubs JSON payloads sent across network boundaries.
/// Exact port of Computah's SensitiveText.swift.
/// </summary>
public static class SensitiveText
{
    private static readonly Regex[] Patterns =
    [
        new(@"\b[A-Z0-9_]*(?:API_KEY|ACCESS_TOKEN|AUTH_TOKEN|SECRET|PASSWORD)[A-Z0-9_]*\s*=\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s;]+)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Compiled),
        new(@"\b(?:sk-[A-Za-z0-9_-]{12,}|apikey_[A-Za-z0-9_-]{12,})",
            RegexOptions.Compiled)
    ];

    public readonly record struct TextRange(int Location, int Length)
    {
        public int Max => Location + Length;

        public bool Intersects(TextRange other) =>
            Math.Max(Location, other.Location) < Math.Min(Max, other.Max);

        public TextRange Intersection(TextRange other)
        {
            var start = Math.Max(Location, other.Location);
            var end = Math.Min(Max, other.Max);
            return end > start ? new TextRange(start, end - start) : new TextRange(0, 0);
        }

        public TextRange Union(TextRange other)
        {
            var start = Math.Min(Location, other.Location);
            var end = Math.Max(Max, other.Max);
            return new TextRange(start, end - start);
        }
    }

    /// <summary>
    /// Detect protected spans before tokenizing or slicing the original source.
    /// </summary>
    public static List<TextRange> ProtectedRanges(string source)
    {
        if (string.IsNullOrEmpty(source)) return [];

        var ranges = new List<TextRange>();
        foreach (var pattern in Patterns)
        {
            var matches = pattern.Matches(source);
            foreach (Match match in matches)
            {
                if (match.Success)
                {
                    ranges.Add(new TextRange(match.Index, match.Length));
                }
            }
        }
        return ranges;
    }

    public static string Redact(string source, TextRange range)
    {
        if (string.IsNullOrEmpty(source) || range.Length <= 0) return string.Empty;

        var sub = source.Substring(range.Location, range.Length);
        var protectedInSub = ProtectedRanges(source)
            .Select(p => p.Intersection(range))
            .Where(i => i.Length > 0)
            .OrderBy(i => i.Location)
            .ToList();

        if (protectedInSub.Count == 0) return sub;

        // Merge overlapping spans
        var spans = new List<TextRange>();
        foreach (var span in protectedInSub)
        {
            if (spans.Count > 0 && spans[^1].Max >= span.Location)
            {
                spans[^1] = spans[^1].Union(span);
            }
            else
            {
                spans.Add(span);
            }
        }

        var sb = new System.Text.StringBuilder(sub);
        // Replace from end to front to keep relative offsets
        for (int i = spans.Count - 1; i >= 0; i--)
        {
            var relLocation = spans[i].Location - range.Location;
            sb.Remove(relLocation, spans[i].Length);
            sb.Insert(relLocation, "[REDACTED CREDENTIAL]");
        }

        return sb.ToString();
    }

    public static string Redact(string source)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        if (!source.Contains('=') && !source.Contains("sk-") && !source.Contains("apikey_"))
            return source;

        var text = source;
        foreach (var pattern in Patterns)
        {
            text = pattern.Replace(text, "[REDACTED CREDENTIAL]");
        }
        return text;
    }

    public static object Json(object? value)
    {
        return value switch
        {
            null => string.Empty,
            string s => Redact(s),
            IDictionary<string, object?> dict => dict.ToDictionary(k => k.Key, v => Json(v.Value)),
            IEnumerable<object?> list => list.Select(Json).ToList(),
            _ => value
        };
    }
}
