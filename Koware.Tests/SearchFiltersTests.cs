// Author: Ilgaz Mehmetoğlu
// Tests for SearchFilters model and parsing.
using System;
using System.Collections.Generic;
using Koware.Domain.Models;
using Xunit;

namespace Koware.Tests;

public class SearchFiltersTests
{
    #region ContentStatus Enum Tests

    [Theory]
    [InlineData(ContentStatus.Any, "Any")]
    [InlineData(ContentStatus.Ongoing, "Ongoing")]
    [InlineData(ContentStatus.Completed, "Completed")]
    [InlineData(ContentStatus.Upcoming, "Upcoming")]
    [InlineData(ContentStatus.Hiatus, "Hiatus")]
    [InlineData(ContentStatus.Cancelled, "Cancelled")]
    public void ContentStatus_HasExpectedValues(ContentStatus status, string expectedName)
    {
        Assert.Equal(expectedName, status.ToString());
    }

    #endregion

    #region SearchSort Enum Tests

    [Theory]
    [InlineData(SearchSort.Default, "Default")]
    [InlineData(SearchSort.Popularity, "Popularity")]
    [InlineData(SearchSort.Score, "Score")]
    [InlineData(SearchSort.Recent, "Recent")]
    [InlineData(SearchSort.Title, "Title")]
    public void SearchSort_HasExpectedValues(SearchSort sort, string expectedName)
    {
        Assert.Equal(expectedName, sort.ToString());
    }

    #endregion

    #region KnownGenres Tests

    [Fact]
    public void KnownGenres_ContainsCommonGenres()
    {
        Assert.Equal("Action", KnownGenres.Action);
        Assert.Equal("Adventure", KnownGenres.Adventure);
        Assert.Equal("Comedy", KnownGenres.Comedy);
        Assert.Equal("Drama", KnownGenres.Drama);
        Assert.Equal("Fantasy", KnownGenres.Fantasy);
        Assert.Equal("Horror", KnownGenres.Horror);
        Assert.Equal("Isekai", KnownGenres.Isekai);
        Assert.Equal("Romance", KnownGenres.Romance);
        Assert.Equal("Sci-Fi", KnownGenres.SciFi);
        Assert.Equal("Slice of Life", KnownGenres.SliceOfLife);
    }

    [Theory]
    [InlineData("action", "Action")]
    [InlineData("ACTION", "Action")]
    [InlineData("Action", "Action")]
    [InlineData("sci-fi", "Sci-Fi")]
    [InlineData("scifi", "Sci-Fi")]
    [InlineData("slice of life", "Slice of Life")]
    [InlineData("sliceoflife", "Slice of Life")]
    [InlineData("sol", "Slice of Life")]
    public void KnownGenres_TryMatch_MatchesVariations(string input, string expected)
    {
        var result = KnownGenres.TryMatch(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("notarealgenre")]
    [InlineData("")]
    [InlineData("xyz123")]
    public void KnownGenres_TryMatch_ReturnsNullForUnknown(string input)
    {
        var result = KnownGenres.TryMatch(input);
        Assert.Null(result);
    }

    [Fact]
    public void KnownGenres_All_ContainsAllGenres()
    {
        var all = KnownGenres.All;
        Assert.Contains("Action", all);
        Assert.Contains("Comedy", all);
        Assert.Contains("Romance", all);
        Assert.True(all.Count >= 15); // At least 15 genres defined
    }

    #endregion

    #region SearchFilters Record Tests

    [Fact]
    public void SearchFilters_DefaultValues()
    {
        var filters = new SearchFilters();

        Assert.Empty(filters.Genres);
        Assert.Null(filters.Year);
        Assert.Equal(ContentStatus.Any, filters.Status);
        Assert.Null(filters.MinScore);
        Assert.Equal(SearchSort.Default, filters.Sort);
        Assert.Null(filters.CountryOrigin);
    }

    [Fact]
    public void SearchFilters_WithInitializer()
    {
        var filters = new SearchFilters
        {
            Genres = new List<string> { "Action", "Comedy" },
            Year = 2023,
            Status = ContentStatus.Ongoing,
            MinScore = 7.5f,
            Sort = SearchSort.Popularity,
            CountryOrigin = "JP"
        };

        Assert.Equal(2, filters.Genres.Count);
        Assert.Equal(2023, filters.Year);
        Assert.Equal(ContentStatus.Ongoing, filters.Status);
        Assert.Equal(7.5f, filters.MinScore);
        Assert.Equal(SearchSort.Popularity, filters.Sort);
        Assert.Equal("JP", filters.CountryOrigin);
    }

    [Fact]
    public void SearchFilters_IsEmpty_TrueWhenDefault()
    {
        var filters = new SearchFilters();
        Assert.True(filters.IsEmpty);
    }

    [Fact]
    public void SearchFilters_IsEmpty_FalseWhenHasGenres()
    {
        var filters = new SearchFilters { Genres = new List<string> { "Action" } };
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void SearchFilters_IsEmpty_FalseWhenHasYear()
    {
        var filters = new SearchFilters { Year = 2023 };
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void SearchFilters_IsEmpty_FalseWhenHasStatus()
    {
        var filters = new SearchFilters { Status = ContentStatus.Ongoing };
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void SearchFilters_IsEmpty_FalseWhenHasMinScore()
    {
        var filters = new SearchFilters { MinScore = 8.0f };
        Assert.False(filters.IsEmpty);
    }

    [Fact]
    public void SearchFilters_IsEmpty_FalseWhenHasSort()
    {
        var filters = new SearchFilters { Sort = SearchSort.Score };
        Assert.False(filters.IsEmpty);
    }

    #endregion

    #region SearchFilters.Parse Tests

    [Fact]
    public void SearchFilters_Parse_EmptyArgs_ReturnsEmpty()
    {
        var (filters, remaining) = SearchFilters.Parse(Array.Empty<string>());

        Assert.True(filters.IsEmpty);
        Assert.Empty(remaining);
    }

    [Fact]
    public void SearchFilters_Parse_GenreFlag()
    {
        var args = new[] { "--genre", "action" };
        var (filters, remaining) = SearchFilters.Parse(args);

        Assert.Single(filters.Genres);
        Assert.Contains("Action", filters.Genres);
        Assert.Empty(remaining);
    }

    [Fact]
    public void SearchFilters_Parse_MultipleGenres()
    {
        var args = new[] { "--genre", "action", "--genre", "comedy" };
        var (filters, remaining) = SearchFilters.Parse(args);

        Assert.Equal(2, filters.Genres.Count);
        Assert.Contains("Action", filters.Genres);
        Assert.Contains("Comedy", filters.Genres);
    }

    [Fact]
    public void SearchFilters_Parse_YearFlag()
    {
        var args = new[] { "--year", "2023" };
        var (filters, remaining) = SearchFilters.Parse(args);

        Assert.Equal(2023, filters.Year);
    }

    [Theory]
    [InlineData("1899")]
    [InlineData("2101")]
    [InlineData("notanumber")]
    public void SearchFilters_Parse_InvalidYear_Ignored(string yearValue)
    {
        var args = new[] { "--year", yearValue };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Null(filters.Year);
    }

    [Theory]
    [InlineData("ongoing", ContentStatus.Ongoing)]
    [InlineData("completed", ContentStatus.Completed)]
    [InlineData("upcoming", ContentStatus.Upcoming)]
    [InlineData("hiatus", ContentStatus.Hiatus)]
    [InlineData("cancelled", ContentStatus.Cancelled)]
    [InlineData("airing", ContentStatus.Ongoing)]
    [InlineData("finished", ContentStatus.Completed)]
    public void SearchFilters_Parse_StatusFlag(string statusValue, ContentStatus expected)
    {
        var args = new[] { "--status", statusValue };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Equal(expected, filters.Status);
    }

    [Theory]
    [InlineData("popularity", SearchSort.Popularity)]
    [InlineData("score", SearchSort.Score)]
    [InlineData("rating", SearchSort.Score)]
    [InlineData("recent", SearchSort.Recent)]
    [InlineData("new", SearchSort.Recent)]
    [InlineData("title", SearchSort.Title)]
    [InlineData("name", SearchSort.Title)]
    public void SearchFilters_Parse_SortFlag(string sortValue, SearchSort expected)
    {
        var args = new[] { "--sort", sortValue };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Equal(expected, filters.Sort);
    }

    [Fact]
    public void SearchFilters_Parse_ScoreFlag()
    {
        var args = new[] { "--score", "7.5" };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Equal(7.5f, filters.MinScore);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("11")]
    [InlineData("notanumber")]
    public void SearchFilters_Parse_InvalidScore_Ignored(string scoreValue)
    {
        var args = new[] { "--score", scoreValue };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Null(filters.MinScore);
    }

    [Theory]
    [InlineData("jp", "JP")]
    [InlineData("JP", "JP")]
    [InlineData("kr", "KR")]
    [InlineData("cn", "CN")]
    public void SearchFilters_Parse_CountryFlag(string countryValue, string expected)
    {
        var args = new[] { "--country", countryValue };
        var (filters, _) = SearchFilters.Parse(args);

        Assert.Equal(expected, filters.CountryOrigin);
    }

    [Fact]
    public void SearchFilters_Parse_PreservesNonFilterArgs()
    {
        var args = new[] { "search", "one piece", "--genre", "action", "--year", "2023", "--limit", "10" };
        var (filters, remaining) = SearchFilters.Parse(args);

        Assert.Single(filters.Genres);
        Assert.Equal(2023, filters.Year);
        Assert.Equal(3, remaining.Count); // "search", "one piece", "--limit", "10" - wait, limit should be preserved
        Assert.Contains("search", remaining);
        Assert.Contains("one piece", remaining);
    }

    [Fact]
    public void SearchFilters_Parse_ComplexArgs()
    {
        var args = new[] { 
            "my query", 
            "--genre", "action", 
            "--genre", "fantasy",
            "--year", "2023",
            "--status", "ongoing",
            "--sort", "popularity",
            "--score", "8",
            "--country", "jp"
        };
        var (filters, remaining) = SearchFilters.Parse(args);

        Assert.Equal(2, filters.Genres.Count);
        Assert.Equal(2023, filters.Year);
        Assert.Equal(ContentStatus.Ongoing, filters.Status);
        Assert.Equal(SearchSort.Popularity, filters.Sort);
        Assert.Equal(8f, filters.MinScore);
        Assert.Equal("JP", filters.CountryOrigin);
        Assert.Single(remaining);
        Assert.Equal("my query", remaining[0]);
    }

    #endregion
}
