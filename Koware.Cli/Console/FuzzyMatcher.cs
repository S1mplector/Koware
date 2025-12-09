// Author: Ilgaz Mehmetoğlu
// Fuzzy string matching algorithm for search functionality.
using System;
using System.Collections.Generic;
using System.Linq;

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

        var textLower = text.ToLowerInvariant();
        var patternLower = pattern.ToLowerInvariant();

        // Exact substring match gets highest score
        if (textLower.Contains(patternLower))
        {
            // Bonus for match at start
            if (textLower.StartsWith(patternLower))
                return 1000 + patternLower.Length;
            return 500 + patternLower.Length;
        }

        // Fuzzy match: all pattern characters must appear in order
        var score = 0;
        var patternIndex = 0;
        var consecutiveBonus = 0;
        var lastMatchIndex = -1;

        for (var i = 0; i < textLower.Length && patternIndex < patternLower.Length; i++)
        {
            if (textLower[i] == patternLower[patternIndex])
            {
                score += 10;

                // Bonus for consecutive matches
                if (lastMatchIndex == i - 1)
                {
                    consecutiveBonus += 5;
                }

                // Bonus for matching at word boundaries
                if (i == 0 || !char.IsLetterOrDigit(textLower[i - 1]))
                {
                    score += 15;
                }

                lastMatchIndex = i;
                patternIndex++;
            }
        }

        // All pattern characters must match
        if (patternIndex < patternLower.Length)
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
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return items
                .Select((item, index) => (item, index, Score: 0))
                .ToList();
        }

        return items
            .Select((item, index) => (item, index, Score: Score(getText(item), pattern)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ToList();
    }
}
