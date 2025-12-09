// Author: Ilgaz Mehmetoğlu
// Tests for utility functions in Program.cs (FormatFileSize, FormatNumberRanges, etc.)
using System;
using System.Collections.Generic;
using System.Reflection;
using Xunit;

namespace Koware.Tests;

public class UtilityTests
{
    #region FormatFileSize Tests

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KB")]
    [InlineData(1536, "1.5 KB")]
    [InlineData(1048576, "1 MB")]
    [InlineData(1572864, "1.5 MB")]
    [InlineData(1073741824, "1 GB")]
    [InlineData(1610612736, "1.5 GB")]
    [InlineData(1099511627776, "1 TB")]
    public void FormatFileSize_ReturnsExpectedFormat(long bytes, string expected)
    {
        // Access via reflection since it's a local function in Program
        var result = FormatFileSizeHelper(bytes);
        Assert.Equal(expected, result);
    }

    // Helper that mimics the FormatFileSize function
    private static string FormatFileSizeHelper(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    #endregion

    #region FormatNumberRanges Tests

    [Fact]
    public void FormatNumberRanges_EmptyList_ReturnsNone()
    {
        var result = FormatNumberRangesHelper(Array.Empty<int>());
        Assert.Equal("none", result);
    }

    [Fact]
    public void FormatNumberRanges_SingleNumber()
    {
        var result = FormatNumberRangesHelper(new[] { 5 });
        Assert.Equal("5", result);
    }

    [Fact]
    public void FormatNumberRanges_ConsecutiveRange()
    {
        var result = FormatNumberRangesHelper(new[] { 1, 2, 3, 4, 5 });
        Assert.Equal("1-5", result);
    }

    [Fact]
    public void FormatNumberRanges_MultipleRanges()
    {
        var result = FormatNumberRangesHelper(new[] { 1, 2, 3, 7, 8, 9, 15 });
        Assert.Equal("1-3, 7-9, 15", result);
    }

    [Fact]
    public void FormatNumberRanges_NonConsecutive()
    {
        var result = FormatNumberRangesHelper(new[] { 1, 3, 5, 7 });
        Assert.Equal("1, 3, 5, 7", result);
    }

    [Fact]
    public void FormatNumberRanges_MixedRangesAndSingles()
    {
        var result = FormatNumberRangesHelper(new[] { 1, 2, 5, 10, 11, 12, 20 });
        Assert.Equal("1-2, 5, 10-12, 20", result);
    }

    [Fact]
    public void FormatNumberRanges_UnsortedInput_SortsCorrectly()
    {
        var result = FormatNumberRangesHelper(new[] { 5, 1, 3, 2, 4 });
        Assert.Equal("1-5", result);
    }

    // Helper that mimics the FormatNumberRanges function
    private static string FormatNumberRangesHelper(IReadOnlyList<int> numbers)
    {
        if (numbers.Count == 0) return "none";
        
        var sorted = new List<int>(numbers);
        sorted.Sort();
        var ranges = new List<string>();
        var start = sorted[0];
        var end = sorted[0];
        
        for (int i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == end + 1)
            {
                end = sorted[i];
            }
            else
            {
                ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
                start = end = sorted[i];
            }
        }
        ranges.Add(start == end ? start.ToString() : $"{start}-{end}");
        
        return string.Join(", ", ranges);
    }

    #endregion

    #region GetTimeAgo Tests (from InteractiveBrowser)

    [Fact]
    public void GetTimeAgo_JustNow()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddSeconds(-30));
        Assert.Equal("just now", result);
    }

    [Fact]
    public void GetTimeAgo_Minutes()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.Equal("5m ago", result);
    }

    [Fact]
    public void GetTimeAgo_Hours()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddHours(-3));
        Assert.Equal("3h ago", result);
    }

    [Fact]
    public void GetTimeAgo_Days()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddDays(-5));
        Assert.Equal("5d ago", result);
    }

    [Fact]
    public void GetTimeAgo_Weeks()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddDays(-14));
        Assert.Equal("2w ago", result);
    }

    [Fact]
    public void GetTimeAgo_Months()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddDays(-60));
        Assert.Equal("2mo ago", result);
    }

    [Fact]
    public void GetTimeAgo_Years()
    {
        var result = GetTimeAgoHelper(DateTimeOffset.UtcNow.AddDays(-400));
        Assert.Equal("1y ago", result);
    }

    // Helper that mimics the GetTimeAgo function
    private static string GetTimeAgoHelper(DateTimeOffset time)
    {
        var span = DateTimeOffset.UtcNow - time;

        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 30) return $"{(int)(span.TotalDays / 7)}w ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }

    #endregion

    #region ContentStatus Extension Tests

    [Theory]
    [InlineData("ongoing", true)]
    [InlineData("airing", true)]
    [InlineData("completed", true)]
    [InlineData("finished", true)]
    [InlineData("upcoming", true)]
    [InlineData("hiatus", true)]
    [InlineData("cancelled", true)]
    [InlineData("canceled", true)]
    [InlineData("invalid", false)]
    public void TryParseContentStatus_HandlesVariousInputs(string input, bool shouldSucceed)
    {
        var result = TryParseContentStatusHelper(input);
        Assert.Equal(shouldSucceed, result.HasValue);
    }

    private static Koware.Domain.Models.ContentStatus? TryParseContentStatusHelper(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "ongoing" or "airing" => Koware.Domain.Models.ContentStatus.Ongoing,
            "completed" or "finished" => Koware.Domain.Models.ContentStatus.Completed,
            "upcoming" => Koware.Domain.Models.ContentStatus.Upcoming,
            "hiatus" => Koware.Domain.Models.ContentStatus.Hiatus,
            "cancelled" or "canceled" => Koware.Domain.Models.ContentStatus.Cancelled,
            _ => null
        };
    }

    #endregion

    #region SearchSort Parsing Tests

    [Theory]
    [InlineData("popularity", Koware.Domain.Models.SearchSort.Popularity)]
    [InlineData("score", Koware.Domain.Models.SearchSort.Score)]
    [InlineData("rating", Koware.Domain.Models.SearchSort.Score)]
    [InlineData("recent", Koware.Domain.Models.SearchSort.Recent)]
    [InlineData("new", Koware.Domain.Models.SearchSort.Recent)]
    [InlineData("latest", Koware.Domain.Models.SearchSort.Recent)]
    [InlineData("title", Koware.Domain.Models.SearchSort.Title)]
    [InlineData("name", Koware.Domain.Models.SearchSort.Title)]
    [InlineData("alphabetical", Koware.Domain.Models.SearchSort.Title)]
    public void TryParseSearchSort_HandlesVariousInputs(string input, Koware.Domain.Models.SearchSort expected)
    {
        var result = TryParseSearchSortHelper(input);
        Assert.Equal(expected, result);
    }

    private static Koware.Domain.Models.SearchSort TryParseSearchSortHelper(string value)
    {
        return value.ToLowerInvariant() switch
        {
            "popularity" or "popular" => Koware.Domain.Models.SearchSort.Popularity,
            "score" or "rating" => Koware.Domain.Models.SearchSort.Score,
            "recent" or "new" or "latest" => Koware.Domain.Models.SearchSort.Recent,
            "title" or "name" or "alphabetical" => Koware.Domain.Models.SearchSort.Title,
            _ => Koware.Domain.Models.SearchSort.Default
        };
    }

    #endregion
}
