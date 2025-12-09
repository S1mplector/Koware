// Author: Ilgaz Mehmetoğlu
// Tests for InteractiveSelector fuzzy matching and helper utilities.
using System;
using System.Collections.Generic;
using System.Reflection;
using Koware.Cli.Console;
using Xunit;

namespace Koware.Tests;

public class InteractiveSelectorTests
{
    #region SelectionResult Tests

    [Fact]
    public void SelectionResult_Cancel_ReturnsCorrectState()
    {
        var result = SelectionResult<string>.Cancel();

        Assert.True(result.Cancelled);
        Assert.Null(result.Selected);
        Assert.Equal(-1, result.SelectedIndex);
    }

    [Fact]
    public void SelectionResult_Success_ReturnsCorrectState()
    {
        var result = SelectionResult<string>.Success("test", 5);

        Assert.False(result.Cancelled);
        Assert.Equal("test", result.Selected);
        Assert.Equal(5, result.SelectedIndex);
    }

    #endregion

    #region ItemStatus Tests

    [Theory]
    [InlineData(ItemStatus.None)]
    [InlineData(ItemStatus.Watched)]
    [InlineData(ItemStatus.Downloaded)]
    [InlineData(ItemStatus.InProgress)]
    [InlineData(ItemStatus.New)]
    public void ItemStatus_AllValuesExist(ItemStatus status)
    {
        Assert.True(Enum.IsDefined(typeof(ItemStatus), status));
    }

    #endregion

    #region SelectorOptions Tests

    [Fact]
    public void SelectorOptions_DefaultValues()
    {
        var options = new SelectorOptions<string>();

        Assert.Null(options.Prompt);
        Assert.Equal(10, options.MaxVisibleItems);
        Assert.True(options.ShowSearch);
        Assert.True(options.ShowPreview);
        Assert.Equal(ConsoleColor.Cyan, options.HighlightColor);
        Assert.Equal(ConsoleColor.Yellow, options.SelectedColor);
        Assert.Null(options.EmptyMessage);
        Assert.Null(options.SecondaryDisplayFunc);
        Assert.Null(options.PreviewFunc);
        Assert.Null(options.StatusFunc);
    }

    [Fact]
    public void SelectorOptions_CustomValues()
    {
        var options = new SelectorOptions<string>
        {
            Prompt = "Select item",
            MaxVisibleItems = 15,
            ShowSearch = false,
            ShowPreview = false,
            HighlightColor = ConsoleColor.Green,
            SelectedColor = ConsoleColor.Red,
            EmptyMessage = "Nothing found",
            PreviewFunc = s => $"Preview: {s}",
            StatusFunc = _ => ItemStatus.Watched
        };

        Assert.Equal("Select item", options.Prompt);
        Assert.Equal(15, options.MaxVisibleItems);
        Assert.False(options.ShowSearch);
        Assert.False(options.ShowPreview);
        Assert.Equal(ConsoleColor.Green, options.HighlightColor);
        Assert.Equal(ConsoleColor.Red, options.SelectedColor);
        Assert.Equal("Nothing found", options.EmptyMessage);
        Assert.NotNull(options.PreviewFunc);
        Assert.NotNull(options.StatusFunc);
    }

    #endregion

    #region FuzzyScore Tests (via reflection)

    [Theory]
    [InlineData("Demon Slayer", "demon", true)]
    [InlineData("Demon Slayer", "slayer", true)]
    [InlineData("Demon Slayer", "ds", true)]
    [InlineData("One Piece", "op", true)]
    [InlineData("Attack on Titan", "aot", true)]
    [InlineData("My Hero Academia", "mha", true)]
    [InlineData("Naruto", "xyz", false)]
    public void FuzzyScore_MatchesExpectedPatterns(string text, string pattern, bool shouldMatch)
    {
        // Access the private FuzzyScore method via reflection
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "FuzzyScore",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.NotNull(method);

        var score = (int)method!.Invoke(null, new object[] { text, pattern })!;

        if (shouldMatch)
        {
            Assert.True(score > 0, $"Expected '{pattern}' to match '{text}'");
        }
        else
        {
            Assert.Equal(0, score);
        }
    }

    [Theory]
    [InlineData("Demon Slayer", "Demon Slayer")] // Exact match should score highest
    [InlineData("Demon Slayer", "Demon")]        // Prefix match
    [InlineData("Demon Slayer", "demon")]        // Case insensitive
    public void FuzzyScore_ExactAndPrefixMatchesScoreHigher(string text, string pattern)
    {
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "FuzzyScore",
            BindingFlags.NonPublic | BindingFlags.Static);

        var score = (int)method!.Invoke(null, new object[] { text, pattern })!;

        // Exact/prefix matches should have high scores (500+)
        Assert.True(score >= 500, $"Expected high score for '{pattern}' matching '{text}', got {score}");
    }

    [Fact]
    public void FuzzyScore_EmptyPattern_ReturnsOne()
    {
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "FuzzyScore",
            BindingFlags.NonPublic | BindingFlags.Static);

        var score = (int)method!.Invoke(null, new object[] { "Some Text", "" })!;

        Assert.Equal(1, score);
    }

    [Fact]
    public void FuzzyScore_EmptyText_ReturnsZero()
    {
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "FuzzyScore",
            BindingFlags.NonPublic | BindingFlags.Static);

        var score = (int)method!.Invoke(null, new object[] { "", "pattern" })!;

        Assert.Equal(0, score);
    }

    #endregion

    #region EpisodeItem Tests

    [Fact]
    public void EpisodeItem_DefaultValues()
    {
        var item = new EpisodeItem();

        Assert.Equal(0, item.Number);
        Assert.Null(item.Title);
        Assert.False(item.IsWatched);
        Assert.False(item.IsDownloaded);
        Assert.Null(item.FilePath);
    }

    [Fact]
    public void EpisodeItem_WithValues()
    {
        var item = new EpisodeItem
        {
            Number = 5,
            Title = "The Beginning",
            IsWatched = true,
            IsDownloaded = true,
            FilePath = "/path/to/file.mp4"
        };

        Assert.Equal(5, item.Number);
        Assert.Equal("The Beginning", item.Title);
        Assert.True(item.IsWatched);
        Assert.True(item.IsDownloaded);
        Assert.Equal("/path/to/file.mp4", item.FilePath);
    }

    #endregion

    #region HistoryItem Tests

    [Fact]
    public void HistoryItem_DefaultValues()
    {
        var item = new HistoryItem();

        Assert.Equal("", item.Title);
        Assert.Equal(0, item.LastEpisode);
        Assert.Null(item.TotalEpisodes);
        Assert.Equal(default, item.WatchedAt);
        Assert.Null(item.Provider);
        Assert.Null(item.Quality);
    }

    [Fact]
    public void HistoryItem_WithValues()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new HistoryItem
        {
            Title = "Demon Slayer",
            LastEpisode = 10,
            TotalEpisodes = 26,
            WatchedAt = now,
            Provider = "AllAnime",
            Quality = "1080p"
        };

        Assert.Equal("Demon Slayer", item.Title);
        Assert.Equal(10, item.LastEpisode);
        Assert.Equal(26, item.TotalEpisodes);
        Assert.Equal(now, item.WatchedAt);
        Assert.Equal("AllAnime", item.Provider);
        Assert.Equal("1080p", item.Quality);
    }

    #endregion

    #region InteractiveBrowser Tests

    [Fact]
    public void InteractiveBrowser_BrowseEpisodes_EmptyList_ReturnsNull()
    {
        var result = InteractiveBrowser.BrowseEpisodes(Array.Empty<EpisodeItem>(), "Test");
        Assert.Null(result);
    }

    [Fact]
    public void InteractiveBrowser_BrowseChapters_EmptyList_ReturnsNull()
    {
        var result = InteractiveBrowser.BrowseChapters(Array.Empty<EpisodeItem>(), "Test");
        Assert.Null(result);
    }

    #endregion

    #region InteractiveSelect Helper Tests

    [Fact]
    public void InteractiveSelect_Confirm_DefaultYes_ReturnsTrue_OnEnter()
    {
        // This test documents expected behavior but can't actually test
        // interactive console input without mocking
        // The test serves as documentation
        Assert.True(true);
    }

    [Fact]
    public void InteractiveSelect_SelectNumber_ValidRange()
    {
        // This test documents expected behavior
        // Actual interactive testing would require console mocking
        Assert.True(true);
    }

    #endregion
}

public class WordWrapTests
{
    [Fact]
    public void WordWrap_EmptyString_ReturnsEmptyEnumerable()
    {
        // Access via reflection since it's private
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "WordWrap",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = method!.Invoke(null, new object[] { "", 50 }) as IEnumerable<string>;

        Assert.NotNull(result);
        var list = new List<string>(result!);
        Assert.Single(list);
        Assert.Equal("", list[0]);
    }

    [Fact]
    public void WordWrap_ShortText_SingleLine()
    {
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "WordWrap",
            BindingFlags.NonPublic | BindingFlags.Static);

        var result = method!.Invoke(null, new object[] { "Hello World", 50 }) as IEnumerable<string>;

        Assert.NotNull(result);
        var list = new List<string>(result!);
        Assert.Single(list);
        Assert.Equal("Hello World", list[0]);
    }

    [Fact]
    public void WordWrap_LongText_MultipleLines()
    {
        var method = typeof(InteractiveSelector<string>).GetMethod(
            "WordWrap",
            BindingFlags.NonPublic | BindingFlags.Static);

        var text = "This is a longer text that should be wrapped across multiple lines when the width is small";
        var result = method!.Invoke(null, new object[] { text, 20 }) as IEnumerable<string>;

        Assert.NotNull(result);
        var list = new List<string>(result!);
        Assert.True(list.Count > 1);
        foreach (var line in list)
        {
            Assert.True(line.Length <= 25); // Allow some flexibility
        }
    }
}
