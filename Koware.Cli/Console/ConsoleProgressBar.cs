// Author: Ilgaz Mehmetoğlu
// Reusable console progress bar for displaying download/operation progress.
using System;

namespace Koware.Cli.Console;

/// <summary>
/// A reusable console progress bar that displays visual progress with percentage,
/// transfer speed, and ETA. Can be used for downloads, diagnostics, or any long-running operation.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// using var progressBar = new ConsoleProgressBar("Downloading", totalBytes);
/// progressBar.Report(bytesDownloaded);
/// // ... or use as IProgress&lt;long&gt;
/// progressBar.Complete("Download complete");
/// </code>
/// </remarks>
internal sealed class ConsoleProgressBar : IProgress<long>, IProgress<(int current, int total, string label)>, IDisposable
{
    private readonly string _label;
    private readonly long _total;
    private readonly DateTime _startTime;
    private readonly object _lock = new();
    private readonly bool _showSpeed;
    private readonly int _barWidth;
    
    private long _current;
    private int _lastRenderedPercent = -1;
    private string _lastLabel = "";
    private bool _completed;
    private ConsoleColor _originalColor;
    private int _lastRenderLength;

    /// <summary>
    /// Create a progress bar for byte-based progress (downloads).
    /// </summary>
    /// <param name="label">Label to display (e.g., "Downloading").</param>
    /// <param name="total">Total bytes expected.</param>
    /// <param name="barWidth">Width of the progress bar in characters.</param>
    public ConsoleProgressBar(string label, long total, int barWidth = 30)
    {
        _label = label;
        _total = total > 0 ? total : 1;
        _startTime = DateTime.UtcNow;
        _showSpeed = true;
        _barWidth = barWidth;
        _originalColor = System.Console.ForegroundColor;
    }

    /// <summary>
    /// Create a progress bar for step-based progress (diagnostics, operations).
    /// </summary>
    /// <param name="barWidth">Width of the progress bar in characters.</param>
    public ConsoleProgressBar(int barWidth = 30)
    {
        _label = "";
        _total = 100;
        _startTime = DateTime.UtcNow;
        _showSpeed = false;
        _barWidth = barWidth;
        _originalColor = System.Console.ForegroundColor;
    }

    /// <summary>
    /// Report progress as bytes downloaded/processed.
    /// </summary>
    public void Report(long value)
    {
        lock (_lock)
        {
            if (_completed) return;
            
            _current = value;
            var percent = (int)(_current * 100 / _total);
            
            // Only re-render if percent changed (avoid console flicker)
            if (percent != _lastRenderedPercent)
            {
                _lastRenderedPercent = percent;
                Render(percent, _label);
            }
        }
    }

    /// <summary>
    /// Report progress as (current step, total steps, label).
    /// </summary>
    public void Report((int current, int total, string label) value)
    {
        lock (_lock)
        {
            if (_completed) return;
            
            var percent = value.total > 0 ? (int)(value.current * 100.0 / value.total) : 0;
            _lastLabel = value.label;
            
            if (percent != _lastRenderedPercent || _lastLabel != value.label)
            {
                _lastRenderedPercent = percent;
                Render(percent, value.label);
            }
        }
    }

    /// <summary>
    /// Render the progress bar to console.
    /// </summary>
    private void Render(int percent, string label)
    {
        var filled = (int)(percent * _barWidth / 100.0);
        var empty = _barWidth - filled;
        var bar = new string('█', filled) + new string('░', empty);

        string line;
        if (_showSpeed && _current > 0)
        {
            var elapsed = DateTime.UtcNow - _startTime;
            var speedBps = elapsed.TotalSeconds > 0 ? _current / elapsed.TotalSeconds : 0;
            var speedStr = FormatSpeed(speedBps);
            
            var remaining = speedBps > 0 ? TimeSpan.FromSeconds((_total - _current) / speedBps) : TimeSpan.Zero;
            var etaStr = remaining.TotalSeconds > 0 ? FormatEta(remaining) : "";
            
            var sizeStr = $"{FormatBytes(_current)}/{FormatBytes(_total)}";
            line = $"\r  [{bar}] {percent,3}% {sizeStr} {speedStr}{(string.IsNullOrEmpty(etaStr) ? "" : $" ETA: {etaStr}")}";
        }
        else
        {
            line = $"\r  [{bar}] {percent,3}% - {label,-20}";
        }

        // Clear previous render and write new line
        var clear = new string(' ', Math.Max(_lastRenderLength, line.Length));
        System.Console.Write($"\r{clear}");
        
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write(line);
        System.Console.ForegroundColor = _originalColor;
        
        _lastRenderLength = line.Length;
    }

    /// <summary>
    /// Mark the progress as complete with a success message.
    /// </summary>
    public void Complete(string? message = null)
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            
            // Clear progress bar
            var clear = new string(' ', _lastRenderLength + 10);
            System.Console.Write($"\r{clear}\r");
            
            if (!string.IsNullOrWhiteSpace(message))
            {
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"  ✔ {message}");
                System.Console.ForegroundColor = _originalColor;
            }
        }
    }

    /// <summary>
    /// Mark the progress as failed with an error message.
    /// </summary>
    public void Fail(string? message = null)
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
            
            // Clear progress bar
            var clear = new string(' ', _lastRenderLength + 10);
            System.Console.Write($"\r{clear}\r");
            
            if (!string.IsNullOrWhiteSpace(message))
            {
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"  ✖ {message}");
                System.Console.ForegroundColor = _originalColor;
            }
        }
    }

    /// <summary>
    /// Format bytes as human-readable string.
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        const long KB = 1024;
        const long MB = KB * 1024;
        const long GB = MB * 1024;

        return bytes switch
        {
            >= GB => $"{bytes / (double)GB:F1} GB",
            >= MB => $"{bytes / (double)MB:F1} MB",
            >= KB => $"{bytes / (double)KB:F1} KB",
            _ => $"{bytes} B"
        };
    }

    /// <summary>
    /// Format speed as human-readable string.
    /// </summary>
    private static string FormatSpeed(double bytesPerSecond)
    {
        const long KB = 1024;
        const long MB = KB * 1024;

        return bytesPerSecond switch
        {
            >= MB => $"@ {bytesPerSecond / MB:F1} MB/s",
            >= KB => $"@ {bytesPerSecond / KB:F0} KB/s",
            _ => $"@ {bytesPerSecond:F0} B/s"
        };
    }

    /// <summary>
    /// Format ETA as human-readable string.
    /// </summary>
    private static string FormatEta(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        if (remaining.TotalMinutes >= 1)
            return $"{remaining.Minutes}m {remaining.Seconds}s";
        return $"{remaining.Seconds}s";
    }

    /// <summary>
    /// Dispose and complete if not already done.
    /// </summary>
    public void Dispose()
    {
        if (!_completed)
        {
            Complete();
        }
    }
}
