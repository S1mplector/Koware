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
            // Usage: Koware.Reader <pagesJson> [title] [--referer <url>] [--user-agent <ua>]
            List<PageInfo> pages = new();
            string title = "Koware Reader";
            string? referer = null;
            string? userAgent = null;

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
                HttpUserAgent = userAgent
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
