using System;
using System.Globalization;
using System.IO;

namespace Koware.Cli.Downloads;

internal static class DownloadPathHelpers
{
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

    internal static string BuildMangaChapterDirectoryName(float chapterNumber)
    {
        var rounded = MathF.Round(chapterNumber);
        if (MathF.Abs(chapterNumber - rounded) < 0.0001f)
        {
            return $"Chapter_{(int)rounded:000}";
        }

        var normalized = chapterNumber
            .ToString("0.###", CultureInfo.InvariantCulture)
            .Replace('-', 'n')
            .Replace('.', '_');

        return $"Chapter_{normalized}";
    }
}
