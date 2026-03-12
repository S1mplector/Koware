using Koware.Cli.Downloads;
using Xunit;

namespace Koware.Tests;

public class DownloadPathHelpersTests
{
    [Theory]
    [InlineData("https://cdn.example.com/page001.jpg", ".jpg")]
    [InlineData("https://cdn.example.com/page001.JPG?token=abc", ".jpg")]
    [InlineData("https://cdn.example.com/page001.webp#fragment", ".webp")]
    [InlineData("  https://cdn.example.com/page001.png  ", ".png")]
    public void GetImageExtensionFromUrl_ExtractsNormalizedExtension(string url, string expected)
    {
        var extension = DownloadPathHelpers.GetImageExtensionFromUrl(url);

        Assert.Equal(expected, extension);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("https://cdn.example.com/image")]
    [InlineData("https://cdn.example.com/image.invalidext")]
    public void GetImageExtensionFromUrl_DefaultsToJpgForInvalidOrMissingExtension(string? url)
    {
        var extension = DownloadPathHelpers.GetImageExtensionFromUrl(url);

        Assert.Equal(".jpg", extension);
    }

    [Theory]
    [InlineData(1f, "Chapter_001")]
    [InlineData(12f, "Chapter_012")]
    [InlineData(123f, "Chapter_123")]
    public void BuildMangaChapterDirectoryName_UsesPaddedIntegerFormatForWholeNumbers(float chapter, string expected)
    {
        var name = DownloadPathHelpers.BuildMangaChapterDirectoryName(chapter);

        Assert.Equal(expected, name);
    }

    [Theory]
    [InlineData(12.5f, "Chapter_12_5")]
    [InlineData(57.25f, "Chapter_57_25")]
    [InlineData(100.125f, "Chapter_100_125")]
    public void BuildMangaChapterDirectoryName_PreservesFractionalChapterNumber(float chapter, string expected)
    {
        var name = DownloadPathHelpers.BuildMangaChapterDirectoryName(chapter);

        Assert.Equal(expected, name);
    }
}
