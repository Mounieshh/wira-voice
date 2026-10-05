using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace WiraVoice.Core.Decision;

public record SourceToken(string Id, string Text, int StartUtf16, int EndUtf16);

/// <summary>
/// Tokenizes user speech utterances into lexical tokens with exact UTF-16 character offsets
/// for precise argument slicing (lexical token anchoring).
/// </summary>
public static class LexicalTokenizer
{
    private static readonly Regex TokenRegex = new(@"\S+", RegexOptions.Compiled);

    public static IReadOnlyList<SourceToken> Tokenize(string text)
    {
        if (string.IsNullOrEmpty(text))
            return Array.Empty<SourceToken>();

        var tokens = new List<SourceToken>();
        var matches = TokenRegex.Matches(text);

        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            tokens.Add(new SourceToken(
                Id: $"t{i}",
                Text: match.Value,
                StartUtf16: match.Index,
                EndUtf16: match.Index + match.Length
            ));
        }

        return tokens;
    }

    /// <summary>
    /// Slices verbatim text between two token IDs from the original text string.
    /// </summary>
    public static string SliceVerbatim(string originalText, IReadOnlyList<SourceToken> tokens, string startTokenId, string endTokenId)
    {
        int startIndex = -1;
        int endIndex = -1;

        foreach (var t in tokens)
        {
            if (t.Id == startTokenId) startIndex = t.StartUtf16;
            if (t.Id == endTokenId) endIndex = t.EndUtf16;
        }

        if (startIndex >= 0 && endIndex > startIndex && endIndex <= originalText.Length)
        {
            return originalText.Substring(startIndex, endIndex - startIndex);
        }

        return string.Empty;
    }
}
