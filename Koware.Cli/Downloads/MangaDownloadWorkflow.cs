using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Koware.Application.Abstractions;
using Koware.Application.UseCases;
using Koware.Cli.Configuration;
using Koware.Cli.Console;
using Koware.Cli.History;
using Koware.Domain.Models;
using Koware.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Koware.Cli.Downloads;

internal static class MangaDownloadWorkflow
{
    internal static async Task<int> RunAsync(string[] args, IServiceProvider services, ILogger logger, DefaultCliOptions defaults, CancellationToken cancellationToken)
    {
        if (!TryParseOptions(args, logger, out var options))
        {
            return 1;
        }

        var mangaCatalog = services.GetRequiredService<IMangaCatalog>();
        var selectedManga = await SearchAndSelectMangaAsync(mangaCatalog, options, logger, cancellationToken);
        if (selectedManga is null)
        {
            return 1;
        }

        logger.LogInformation("Selected: {Title}", selectedManga.Title);

        var chaptersStep = ConsoleStep.Start("Fetching chapters");
        IReadOnlyCollection<Chapter> chapters;
        try
        {
            chapters = await mangaCatalog.GetChaptersAsync(selectedManga, cancellationToken);
            chaptersStep.Succeed($"Found {chapters.Count} chapter(s)");
        }
        catch (Exception)
        {
            chaptersStep.Fail("Failed to fetch chapters");
            throw;
        }

        if (chapters.Count == 0)
        {
            logger.LogWarning("No chapters found for {Title}.", selectedManga.Title);
            return 1;
        }

        var orderedChapters = chapters.OrderBy(c => c.Number).ToArray();
        var selectionSpec = options.SelectionSpec;
        if (string.IsNullOrWhiteSpace(selectionSpec) && !options.NonInteractive)
        {
            selectionSpec = PromptForChapterSelection(orderedChapters);
            if (string.IsNullOrWhiteSpace(selectionSpec))
            {
                logger.LogInformation("Download cancelled.");
                return 0;
            }
        }

        var targetChapters = DownloadPlanner.ResolveChapterSelection(selectionSpec, null, orderedChapters, logger);
        if (targetChapters.Count == 0)
        {
            logger.LogWarning("No chapters match the requested selection.");
            return 1;
        }

        var sanitizedTitle = DownloadPathHelpers.SanitizeFileName(selectedManga.Title);
        var targetDir = string.IsNullOrWhiteSpace(options.OutputDir)
            ? Path.Combine(defaults.GetMangaDownloadPath(), sanitizedTitle)
            : Path.Combine(options.OutputDir, sanitizedTitle);
        Directory.CreateDirectory(targetDir);

        var providerSlug = ResolveMangaProviderSlug(selectedManga.Id.Value);
        var allMangaOptions = services.GetService<IOptions<AllMangaOptions>>()?.Value;
        var nhentaiOptions = services.GetService<IOptions<NhentaiOptions>>()?.Value;
        var mangaDexOptions = services.GetService<IOptions<MangaDexOptions>>()?.Value;
        var httpReferrer = providerSlug switch
        {
            "nhentai" => nhentaiOptions?.EffectiveReferer,
            "mangadex" => mangaDexOptions?.Referer,
            _ => allMangaOptions?.Referer
        };
        var httpUserAgent = providerSlug switch
        {
            "nhentai" => nhentaiOptions?.UserAgent,
            "mangadex" => mangaDexOptions?.UserAgent,
            _ => allMangaOptions?.UserAgent
        };

        using var httpClient = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2) });
        if (!string.IsNullOrWhiteSpace(httpUserAgent))
        {
            httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(httpUserAgent);
        }
        if (!string.IsNullOrWhiteSpace(httpReferrer))
        {
            httpClient.DefaultRequestHeaders.Referrer = new Uri(httpReferrer);
        }

        var downloadStore = services.GetRequiredService<IDownloadStore>();
        var results = new List<ChapterDownloadResult>(targetChapters.Count);

        logger.LogInformation("Downloading {Count} chapter(s) to {Dir}", targetChapters.Count, targetDir);

        using (var progress = new MangaDownloadProgressRenderer(targetChapters.Count))
        {
            for (var index = 0; index < targetChapters.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var chapter = targetChapters[index];
                progress.StartChapter(index + 1, chapter.Number, 0);

                var result = await DownloadChapterAsync(
                    selectedManga,
                    chapter,
                    index + 1,
                    targetDir,
                    mangaCatalog,
                    httpClient,
                    httpReferrer,
                    httpUserAgent,
                    downloadStore,
                    progress,
                    options,
                    logger,
                    cancellationToken);

                results.Add(result);
                progress.CompleteChapter();
            }

            var completedCount = results.Count(r => r.State == DownloadState.Completed);
            var partialCount = results.Count(r => r.State == DownloadState.Partial);
            var failedCount = results.Count(r => r.State == DownloadState.Failed);
            progress.Complete($"{completedCount} complete, {partialCount} partial, {failedCount} failed");
        }

        var pdfDir = Path.Combine(targetDir, "pdf");
        var exportableResults = results.Where(r => r.CompletedPages > 0 && Directory.Exists(r.ChapterDirectory)).ToArray();
        var exportedPdfCount = 0;
        string? mergedPdfPath = null;
        var pdfExporter = new MangaPdfExporter(logger);

        if (options.ExportPerChapterPdf && exportableResults.Length > 0)
        {
            Directory.CreateDirectory(pdfDir);
            using var progressBar = new ConsoleProgressBar();
            for (var index = 0; index < exportableResults.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = exportableResults[index];
                progressBar.Report((index, exportableResults.Length, $"PDF ch {DownloadDisplayFormatter.FormatNumber(result.Chapter.Number)}"));

                try
                {
                    var outputPath = Path.Combine(pdfDir, DownloadPathHelpers.BuildMangaChapterPdfFileName(result.Chapter.Number));
                    pdfExporter.ExportChapterDirectory(result.ChapterDirectory, outputPath, cancellationToken);
                    exportedPdfCount++;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to export chapter {Chapter} to PDF.", result.Chapter.Number);
                }

                progressBar.Report((index + 1, exportableResults.Length, $"PDF ch {DownloadDisplayFormatter.FormatNumber(result.Chapter.Number)}"));
            }

            progressBar.Complete($"Created {exportedPdfCount} chapter PDF(s)");
        }

        if (options.ExportMergedPdf && exportableResults.Length > 0)
        {
            Directory.CreateDirectory(pdfDir);
            var firstChapter = exportableResults.First().Chapter.Number;
            var lastChapter = exportableResults.Last().Chapter.Number;
            mergedPdfPath = Path.Combine(pdfDir, DownloadPathHelpers.BuildMergedMangaPdfFileName(selectedManga.Title, firstChapter, lastChapter));

            var mergeStep = ConsoleStep.Start("Exporting merged PDF");
            try
            {
                pdfExporter.ExportChapterDirectories(exportableResults.Select(r => r.ChapterDirectory), mergedPdfPath, cancellationToken);
                mergeStep.Succeed(Path.GetFileName(mergedPdfPath));
            }
            catch (Exception ex)
            {
                mergeStep.Fail("Merged PDF export failed");
                logger.LogWarning(ex, "Failed to export merged PDF for {Title}.", selectedManga.Title);
                mergedPdfPath = null;
            }
        }

        var fullyCompleted = results.Count(r => r.State == DownloadState.Completed);
        var partial = results.Count(r => r.State == DownloadState.Partial);
        var failed = results.Count(r => r.State == DownloadState.Failed);
        var reused = results.Count(r => r.UsedExistingFiles);

        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Green;
        System.Console.WriteLine($"Download complete. Saved chapters to \"{targetDir}\".");
        System.Console.ResetColor();

        System.Console.WriteLine($"  Completed: {fullyCompleted}");
        System.Console.WriteLine($"  Partial:   {partial}");
        System.Console.WriteLine($"  Failed:    {failed}");
        if (reused > 0)
        {
            System.Console.WriteLine($"  Reused:    {reused}");
        }
        if (options.ExportPerChapterPdf)
        {
            System.Console.WriteLine($"  PDFs:      {exportedPdfCount}");
        }
        if (!string.IsNullOrWhiteSpace(mergedPdfPath))
        {
            System.Console.WriteLine($"  Merged:    {mergedPdfPath}");
        }

        return results.Any(r => r.State != DownloadState.Failed) ? 0 : 1;
    }

    private static async Task<ChapterDownloadResult> DownloadChapterAsync(
        Manga manga,
        Chapter chapter,
        int chapterIndex,
        string targetDir,
        IMangaCatalog mangaCatalog,
        HttpClient httpClient,
        string? httpReferrer,
        string? httpUserAgent,
        IDownloadStore downloadStore,
        MangaDownloadProgressRenderer progress,
        MangaDownloadCommandOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var chapterDir = Path.Combine(targetDir, DownloadPathHelpers.BuildMangaChapterDirectoryName(chapter.Number));
        Directory.CreateDirectory(chapterDir);

        try
        {
            var pages = (await mangaCatalog.GetPagesAsync(chapter, cancellationToken))
                .OrderBy(page => page.PageNumber)
                .ToArray();

            if (pages.Length == 0)
            {
                logger.LogWarning("No pages found for chapter {Chapter}.", chapter.Number);
                progress.StartChapter(chapterIndex, chapter.Number, 0);
                return new ChapterDownloadResult(chapter, chapterDir, DownloadState.Failed, 0, 0, 0, false);
            }

            var pageDownloads = pages
                .Select(page =>
                {
                    var ext = DownloadPathHelpers.GetImageExtensionFromUrl(page.ImageUrl.ToString());
                    var fileName = $"{page.PageNumber:000}{ext}";
                    var filePath = Path.Combine(chapterDir, fileName);
                    return new PageDownloadPlan(page, filePath);
                })
                .ToArray();

            var existingPages = options.Force
                ? 0
                : pageDownloads.Count(plan => File.Exists(plan.OutputPath));
            progress.StartChapter(chapterIndex, chapter.Number, pageDownloads.Length, existingPages);

            var completedPages = existingPages;
            var failedPages = 0;
            var missingPages = options.Force
                ? pageDownloads
                : pageDownloads.Where(plan => !File.Exists(plan.OutputPath)).ToArray();

            await Parallel.ForEachAsync(
                missingPages,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = options.MaxConcurrency,
                    CancellationToken = cancellationToken
                },
                async (pagePlan, token) =>
                {
                    var success = await httpClient.DownloadImageWithRetryAsync(
                        pagePlan.Page.ImageUrl,
                        pagePlan.OutputPath,
                        referer: httpReferrer,
                        userAgent: httpUserAgent,
                        maxRetries: 3,
                        logger: logger,
                        cancellationToken: token);

                    if (success)
                    {
                        Interlocked.Increment(ref completedPages);
                        progress.MarkPageCompleted();
                    }
                    else
                    {
                        Interlocked.Increment(ref failedPages);
                        progress.MarkPageFailed();
                        logger.LogWarning("Failed to download page {Page} of chapter {Chapter} after retries", pagePlan.Page.PageNumber, chapter.Number);
                    }
                });

            var state = completedPages switch
            {
                <= 0 => DownloadState.Failed,
                _ when completedPages >= pageDownloads.Length => DownloadState.Completed,
                _ => DownloadState.Partial
            };

            var chapterSize = Directory.Exists(chapterDir)
                ? new DirectoryInfo(chapterDir).EnumerateFiles().Sum(file => file.Length)
                : 0;

            await downloadStore.AddAsync(
                DownloadType.Chapter,
                manga.Id.Value,
                manga.Title,
                chapter.Number,
                null,
                chapterDir,
                chapterSize,
                state,
                completedPages,
                pageDownloads.Length,
                cancellationToken);

            if (failedPages > 0)
            {
                logger.LogWarning("Chapter {Chapter}: {Failed}/{Total} pages failed", chapter.Number, failedPages, pageDownloads.Length);
            }

            var usedExistingFiles = !options.Force && existingPages == pageDownloads.Length;
            return new ChapterDownloadResult(chapter, chapterDir, state, completedPages, failedPages, pageDownloads.Length, usedExistingFiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Failed to download chapter {Chapter}", chapter.Number);
            return new ChapterDownloadResult(chapter, chapterDir, DownloadState.Failed, 0, 0, 0, false);
        }
    }

    private static async Task<Manga?> SearchAndSelectMangaAsync(IMangaCatalog mangaCatalog, MangaDownloadCommandOptions options, ILogger logger, CancellationToken cancellationToken)
    {
        var searchStep = ConsoleStep.Start("Searching manga");
        IReadOnlyCollection<Manga> results;
        try
        {
            results = await mangaCatalog.SearchAsync(options.Query, cancellationToken);
            searchStep.Succeed($"Found {results.Count} result(s)");
        }
        catch (Exception)
        {
            searchStep.Fail("Search failed");
            throw;
        }

        if (results.Count == 0)
        {
            logger.LogWarning("No manga found for query: {Query}", options.Query);
            return null;
        }

        if (options.PreferredIndex.HasValue)
        {
            var index = options.PreferredIndex.Value - 1;
            if (index < 0 || index >= results.Count)
            {
                logger.LogWarning("Index {Index} is out of range (1-{Count}).", options.PreferredIndex.Value, results.Count);
                return null;
            }

            return results.ElementAt(index);
        }

        if (results.Count == 1 || options.NonInteractive)
        {
            return results.First();
        }

        var selectResult = InteractiveSelect.Show(
            results.ToList(),
            manga => manga.Title,
            new SelectorOptions<Manga>
            {
                Prompt = "Select manga to download",
                MaxVisibleItems = 10,
                ShowSearch = true,
                SecondaryDisplayFunc = manga => manga.Synopsis ?? string.Empty
            });

        if (selectResult.Cancelled)
        {
            logger.LogWarning("Selection cancelled.");
            return null;
        }

        return selectResult.Selected!;
    }

    private static bool TryParseOptions(string[] args, ILogger logger, out MangaDownloadCommandOptions options)
    {
        options = new MangaDownloadCommandOptions(string.Empty, null, null, null, false, false, false, false, 4);

        var queryParts = new List<string>();
        string? selectionSpec = null;
        string? outputDir = null;
        int? preferredIndex = null;
        var nonInteractive = false;
        var exportPerChapterPdf = false;
        var exportMergedPdf = false;
        var force = false;
        var maxConcurrency = 4;

        for (var i = 1; i < args.Length; i++)
        {
            var arg = args[i];
            if ((arg.Equals("--chapter", StringComparison.OrdinalIgnoreCase) || arg.Equals("--chapters", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
            {
                selectionSpec = args[++i];
                continue;
            }

            if (arg.Equals("--index", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out var idx) || idx < 1)
                {
                    logger.LogWarning("--index must be a positive integer.");
                    return false;
                }

                preferredIndex = idx;
                continue;
            }

            if (arg.Equals("--dir", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                outputDir = args[++i];
                continue;
            }

            if (arg.Equals("--concurrency", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                if (!int.TryParse(args[++i], out maxConcurrency) || maxConcurrency < 1)
                {
                    logger.LogWarning("--concurrency must be a positive integer.");
                    return false;
                }

                maxConcurrency = Math.Min(maxConcurrency, 12);
                continue;
            }

            if (arg.Equals("--pdf", StringComparison.OrdinalIgnoreCase))
            {
                exportPerChapterPdf = true;
                continue;
            }

            if (arg.Equals("--merge-pdf", StringComparison.OrdinalIgnoreCase))
            {
                exportMergedPdf = true;
                continue;
            }

            if (arg.Equals("--force", StringComparison.OrdinalIgnoreCase))
            {
                force = true;
                continue;
            }

            if (arg.Equals("--non-interactive", StringComparison.OrdinalIgnoreCase))
            {
                nonInteractive = true;
                continue;
            }

            queryParts.Add(arg);
        }

        if (queryParts.Count == 0)
        {
            logger.LogWarning("download command is missing a search query.");
            System.Console.ForegroundColor = ConsoleColor.Cyan;
            System.Console.WriteLine("Usage: koware download <query> [--chapter <n|n-m|list|all>] [--dir <path>] [--index <n>] [--pdf] [--merge-pdf] [--concurrency <n>] [--force] [--non-interactive]");
            System.Console.ResetColor();
            return false;
        }

        options = new MangaDownloadCommandOptions(
            string.Join(' ', queryParts).Trim(),
            selectionSpec,
            outputDir,
            preferredIndex,
            nonInteractive,
            exportPerChapterPdf,
            exportMergedPdf,
            force,
            maxConcurrency);
        return true;
    }

    private static string? PromptForChapterSelection(IReadOnlyList<Chapter> chapters)
    {
        System.Console.WriteLine();
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.WriteLine($"Available chapters: {DownloadDisplayFormatter.FormatNumber(chapters.First().Number)} - {DownloadDisplayFormatter.FormatNumber(chapters.Last().Number)} ({chapters.Count} total)");
        System.Console.ResetColor();
        System.Console.Write("Enter chapter(s) to download (e.g., 1, 1-10, 1,3,5-7, all): ");
        return System.Console.ReadLine()?.Trim();
    }

    private static string ResolveMangaProviderSlug(string mangaId)
    {
        if (string.IsNullOrWhiteSpace(mangaId))
        {
            return string.Empty;
        }

        var delimiterIndex = mangaId.IndexOf(':');
        if (delimiterIndex <= 0)
        {
            return string.Empty;
        }

        return mangaId[..delimiterIndex].Trim().ToLowerInvariant();
    }

    private sealed record MangaDownloadCommandOptions(
        string Query,
        string? SelectionSpec,
        string? OutputDir,
        int? PreferredIndex,
        bool NonInteractive,
        bool ExportPerChapterPdf,
        bool ExportMergedPdf,
        bool Force,
        int MaxConcurrency);

    private sealed record PageDownloadPlan(ChapterPage Page, string OutputPath);

    private sealed record ChapterDownloadResult(
        Chapter Chapter,
        string ChapterDirectory,
        DownloadState State,
        int CompletedPages,
        int FailedPages,
        int TotalPages,
        bool UsedExistingFiles);
}
