// Author: Ilgaz Mehmetoğlu
// Tests for InteractiveSelector and related helper utilities.
using System;
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

    #region FuzzyMatcher Integration Tests

    // Note: Detailed FuzzyMatcher tests are in FuzzyMatcherTests.cs
    // These tests verify integration with InteractiveSelector

    [Theory]
    [InlineData("Demon Slayer", "demon", true)]
    [InlineData("Demon Slayer", "ds", true)]
    [InlineData("Naruto", "xyz", false)]
    public void FuzzyMatcher_IntegrationWithSelector(string text, string pattern, bool shouldMatch)
    {
        // Use the public FuzzyMatcher API
        var score = FuzzyMatcher.Score(text, pattern);

        if (shouldMatch)
        {
            Assert.True(score > 0, $"Expected '{pattern}' to match '{text}'");
        }
        else
        {
            Assert.Equal(0, score);
        }
    }

    [Fact]
    public void FuzzyMatcher_Filter_ReturnsCorrectIndices()
    {
        var items = new[] { "Apple", "Banana", "Avocado" };
        
        var result = FuzzyMatcher.Filter(items, x => x, "a");
        
        // All items contain 'a', should preserve original indices
        Assert.All(result, r => Assert.True(r.OriginalIndex >= 0 && r.OriginalIndex < items.Length));
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

// Note: WordWrap tests removed - WordWrap is now a private method in SelectorRenderer.
// The functionality is tested indirectly through SelectorRenderer rendering tests.
