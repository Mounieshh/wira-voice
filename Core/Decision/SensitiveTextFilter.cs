using System.Text.RegularExpressions;

namespace WiraVoice.Core.Decision;

/// <summary>
/// Locally filters and redacts passwords, tokens, and secret strings before network dispatch.
/// </summary>
public static class SensitiveTextFilter
{
    private static readonly Regex KeyValueRegex = new(
        @"(api[_-]?key|secret|password|token)\s*[:=]\s*([^\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled
    );

    public static string Redact(string input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;

        return KeyValueRegex.Replace(input, "$1: [REDACTED]");
    }
}
