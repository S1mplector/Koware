// Author: Ilgaz Mehmetoğlu
// Tests for SelectorRenderer and related types.
using System;
using Koware.Cli.Console;
using Xunit;

namespace Koware.Tests;

public class SelectorRendererTests
{
    #region RenderConfig Tests

    [Fact]
    public void RenderConfig_DefaultValues()
    {
        var config = new RenderConfig();

        Assert.Equal("Select", config.Prompt);
        Assert.Equal(10, config.MaxVisibleItems);
        Assert.True(config.ShowSearch);
        Assert.False(config.ShowPreview);
        Assert.True(config.ShowFooter);
    }

    [Fact]
    public void RenderConfig_CustomValues()
    {
        var config = new RenderConfig
        {
            Prompt = "Choose item",
            MaxVisibleItems = 15,
            ShowSearch = false,
            ShowPreview = true,
            ShowFooter = false,
            HighlightColor = ConsoleColor.Green,
            SelectionColor = ConsoleColor.Red
        };

        Assert.Equal("Choose item", config.Prompt);
        Assert.Equal(15, config.MaxVisibleItems);
        Assert.False(config.ShowSearch);
        Assert.True(config.ShowPreview);
        Assert.False(config.ShowFooter);
        Assert.Equal(ConsoleColor.Green, config.HighlightColor);
        Assert.Equal(ConsoleColor.Red, config.SelectionColor);
    }

    #endregion

    #region RenderState Tests

    [Fact]
    public void RenderState_RequiredProperties()
    {
        var items = new[] { ("Item 1", 0, ItemStatus.None, (string?)null) };
        
        var state = new RenderState
        {
            Items = items,
            TotalCount = 10,
            FilteredCount = 5,
            SelectedIndex = 0,
            ScrollOffset = 0,
            SearchText = ""
        };

        Assert.Equal(items, state.Items);
        Assert.Equal(10, state.TotalCount);
        Assert.Equal(5, state.FilteredCount);
        Assert.Equal(0, state.SelectedIndex);
        Assert.Equal(0, state.ScrollOffset);
        Assert.Equal("", state.SearchText);
    }

    [Fact]
    public void RenderState_SearchText_DefaultsToEmpty()
    {
        var state = new RenderState
        {
            Items = Array.Empty<(string, int, ItemStatus, string?)>(),
            TotalCount = 0,
            FilteredCount = 0,
            SelectedIndex = 0,
            ScrollOffset = 0
        };

        Assert.Equal("", state.SearchText);
    }

    [Fact]
    public void RenderState_WithSearchText()
    {
        var state = new RenderState
        {
            Items = Array.Empty<(string, int, ItemStatus, string?)>(),
            TotalCount = 0,
            FilteredCount = 0,
            SelectedIndex = 0,
            ScrollOffset = 0,
            SearchText = "demon"
        };

        Assert.Equal("demon", state.SearchText);
    }

    #endregion

    #region SelectorRenderer Constructor Tests

    [Fact]
    public void SelectorRenderer_NullBuffer_ThrowsArgumentNullException()
    {
        var config = new RenderConfig();
        
        Assert.Throws<ArgumentNullException>(() => new SelectorRenderer(null!, config));
    }

    [Fact]
    public void SelectorRenderer_NullConfig_ThrowsArgumentNullException()
    {
        using var buffer = new TerminalBuffer();
        
        Assert.Throws<ArgumentNullException>(() => new SelectorRenderer(buffer, null!));
    }

    [Fact]
    public void SelectorRenderer_ValidParameters_Constructs()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig();
        
        var renderer = new SelectorRenderer(buffer, config);
        
        Assert.NotNull(renderer);
    }

    #endregion

    #region CalculateTotalLines Tests

    [Fact]
    public void CalculateTotalLines_MinimalConfig()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig
        {
            MaxVisibleItems = 5,
            ShowSearch = false,
            ShowPreview = false,
            ShowFooter = false
        };
        var renderer = new SelectorRenderer(buffer, config);

        var lines = renderer.CalculateTotalLines();

        // Header (1) + Separator (1) + Items (5) = 7
        Assert.Equal(7, lines);
    }

    [Fact]
    public void CalculateTotalLines_WithSearch()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig
        {
            MaxVisibleItems = 5,
            ShowSearch = true,
            ShowPreview = false,
            ShowFooter = false
        };
        var renderer = new SelectorRenderer(buffer, config);

        var lines = renderer.CalculateTotalLines();

        // Header (1) + Search (1) + Separator (1) + Items (5) = 8
        Assert.Equal(8, lines);
    }

    [Fact]
    public void CalculateTotalLines_WithPreview()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig
        {
            MaxVisibleItems = 5,
            ShowSearch = false,
            ShowPreview = true,
            ShowFooter = false
        };
        var renderer = new SelectorRenderer(buffer, config);

        var lines = renderer.CalculateTotalLines();

        // Header (1) + Separator (1) + Items (5) + Preview (4) = 11
        Assert.Equal(11, lines);
    }

    [Fact]
    public void CalculateTotalLines_WithFooter()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig
        {
            MaxVisibleItems = 5,
            ShowSearch = false,
            ShowPreview = false,
            ShowFooter = true
        };
        var renderer = new SelectorRenderer(buffer, config);

        var lines = renderer.CalculateTotalLines();

        // Header (1) + Separator (1) + Items (5) + Footer (1) = 8
        Assert.Equal(8, lines);
    }

    [Fact]
    public void CalculateTotalLines_FullConfig()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig
        {
            MaxVisibleItems = 10,
            ShowSearch = true,
            ShowPreview = true,
            ShowFooter = true
        };
        var renderer = new SelectorRenderer(buffer, config);

        var lines = renderer.CalculateTotalLines();

        // Header (1) + Search (1) + Separator (1) + Items (10) + Preview (4) + Footer (1) = 18
        Assert.Equal(18, lines);
    }

    #endregion

    #region Render Tests

    [Fact]
    public void Render_EmptyItems_DoesNotThrow()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig { MaxVisibleItems = 5, ShowFooter = false };
        var renderer = new SelectorRenderer(buffer, config);

        var state = new RenderState
        {
            Items = Array.Empty<(string, int, ItemStatus, string?)>(),
            TotalCount = 0,
            FilteredCount = 0,
            SelectedIndex = 0,
            ScrollOffset = 0
        };

        // Should not throw
        renderer.Render(state, 0);
    }

    [Fact]
    public void Render_WithItems_DoesNotThrow()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig { MaxVisibleItems = 5, ShowFooter = false };
        var renderer = new SelectorRenderer(buffer, config);

        var items = new[]
        {
            ("Item 1", 0, ItemStatus.None, (string?)null),
            ("Item 2", 1, ItemStatus.Watched, "Preview 2"),
            ("Item 3", 2, ItemStatus.Downloaded, "Preview 3")
        };

        var state = new RenderState
        {
            Items = items,
            TotalCount = 3,
            FilteredCount = 3,
            SelectedIndex = 1,
            ScrollOffset = 0
        };

        // Should not throw
        renderer.Render(state, 0);
    }

    [Fact]
    public void Render_WithAllItemStatuses_DoesNotThrow()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig { MaxVisibleItems = 5, ShowFooter = false };
        var renderer = new SelectorRenderer(buffer, config);

        var items = new[]
        {
            ("None", 0, ItemStatus.None, (string?)null),
            ("Watched", 1, ItemStatus.Watched, null),
            ("Downloaded", 2, ItemStatus.Downloaded, null),
            ("InProgress", 3, ItemStatus.InProgress, null),
            ("New", 4, ItemStatus.New, null)
        };

        var state = new RenderState
        {
            Items = items,
            TotalCount = 5,
            FilteredCount = 5,
            SelectedIndex = 0,
            ScrollOffset = 0
        };

        // Should not throw
        renderer.Render(state, 0);
    }

    [Fact]
    public void Render_WithSearchText_DoesNotThrow()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig { MaxVisibleItems = 5, ShowSearch = true, ShowFooter = false };
        var renderer = new SelectorRenderer(buffer, config);

        var items = new[] { ("Demon Slayer", 0, ItemStatus.None, (string?)null) };

        var state = new RenderState
        {
            Items = items,
            TotalCount = 10,
            FilteredCount = 1,
            SelectedIndex = 0,
            ScrollOffset = 0,
            SearchText = "demon"
        };

        // Should not throw
        renderer.Render(state, 0);
    }

    [Fact]
    public void Render_WithPreview_DoesNotThrow()
    {
        using var buffer = new TerminalBuffer();
        var config = new RenderConfig { MaxVisibleItems = 5, ShowPreview = true, ShowFooter = false };
        var renderer = new SelectorRenderer(buffer, config);

        var items = new[]
        {
            ("Demon Slayer", 0, ItemStatus.None, "A young boy becomes a demon slayer to save his sister.")
        };

        var state = new RenderState
        {
            Items = items,
            TotalCount = 1,
            FilteredCount = 1,
            SelectedIndex = 0,
            ScrollOffset = 0
        };

        // Should not throw
        renderer.Render(state, 0);
    }

    #endregion
}
