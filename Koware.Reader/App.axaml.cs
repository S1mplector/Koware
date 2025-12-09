// Author: Ilgaz Mehmetoğlu
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Koware.Reader;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var args = desktop.Args ?? Array.Empty<string>();
            
            // Parse command line arguments
            // Usage: Koware.Reader <pagesJson> [title] [--referer <url>] [--user-agent <ua>] [--chapters <json>] [--start-page <n>]
            List<PageInfo> pages = new();
            string title = "Koware Reader";
            string? referer = null;
            string? userAgent = null;
            List<ChapterInfo> chapters = new();
            string? navPath = null;
            int startPage = 1;

            for (int i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                
                if (arg.Equals("--referer", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    referer = args[++i];
                }
                else if (arg.Equals("--user-agent", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    userAgent = args[++i];
                }
                else if (arg.Equals("--nav", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    navPath = args[++i];
                }
                else if (arg.Equals("--start-page", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    if (int.TryParse(args[++i], out var sp) && sp > 0)
                    {
                        startPage = sp;
                    }
                }
                else if (arg.Equals("--chapters", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    var chapterJson = args[++i];
                    try
                    {
                        chapters = ChapterParser.ParseChapters(chapterJson);
                    }
                    catch
                    {
                        // ignore malformed chapter payloads
                    }
                }
                else if (arg.StartsWith("[") || arg.StartsWith("{"))
                {
                    // JSON pages array
                    try
                    {
                        pages = ParsePages(arg);
                    }
                    catch
                    {
                        // Invalid JSON, ignore
                    }
                }
                else if (pages.Count > 0 && !arg.StartsWith("--"))
                {
                    title = arg;
                }
            }

            desktop.MainWindow = new MainWindow
            {
                Pages = pages,
                Title = title,
                HttpReferer = referer,
                HttpUserAgent = userAgent,
                Chapters = chapters,
                NavResultPath = navPath,
                StartPage = startPage
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static List<PageInfo> ParsePages(string json)
    {
        var result = new List<PageInfo>();
        
        using var doc = JsonDocument.Parse(json);
        foreach (var element in doc.RootElement.EnumerateArray())
        {
            var url = element.GetProperty("url").GetString();
            var pageNumber = element.TryGetProperty("pageNumber", out var pn) ? pn.GetInt32() : result.Count + 1;
            
            if (!string.IsNullOrWhiteSpace(url))
            {
                result.Add(new PageInfo(pageNumber, url));
            }
        }
        
        return result;
    }
}

public record PageInfo(int PageNumber, string Url);

public record ChapterInfo(float Number, string? Title, bool IsRead);

public static class ChapterParser
{
    public static List<ChapterInfo> ParseChapters(string json)
    {
        var result = new List<ChapterInfo>();
        using var doc = JsonDocument.Parse(json);
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            // Chapter numbers can be floats (e.g., 6.5 for half chapters)
            float num = 0;
            if (el.TryGetProperty("number", out var n))
            {
                if (n.ValueKind == JsonValueKind.Number)
                {
                    num = (float)n.GetDouble();
                }
            }
            var title = el.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var isRead = el.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.True;
            if (num > 0)
            {
                result.Add(new ChapterInfo(num, title, isRead));
            }
        }

        return result;
    }
}
