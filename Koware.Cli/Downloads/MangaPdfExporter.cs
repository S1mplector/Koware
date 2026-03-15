using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace Koware.Cli.Downloads;

internal sealed record PdfExportResult(string OutputPath, int PageCount);

internal sealed class MangaPdfExporter
{
    private readonly ILogger? _logger;

    internal MangaPdfExporter(ILogger? logger = null)
    {
        _logger = logger;
    }

    internal PdfExportResult ExportChapterDirectory(string chapterDirectory, string outputPath, CancellationToken cancellationToken = default)
    {
        var pageFiles = DownloadPathHelpers.EnumerateDownloadedPageFiles(chapterDirectory);
        if (pageFiles.Count == 0)
        {
            throw new InvalidOperationException($"No page images found in '{chapterDirectory}'.");
        }

        return ExportPages(pageFiles, outputPath, cancellationToken);
    }

    internal PdfExportResult ExportChapterDirectories(IEnumerable<string> chapterDirectories, string outputPath, CancellationToken cancellationToken = default)
    {
        var pageFiles = chapterDirectories
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(DownloadPathHelpers.EnumerateDownloadedPageFiles)
            .ToArray();

        if (pageFiles.Length == 0)
        {
            throw new InvalidOperationException("No page images found for the selected chapters.");
        }

        return ExportPages(pageFiles, outputPath, cancellationToken);
    }

    private PdfExportResult ExportPages(IReadOnlyList<string> pageFiles, string outputPath, CancellationToken cancellationToken)
    {
        var outputDirectory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        using var stream = File.Open(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var document = SKDocument.CreatePdf(stream);

        var pageCount = 0;
        foreach (var pageFile in pageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var bitmap = SKBitmap.Decode(pageFile);
            if (bitmap is null)
            {
                _logger?.LogWarning("Skipping unsupported image while exporting PDF: {Path}", pageFile);
                continue;
            }

            using var image = SKImage.FromBitmap(bitmap);
            using var canvas = document.BeginPage(bitmap.Width, bitmap.Height);
            canvas.Clear(SKColors.White);
            canvas.DrawImage(image, 0, 0);
            document.EndPage();
            pageCount++;
        }

        document.Close();

        if (pageCount == 0)
        {
            try
            {
                File.Delete(outputPath);
            }
            catch
            {
                // Ignore cleanup failures on empty exports.
            }

            throw new InvalidOperationException("No supported page images could be exported to PDF.");
        }

        return new PdfExportResult(outputPath, pageCount);
    }
}
