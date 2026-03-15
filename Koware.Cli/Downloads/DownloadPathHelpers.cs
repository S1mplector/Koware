using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Koware.Cli.Downloads;

internal static class DownloadPathHelpers
{
    private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg",
        ".jpeg",
        ".png",
        ".webp",
        ".gif",
        ".bmp",
        ".avif"
    };

    internal static string GetImageExtensionFromUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return ".jpg";
        }

        var trimmed = url.Trim();
        var separatorIndex = trimmed.IndexOfAny(['?', '#']);
        var path = separatorIndex >= 0 ? trimmed[..separatorIndex] : trimmed;
        var extension = Path.GetExtension(path);

        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 5)
        {
            return ".jpg";
        }

        return extension.StartsWith(".", StringComparison.Ordinal)
            ? extension.ToLowerInvariant()
            : "." + extension.ToLowerInvariant();
    }

    internal static string BuildMangaChapterDirectoryName(double chapterNumber)
    {
        var rounded = Math.Round(chapterNumber);
        if (Math.Abs(chapterNumber - rounded) < 0.0001d)
        {
            return $"Chapter_{(int)rounded:000}";
        }

        var normalized = chapterNumber
            .ToString("0.###", CultureInfo.InvariantCulture)
            .Replace('-', 'n')
            .Replace('.', '_');

        return $"Chapter_{normalized}";
    }

    internal static string BuildMangaChapterPdfFileName(double chapterNumber)
    {
        return $"{BuildMangaChapterDirectoryName(chapterNumber)}.pdf";
    }

    internal static string BuildMergedMangaPdfFileName(string title, double firstChapter, double lastChapter)
    {
        var sanitizedTitle = SanitizeFileName(title);
        var chapterLabel = firstChapter.Equals(lastChapter)
            ? $"Ch {DownloadDisplayFormatter.FormatNumber(firstChapter)}"
            : $"Ch {DownloadDisplayFormatter.FormatNumber(firstChapter)}-{DownloadDisplayFormatter.FormatNumber(lastChapter)}";
        return $"{sanitizedTitle} - {chapterLabel}.pdf";
    }

    internal static IReadOnlyList<string> EnumerateDownloadedPageFiles(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(rootPath)
            .Where(path => SupportedImageExtensions.Contains(Path.GetExtension(path)))
            .Select(path => new { Path = path, Number = ExtractFirstNumber(Path.GetFileNameWithoutExtension(path)) })
            .OrderBy(file => file.Number ?? int.MaxValue)
            .ThenBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(file => file.Path)
            .ToArray();
    }

    internal static int? ExtractFirstNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var start = -1;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsDigit(value[i]))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        var end = start + 1;
        while (end < value.Length && char.IsDigit(value[end]))
        {
            end++;
        }

        var slice = value[start..end];
        return int.TryParse(slice, out var number) ? number : null;
    }

    internal static string SanitizeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "download";
        }

        var invalid = Path.GetInvalidFileNameChars();
        var sanitized = new string(name.Where(c => !invalid.Contains(c)).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(sanitized) ? "download" : sanitized;
    }
}
