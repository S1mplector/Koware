// Author: Ilgaz Mehmetoğlu
// Tests for the FuzzyMatcher string matching algorithm.
using System;
using System.Linq;
using Koware.Cli.Console;
using Xunit;

namespace Koware.Tests;

public class FuzzyMatcherTests
{
    #region Score - Basic Matching

    [Fact]
    public void Score_EmptyPattern_ReturnsOne()
    {
        var score = FuzzyMatcher.Score("Any text", "");
        Assert.Equal(1, score);
    }

    [Fact]
    public void Score_NullPattern_ReturnsOne()
    {
        var score = FuzzyMatcher.Score("Any text", null!);
        Assert.Equal(1, score);
    }

    [Fact]
    public void Score_EmptyText_ReturnsZero()
    {
        var score = FuzzyMatcher.Score("", "pattern");
        Assert.Equal(0, score);
    }

    [Fact]
    public void Score_NullText_ReturnsZero()
    {
        var score = FuzzyMatcher.Score(null!, "pattern");
        Assert.Equal(0, score);
    }

    [Fact]
    public void Score_NoMatch_ReturnsZero()
    {
        var score = FuzzyMatcher.Score("Hello World", "xyz");
        Assert.Equal(0, score);
    }

    #endregion

    #region Score - Exact Matches

    [Theory]
    [InlineData("Demon Slayer", "Demon Slayer")]
    [InlineData("demon slayer", "DEMON SLAYER")]
    [InlineData("DEMON SLAYER", "demon slayer")]
    public void Score_ExactMatch_CaseInsensitive_HighScore(string text, string pattern)
    {
        var score = FuzzyMatcher.Score(text, pattern);
        Assert.True(score >= 500, $"Expected high score for exact match, got {score}");
    }

    [Theory]
    [InlineData("Demon Slayer", "Demon")]
    [InlineData("Attack on Titan", "Attack")]
    [InlineData("One Piece", "One")]
    public void Score_PrefixMatch_ScoresHigherThan500(string text, string pattern)
    {
        var score = FuzzyMatcher.Score(text, pattern);
        Assert.True(score >= 1000, $"Expected prefix match score >= 1000, got {score}");
    }

    [Theory]
    [InlineData("Demon Slayer", "Slayer")]
    [InlineData("Attack on Titan", "Titan")]
    [InlineData("One Piece", "Piece")]
    public void Score_SubstringMatch_ScoresBetween500And1000(string text, string pattern)
    {
        var score = FuzzyMatcher.Score(text, pattern);
        Assert.True(score >= 500 && score < 1000, $"Expected substring match score 500-999, got {score}");
    }

    #endregion

    #region Score - Fuzzy Matches

    [Theory]
    [InlineData("Demon Slayer", "ds")]
    [InlineData("One Piece", "op")]
    [InlineData("Attack on Titan", "aot")]
    [InlineData("My Hero Academia", "mha")]
    [InlineData("Fullmetal Alchemist", "fma")]
    public void Score_Abbreviation_Matches(string text, string pattern)
    {
        var score = FuzzyMatcher.Score(text, pattern);
        Assert.True(score > 0, $"Expected abbreviation '{pattern}' to match '{text}'");
    }

    [Fact]
    public void Score_ConsecutiveMatches_ScoreHigherThanNonConsecutive()
    {
        // "dem" has consecutive matches in "Demon"
        var consecutiveScore = FuzzyMatcher.Score("Demon Slayer", "dem");
        
        // "dmn" has non-consecutive matches
        var nonConsecutiveScore = FuzzyMatcher.Score("Demon Slayer", "dmn");

        // Both should match, but consecutive should score higher
        Assert.True(consecutiveScore > 0);
        Assert.True(nonConsecutiveScore > 0);
        // Note: consecutiveScore is actually an exact substring match (500+)
        Assert.True(consecutiveScore > nonConsecutiveScore);
    }

    [Fact]
    public void Score_WordBoundaryMatch_ScoresHigher()
    {
        // "ds" matches at word boundaries (D-emon S-layer)
        var wordBoundaryScore = FuzzyMatcher.Score("Demon Slayer", "ds");
        
        // "es" matches mid-word (d-E-mon S-layer)
        var midWordScore = FuzzyMatcher.Score("Demon Slayer", "es");

        Assert.True(wordBoundaryScore > 0);
        Assert.True(midWordScore > 0);
        Assert.True(wordBoundaryScore > midWordScore, 
            $"Word boundary score ({wordBoundaryScore}) should be higher than mid-word ({midWordScore})");
    }

    [Fact]
    public void Score_PartialPatternMatch_ReturnsZero()
    {
        // Pattern "xyz" can't be found in order in "Demon Slayer"
        var score = FuzzyMatcher.Score("Demon Slayer", "xyz");
        Assert.Equal(0, score);
    }

    [Fact]
    public void Score_PatternLongerThanText_ReturnsZero()
    {
        var score = FuzzyMatcher.Score("Hi", "Hello World");
        Assert.Equal(0, score);
    }

    #endregion

    #region Filter - Basic Functionality

    [Fact]
    public void Filter_EmptyPattern_ReturnsAllItemsWithZeroScore()
    {
        var items = new[] { "One", "Two", "Three" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "");

        Assert.Equal(3, result.Count);
        Assert.All(result, r => Assert.Equal(0, r.Score));
    }

    [Fact]
    public void Filter_WhitespacePattern_ReturnsAllItems()
    {
        var items = new[] { "One", "Two", "Three" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "   ");

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void Filter_EmptyList_ReturnsEmptyList()
    {
        var items = Array.Empty<string>();
        
        var result = FuzzyMatcher.Filter(items, x => x, "test");

        Assert.Empty(result);
    }

    [Fact]
    public void Filter_NoMatches_ReturnsEmptyList()
    {
        var items = new[] { "Apple", "Banana", "Cherry" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "xyz");

        Assert.Empty(result);
    }

    #endregion

    #region Filter - Sorting and Scoring

    [Fact]
    public void Filter_SortsByScoreDescending()
    {
        var items = new[] { "Demon", "Demon Slayer", "Demonized" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "demon");

        // "Demon" should be first (exact match + prefix)
        Assert.Equal("Demon", result[0].Item);
    }

    [Fact]
    public void Filter_PreservesOriginalIndex()
    {
        var items = new[] { "Cherry", "Apple", "Banana" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "a");

        // Find Apple and Banana in results
        var apple = result.FirstOrDefault(r => r.Item == "Apple");
        var banana = result.FirstOrDefault(r => r.Item == "Banana");

        Assert.Equal(1, apple.OriginalIndex);
        Assert.Equal(2, banana.OriginalIndex);
    }

    [Fact]
    public void Filter_ExcludesNonMatches()
    {
        var items = new[] { "Demon Slayer", "One Piece", "Naruto", "Attack on Titan" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "on");

        // Should match "One Piece" and "Attack on Titan"
        Assert.Equal(2, result.Count);
        Assert.Contains(result, r => r.Item == "One Piece");
        Assert.Contains(result, r => r.Item == "Attack on Titan");
    }

    #endregion

    #region Filter - Complex Types

    [Fact]
    public void Filter_WithCustomDisplayFunction()
    {
        var items = new[]
        {
            new { Id = 1, Name = "Demon Slayer" },
            new { Id = 2, Name = "One Piece" },
            new { Id = 3, Name = "Naruto" }
        };

        var result = FuzzyMatcher.Filter(items, x => x.Name, "piece");

        Assert.Single(result);
        Assert.Equal(2, result[0].Item.Id);
        Assert.Equal(1, result[0].OriginalIndex);
    }

    #endregion

    #region Real-World Anime Matching

    [Theory]
    [InlineData("Shingeki no Kyojin", "aot", false)]  // AOT doesn't match Japanese name
    [InlineData("Attack on Titan", "aot", true)]
    [InlineData("Kimetsu no Yaiba", "demon", false)]
    [InlineData("Demon Slayer: Kimetsu no Yaiba", "demon", true)]
    [InlineData("Boku no Hero Academia", "mha", false)]
    [InlineData("My Hero Academia", "mha", true)]
    public void Score_AnimeAbbreviations_MatchExpected(string title, string abbreviation, bool shouldMatch)
    {
        var score = FuzzyMatcher.Score(title, abbreviation);

        if (shouldMatch)
        {
            Assert.True(score > 0, $"Expected '{abbreviation}' to match '{title}'");
        }
        else
        {
            Assert.Equal(0, score);
        }
    }

    [Theory]
    [InlineData("Episode 1", "1")]
    [InlineData("Episode 10", "10")]
    [InlineData("Episode 100", "100")]
    [InlineData("Chapter 42", "42")]
    public void Score_NumericSearch_Matches(string text, string pattern)
    {
        var score = FuzzyMatcher.Score(text, pattern);
        Assert.True(score > 0, $"Expected '{pattern}' to match '{text}'");
    }

    #endregion
}
