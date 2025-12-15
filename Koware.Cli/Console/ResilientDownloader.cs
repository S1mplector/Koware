// Author: Ilgaz Mehmetoğlu
// Resilient download engine with retry logic, resume support, and timeout handling.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Koware.Cli.Console;

/// <summary>
/// Configuration options for resilient downloads.
/// </summary>
public sealed class DownloadOptions
{
    /// <summary>Maximum number of retry attempts for failed downloads.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>Initial delay between retries (doubles with each retry).</summary>
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Maximum delay between retries.</summary>
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Timeout for individual download attempts.</summary>
    public TimeSpan AttemptTimeout { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Whether to attempt resuming partial downloads.</summary>
    public bool EnableResume { get; init; } = true;

    /// <summary>Minimum file size to attempt resume (smaller files just restart).</summary>
    public long MinResumeSize { get; init; } = 1024 * 100; // 100KB

    /// <summary>Buffer size for streaming downloads.</summary>
    public int BufferSize { get; init; } = 81920;

    /// <summary>Optional HTTP Referer header.</summary>
    public string? Referer { get; init; }

    /// <summary>Optional User-Agent header.</summary>
    public string? UserAgent { get; init; }

    /// <summary>Whether to show progress bar during download.</summary>
    public bool ShowProgress { get; init; } = true;

    /// <summary>Default download options.</summary>
    public static DownloadOptions Default => new();
}

/// <summary>
/// Result of a download operation.
/// </summary>
public sealed class DownloadResult
{
    public bool Success { get; init; }
    public string? FilePath { get; init; }
    public long BytesDownloaded { get; init; }
    public int AttemptsUsed { get; init; }
    public string? ErrorMessage { get; init; }
    public Exception? Exception { get; init; }

    public static DownloadResult Succeeded(string filePath, long bytes, int attempts) => new()
    {
        Success = true,
        FilePath = filePath,
        BytesDownloaded = bytes,
        AttemptsUsed = attempts
    };

    public static DownloadResult Failed(string error, Exception? ex = null, int attempts = 0) => new()
    {
        Success = false,
        ErrorMessage = error,
        Exception = ex,
        AttemptsUsed = attempts
    };
}

/// <summary>
/// Resilient HTTP downloader with retry logic, exponential backoff, resume support, and progress tracking.
/// </summary>
public sealed class ResilientDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ILogger? _logger;

    public ResilientDownloader(HttpClient httpClient, ILogger? logger = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger;
    }

    /// <summary>
    /// Download a file with resilience features (retry, resume, timeout).
    /// </summary>
    /// <param name="url">URL to download from.</param>
    /// <param name="outputPath">Destination file path.</param>
    /// <param name="options">Download options.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Download result indicating success/failure.</returns>
    public async Task<DownloadResult> DownloadFileAsync(
        Uri url,
        string outputPath,
        DownloadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DownloadOptions.Default;
        var attempt = 0;
        Exception? lastException = null;
        var retryDelay = options.InitialRetryDelay;

        while (attempt < options.MaxRetries)
        {
            attempt++;
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var result = await TryDownloadAsync(url, outputPath, options, attempt, cancellationToken);
                if (result.Success)
                {
                    return result;
                }

                lastException = result.Exception;
                _logger?.LogDebug("Download attempt {Attempt}/{MaxAttempts} failed: {Error}",
                    attempt, options.MaxRetries, result.ErrorMessage);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                _logger?.LogDebug("Download attempt {Attempt}/{MaxAttempts} timed out", attempt, options.MaxRetries);
            }
            catch (HttpRequestException ex)
            {
                lastException = ex;
                _logger?.LogDebug(ex, "Download attempt {Attempt}/{MaxAttempts} failed with HTTP error",
                    attempt, options.MaxRetries);
            }
            catch (IOException ex)
            {
                lastException = ex;
                _logger?.LogDebug(ex, "Download attempt {Attempt}/{MaxAttempts} failed with IO error",
                    attempt, options.MaxRetries);
            }

            if (attempt < options.MaxRetries)
            {
                _logger?.LogDebug("Waiting {Delay}ms before retry...", retryDelay.TotalMilliseconds);
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, options.MaxRetryDelay.TotalMilliseconds));
            }
        }

        return DownloadResult.Failed(
            lastException?.Message ?? "Download failed after all retries",
            lastException,
            attempt);
    }

    /// <summary>
    /// Download multiple files concurrently with resilience.
    /// </summary>
    /// <param name="downloads">Collection of (url, outputPath) pairs.</param>
    /// <param name="maxConcurrency">Maximum concurrent downloads.</param>
    /// <param name="options">Download options.</param>
    /// <param name="onProgress">Optional callback for progress updates (completed, total, currentFile).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Collection of download results.</returns>
    public async Task<IReadOnlyList<DownloadResult>> DownloadFilesAsync(
        IReadOnlyList<(Uri Url, string OutputPath)> downloads,
        int maxConcurrency = 3,
        DownloadOptions? options = null,
        Action<int, int, string>? onProgress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= DownloadOptions.Default;
        var results = new DownloadResult[downloads.Count];
        var completed = 0;

        using var semaphore = new SemaphoreSlim(maxConcurrency);

        var tasks = downloads.Select(async (download, index) =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var result = await DownloadFileAsync(download.Url, download.OutputPath, options, cancellationToken);
                results[index] = result;

                var currentCompleted = Interlocked.Increment(ref completed);
                onProgress?.Invoke(currentCompleted, downloads.Count, Path.GetFileName(download.OutputPath));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return results;
    }

    private async Task<DownloadResult> TryDownloadAsync(
        Uri url,
        string outputPath,
        DownloadOptions options,
        int attempt,
        CancellationToken cancellationToken)
    {
        using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attemptCts.CancelAfter(options.AttemptTimeout);
        var token = attemptCts.Token;

        // Check for existing partial download
        long existingBytes = 0;
        var tempPath = outputPath + ".part";

        if (options.EnableResume && File.Exists(tempPath))
        {
            var fileInfo = new FileInfo(tempPath);
            if (fileInfo.Length >= options.MinResumeSize)
            {
                existingBytes = fileInfo.Length;
                _logger?.LogDebug("Found partial download ({Bytes} bytes), attempting resume", existingBytes);
            }
            else
            {
                File.Delete(tempPath);
            }
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        // Add headers
        if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer, UriKind.Absolute, out var refUri))
        {
            request.Headers.Referrer = refUri;
        }

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);
        }

        // Add range header for resume
        if (existingBytes > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingBytes, null);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

        // Handle resume response
        var isResume = response.StatusCode == HttpStatusCode.PartialContent && existingBytes > 0;
        if (!isResume && existingBytes > 0)
        {
            // Server doesn't support resume, start over
            existingBytes = 0;
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }

        if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.PartialContent)
        {
            return DownloadResult.Failed($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}", attempts: attempt);
        }

        var contentLength = response.Content.Headers.ContentLength;
        var totalBytes = isResume ? existingBytes + (contentLength ?? 0) : contentLength ?? 0;

        await using var responseStream = await response.Content.ReadAsStreamAsync(token);

        // Open file for append (resume) or create (new)
        var fileMode = isResume ? FileMode.Append : FileMode.Create;
        await using var fileStream = new FileStream(tempPath, fileMode, FileAccess.Write, FileShare.None, options.BufferSize, useAsync: true);

        var buffer = new byte[options.BufferSize];
        long bytesRead = existingBytes;
        int read;

        // Create progress bar if enabled and we know total size
        using var progressBar = options.ShowProgress && totalBytes > 0
            ? new ConsoleProgressBar("Downloading", totalBytes)
            : null;

        var lastReportTime = DateTime.UtcNow;

        while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), token);
            bytesRead += read;

            // Report progress at most every 100ms
            var now = DateTime.UtcNow;
            if (progressBar is not null && (now - lastReportTime).TotalMilliseconds >= 100)
            {
                progressBar.Report(bytesRead);
                lastReportTime = now;
            }
        }

        progressBar?.Complete("Download complete");

        // Verify download completed (if we knew the expected size)
        if (totalBytes > 0 && bytesRead < totalBytes)
        {
            return DownloadResult.Failed($"Incomplete download: {bytesRead}/{totalBytes} bytes", attempts: attempt);
        }

        // Move temp file to final destination
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
        File.Move(tempPath, outputPath);

        return DownloadResult.Succeeded(outputPath, bytesRead, attempt);
    }
}

/// <summary>
/// Extension methods for resilient image downloads (manga pages).
/// </summary>
public static class ResilientImageDownloader
{
    /// <summary>
    /// Download an image with retry logic optimized for manga pages.
    /// </summary>
    public static async Task<bool> DownloadImageWithRetryAsync(
        this HttpClient httpClient,
        Uri imageUrl,
        string outputPath,
        string? referer = null,
        string? userAgent = null,
        int maxRetries = 3,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var retryDelay = TimeSpan.FromMilliseconds(500);

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, imageUrl);

                if (!string.IsNullOrWhiteSpace(referer))
                {
                    request.Headers.Referrer = new Uri(referer);
                }

                if (!string.IsNullOrWhiteSpace(userAgent))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                }

                using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                attemptCts.CancelAfter(TimeSpan.FromSeconds(30));

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attemptCts.Token);
                response.EnsureSuccessStatusCode();

                var bytes = await response.Content.ReadAsByteArrayAsync(attemptCts.Token);

                // Validate image data (basic check for common image headers)
                if (bytes.Length < 8)
                {
                    throw new InvalidDataException("Downloaded data too small to be a valid image");
                }

                await File.WriteAllBytesAsync(outputPath, bytes, cancellationToken);
                return true;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogDebug("Image download timed out on attempt {Attempt}/{Max}: {Url}", attempt, maxRetries, imageUrl);
            }
            catch (HttpRequestException ex)
            {
                logger?.LogDebug(ex, "Image download failed on attempt {Attempt}/{Max}: {Url}", attempt, maxRetries, imageUrl);
            }
            catch (IOException ex)
            {
                logger?.LogDebug(ex, "Image write failed on attempt {Attempt}/{Max}: {Url}", attempt, maxRetries, imageUrl);
            }
            catch (InvalidDataException ex)
            {
                logger?.LogDebug(ex, "Invalid image data on attempt {Attempt}/{Max}: {Url}", attempt, maxRetries, imageUrl);
            }

            if (attempt < maxRetries)
            {
                await Task.Delay(retryDelay, cancellationToken);
                retryDelay = TimeSpan.FromMilliseconds(Math.Min(retryDelay.TotalMilliseconds * 2, 5000));
            }
        }

        return false;
    }
}
