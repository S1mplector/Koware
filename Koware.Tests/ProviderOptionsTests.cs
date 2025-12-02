// Author: Ilgaz Mehmetoğlu
// Tests for provider configuration options (AllAnime, GogoAnime, AllManga).
using Koware.Infrastructure.Configuration;
using Xunit;

namespace Koware.Tests;

public class ProviderOptionsTests
{
    #region GogoAnimeOptions Tests

    [Fact]
    public void GogoAnimeOptions_IsConfigured_ReturnsFalse_WhenNotEnabled()
    {
        var options = new GogoAnimeOptions
        {
            Enabled = false,
            ApiBase = "https://api.example.com",
            SiteBase = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void GogoAnimeOptions_IsConfigured_ReturnsFalse_WhenApiBaseMissing()
    {
        var options = new GogoAnimeOptions
        {
            Enabled = true,
            ApiBase = null,
            SiteBase = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void GogoAnimeOptions_IsConfigured_ReturnsFalse_WhenSiteBaseMissing()
    {
        var options = new GogoAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            SiteBase = null
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void GogoAnimeOptions_IsConfigured_ReturnsTrue_WhenFullyConfigured()
    {
        var options = new GogoAnimeOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            SiteBase = "https://example.com"
        };
        
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void GogoAnimeOptions_DefaultsToDisabled()
    {
        var options = new GogoAnimeOptions();
        
        Assert.False(options.Enabled);
        Assert.Null(options.ApiBase);
        Assert.Null(options.SiteBase);
        Assert.False(options.IsConfigured);
    }

    #endregion

    #region AllMangaOptions Tests

    [Fact]
    public void AllMangaOptions_IsConfigured_ReturnsFalse_WhenNotEnabled()
    {
        var options = new AllMangaOptions
        {
            Enabled = false,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void AllMangaOptions_IsConfigured_ReturnsFalse_WhenApiBaseMissing()
    {
        var options = new AllMangaOptions
        {
            Enabled = true,
            ApiBase = null,
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void AllMangaOptions_IsConfigured_ReturnsFalse_WhenBaseHostMissing()
    {
        var options = new AllMangaOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = null,
            Referer = "https://example.com"
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void AllMangaOptions_IsConfigured_ReturnsFalse_WhenRefererMissing()
    {
        var options = new AllMangaOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = null
        };
        
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void AllMangaOptions_IsConfigured_ReturnsTrue_WhenFullyConfigured()
    {
        var options = new AllMangaOptions
        {
            Enabled = true,
            ApiBase = "https://api.example.com",
            BaseHost = "example.com",
            Referer = "https://example.com"
        };
        
        Assert.True(options.IsConfigured);
    }

    [Fact]
    public void AllMangaOptions_DefaultsToDisabled()
    {
        var options = new AllMangaOptions();
        
        Assert.False(options.Enabled);
        Assert.Null(options.ApiBase);
        Assert.Null(options.BaseHost);
        Assert.Null(options.Referer);
        Assert.False(options.IsConfigured);
    }

    #endregion

    #region AllAnimeOptions Tests

    [Fact]
    public void AllAnimeOptions_DefaultsToDisabled()
    {
        var options = new AllAnimeOptions();
        
        Assert.False(options.Enabled);
        Assert.Null(options.ApiBase);
        Assert.Null(options.BaseHost);
        Assert.Null(options.Referer);
        Assert.False(options.IsConfigured);
    }

    [Fact]
    public void AllAnimeOptions_HasDefaultTranslationType()
    {
        var options = new AllAnimeOptions();
        
        Assert.Equal("sub", options.TranslationType);
    }

    [Fact]
    public void AllAnimeOptions_HasDefaultSearchLimit()
    {
        var options = new AllAnimeOptions();
        
        Assert.Equal(20, options.SearchLimit);
    }

    #endregion
}
