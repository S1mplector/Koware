// Author: Ilgaz Mehmetoğlu
// Tests for DownloadPlanner episode selection and filename generation.
using System;
using System.Collections.Generic;
using Koware.Application.UseCases;
using Koware.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Koware.Tests;

public class DownloadPlannerTests
{
    [Fact]
    public void ResolveEpisodeSelection_NoEpisodes_ReturnsEmpty()
    {
        var result = DownloadPlanner.ResolveEpisodeSelection(null, null, Array.Empty<Episode>(), NullLogger.Instance);
        Assert.Empty(result);
    }

    [Fact]
    public void ResolveEpisodeSelection_NoArg_UsesSingleEpisodeNumber()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1")),
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection(null, 2, episodes, NullLogger.Instance);

        var ep = Assert.Single(result);
        Assert.Equal(2, ep.Number);
    }

    [Fact]
    public void ResolveEpisodeSelection_NoArg_NoNumber_PicksFirstByNumber()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2")),
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection(null, null, episodes, NullLogger.Instance);

        var ep = Assert.Single(result);
        Assert.Equal(1, ep.Number);
    }

    [Fact]
    public void ResolveEpisodeSelection_All_ReturnsAllOrdered()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-3"), "Episode 3", 3, new Uri("https://example.com/3")),
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1")),
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection("all", null, episodes, NullLogger.Instance);

        Assert.Collection(result,
            e => Assert.Equal(1, e.Number),
            e => Assert.Equal(2, e.Number),
            e => Assert.Equal(3, e.Number));
    }

    [Fact]
    public void ResolveEpisodeSelection_Range_IncludesWithinBounds()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1")),
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2")),
            new(new EpisodeId("ep-3"), "Episode 3", 3, new Uri("https://example.com/3")),
            new(new EpisodeId("ep-4"), "Episode 4", 4, new Uri("https://example.com/4"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection("2-3", null, episodes, NullLogger.Instance);

        Assert.Collection(result,
            e => Assert.Equal(2, e.Number),
            e => Assert.Equal(3, e.Number));
    }

    [Fact]
    public void ResolveEpisodeSelection_RangeReversed_Normalizes()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1")),
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2")),
            new(new EpisodeId("ep-3"), "Episode 3", 3, new Uri("https://example.com/3"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection("3-1", null, episodes, NullLogger.Instance);

        Assert.Collection(result,
            e => Assert.Equal(1, e.Number),
            e => Assert.Equal(2, e.Number),
            e => Assert.Equal(3, e.Number));
    }

    [Fact]
    public void ResolveEpisodeSelection_SingleValue_ReturnsThatEpisode()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1")),
            new(new EpisodeId("ep-2"), "Episode 2", 2, new Uri("https://example.com/2"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection("2", null, episodes, NullLogger.Instance);

        var ep = Assert.Single(result);
        Assert.Equal(2, ep.Number);
    }

    [Fact]
    public void ResolveEpisodeSelection_InvalidSpec_ReturnsEmpty()
    {
        var episodes = new List<Episode>
        {
            new(new EpisodeId("ep-1"), "Episode 1", 1, new Uri("https://example.com/1"))
        };

        var result = DownloadPlanner.ResolveEpisodeSelection("foo-bar", null, episodes, NullLogger.Instance);

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("My Show", "Episode 1", 1, null, "My Show - Ep 001 - Episode 1.mp4")]
    [InlineData("My/Show", "Episode: 1", 1, "1080p", "My_Show - Ep 001 - Episode_ 1 [1080p].mp4")]
    [InlineData("   ", "   ", 5, "720p", "untitled - Ep 005 - Episode 5 [720p].mp4")]
    public void BuildDownloadFileName_ProducesExpected(string animeTitle, string episodeTitle, int number, string? quality, string expected)
    {
        var episode = new Episode(new EpisodeId("id"), episodeTitle, number, new Uri("https://example.com/ep"));

        var name = DownloadPlanner.BuildDownloadFileName(animeTitle, episode, quality);

        Assert.Equal(expected, name);
    }
}
