// Author: Ilgaz Mehmetoğlu
// Parses and stores command-line arguments for the Koware manga reader process.
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Koware.Reader.Win.Startup;

public sealed class ReaderArguments
{
    public ReaderArguments(IReadOnlyList<PageInfo> pages, string title, string? referer, string? userAgent, string? chaptersJson, string? navResultPath)
    {
        Pages = pages;
        Title = string.IsNullOrWhiteSpace(title) ? "Koware Reader" : title;
        Referer = referer;
        UserAgent = userAgent;
        ChaptersJson = chaptersJson;
        NavResultPath = navResultPath;
    }

    public IReadOnlyList<PageInfo> Pages { get; }

    public string Title { get; }

    public string? Referer { get; }

    public string? UserAgent { get; }
    
    public string? ChaptersJson { get; }
    
    public string? NavResultPath { get; }

    public static bool TryParse(string[] args, out ReaderArguments? parsed, out string? error)
    {
        parsed = null;
        error = null;

        if (args.Length == 0)
        {
            error = "Missing pages JSON argument.";
            return false;
        }

        // First argument is JSON array of page URLs or PageInfo objects
        var pagesJson = args[0];
        IReadOnlyList<PageInfo> pages;

        try
        {
            // Try parsing as array of PageInfo objects first
            var pageInfos = JsonSerializer.Deserialize<List<PageInfo>>(pagesJson, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (pageInfos is { Count: > 0 })
            {
                pages = pageInfos;
            }
            else
            {
                // Try parsing as simple string array of URLs
                var urls = JsonSerializer.Deserialize<List<string>>(pagesJson);
                if (urls is null || urls.Count == 0)
                {
                    error = "Pages JSON must be a non-empty array.";
                    return false;
                }

                pages = urls.Select((url, idx) => new PageInfo { Url = url, PageNumber = idx + 1 }).ToList();
            }
        }
        catch (JsonException ex)
        {
            error = $"Invalid pages JSON: {ex.Message}";
            return false;
        }

        var title = args.Length > 1 ? args[1] : "Koware Reader";
        string? referer = null;
        string? userAgent = null;
        string? chaptersJson = null;
        string? navResultPath = null;

        for (var i = 2; i < args.Length; i++)
        {
            var current = args[i];
            if (current.Equals("--referer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                referer = args[++i];
                continue;
            }

            if (current.Equals("--user-agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                userAgent = args[++i];
                continue;
            }

            if (current.Equals("--ua", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                userAgent = args[++i];
                continue;
            }
            
            if (current.Equals("--chapters", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                chaptersJson = args[++i];
                continue;
            }
            
            if (current.Equals("--nav", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                navResultPath = args[++i];
                continue;
            }

            error = $"Unrecognized argument '{current}'.";
            return false;
        }

        parsed = new ReaderArguments(pages, title, referer, userAgent, chaptersJson, navResultPath);
        return true;
    }
}

public sealed class PageInfo
{
    public string Url { get; set; } = string.Empty;
    public int PageNumber { get; set; }
}
