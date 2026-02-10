// Author: Ilgaz Mehmetoğlu
// Fuzzy string matching algorithm for search functionality.
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Koware.Cli.Console;

/// <summary>
/// Provides fuzzy string matching capabilities for filtering lists.
/// </summary>
public static class FuzzyMatcher
{
    /// <summary>
    /// Calculate a fuzzy match score between text and pattern.
    /// Higher scores indicate better matches. Zero means no match.
    /// </summary>
    /// <param name="text">The text to search in.</param>
    /// <param name="pattern">The pattern to match.</param>
    /// <returns>Match score (0 = no match, higher = better match).</returns>
    public static int Score(string text, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 1;
        if (string.IsNullOrEmpty(text)) return 0;

        var textSpan = text.AsSpan();
        var patternSpan = pattern.AsSpan();

        // Fast vectorized case-insensitive substring search provided by runtime.
        var substringIndex = textSpan.IndexOf(patternSpan, StringComparison.OrdinalIgnoreCase);
        if (substringIndex >= 0)
        {
            // Bonus for match at start
            return substringIndex == 0 ? 1000 + patternSpan.Length : 500 + patternSpan.Length;
        }

        // Fuzzy match: all pattern characters must appear in order
        return ScoreFuzzy(textSpan, patternSpan);
    }

    /// <summary>
    /// Score a fuzzy match where pattern characters appear in order but not necessarily contiguous.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ScoreFuzzy(ReadOnlySpan<char> text, ReadOnlySpan<char> pattern)
    {
        var score = 0;
        var patternIndex = 0;
        var consecutiveBonus = 0;
        var lastMatchIndex = -1;
        var patternLen = pattern.Length;
        var textLen = text.Length;
        var nextPatternChar = char.ToLowerInvariant(pattern[0]);

        for (var i = 0; i < textLen && patternIndex < patternLen; i++)
        {
            if (char.ToLowerInvariant(text[i]) == nextPatternChar)
            {
                score += 10;

                // Bonus for consecutive matches
                if (lastMatchIndex == i - 1)
                {
                    consecutiveBonus += 5;
                }

                // Bonus for matching at word boundaries
                if (i == 0 || !char.IsLetterOrDigit(text[i - 1]))
                {
                    score += 15;
                }

                lastMatchIndex = i;
                patternIndex++;
                if (patternIndex < patternLen)
                {
                    nextPatternChar = char.ToLowerInvariant(pattern[patternIndex]);
                }
            }
        }

        // All pattern characters must match
        if (patternIndex < patternLen)
            return 0;

        return score + consecutiveBonus;
    }

    /// <summary>
    /// Filter and sort a list of items by fuzzy match score.
    /// </summary>
    /// <typeparam name="T">Type of items.</typeparam>
    /// <param name="items">Items to filter.</param>
    /// <param name="getText">Function to get searchable text from item.</param>
    /// <param name="pattern">Search pattern.</param>
    /// <returns>Filtered items with their original indices and scores, sorted by score.</returns>
    public static IReadOnlyList<(T Item, int OriginalIndex, int Score)> Filter<T>(
        IReadOnlyList<T> items,
        Func<T, string> getText,
        string pattern)
    {
        var count = items.Count;

        if (string.IsNullOrWhiteSpace(pattern))
        {
            var allResults = new (T Item, int OriginalIndex, int Score)[count];
            for (var i = 0; i < count; i++)
            {
                allResults[i] = (items[i], i, 0);
            }
            return allResults;
        }

        // Pre-allocate with estimated capacity to avoid resizing
        var results = new List<(T Item, int OriginalIndex, int Score)>(Math.Min(count, 64));

        for (var i = 0; i < count; i++)
        {
            var score = Score(getText(items[i]), pattern);
            if (score > 0)
            {
                results.Add((items[i], i, score));
            }
        }

        // Sort in-place by score descending
        results.Sort((a, b) => b.Score.CompareTo(a.Score));

        return results;
    }
}
