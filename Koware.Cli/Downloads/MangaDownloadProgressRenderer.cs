using System;
using System.Text;

namespace Koware.Cli.Downloads;

internal sealed class MangaDownloadProgressRenderer : IDisposable
{
    private readonly int _totalChapters;
    private readonly object _lock = new();
    private readonly bool _disabled;
    private readonly char _filledChar;
    private readonly char _emptyChar;

    private int _lastRenderLength;
    private int _completedChapters;
    private int _currentChapterIndex;
    private double _currentChapterNumber;
    private int _currentChapterTotalPages;
    private int _currentChapterCompletedPages;
    private int _currentChapterFailedPages;
    private bool _completed;

    internal MangaDownloadProgressRenderer(int totalChapters)
    {
        _totalChapters = Math.Max(1, totalChapters);
        _disabled = System.Console.IsOutputRedirected;
        var useUnicode = !_disabled && System.Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage;
        _filledChar = useUnicode ? '█' : '#';
        _emptyChar = useUnicode ? '░' : '-';
    }

    internal void StartChapter(int chapterIndex, double chapterNumber, int totalPages, int completedPages = 0)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _currentChapterIndex = Math.Max(1, chapterIndex);
            _currentChapterNumber = chapterNumber;
            _currentChapterTotalPages = Math.Max(0, totalPages);
            _currentChapterCompletedPages = Math.Clamp(completedPages, 0, _currentChapterTotalPages);
            _currentChapterFailedPages = 0;
            Render();
        }
    }

    internal void MarkPageCompleted()
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _currentChapterCompletedPages++;
            Render();
        }
    }

    internal void MarkPageFailed()
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _currentChapterFailedPages++;
            Render();
        }
    }

    internal void CompleteChapter()
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completedChapters = Math.Min(_totalChapters, _completedChapters + 1);
            Render();
        }
    }

    internal void Complete(string? message = null)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_disabled)
            {
                return;
            }

            ClearCurrentLine();
            if (!string.IsNullOrWhiteSpace(message))
            {
                var original = System.Console.ForegroundColor;
                System.Console.ForegroundColor = ConsoleColor.Green;
                System.Console.WriteLine($"  {(UseUnicodeGlyphs() ? "✔" : "OK")} {message}");
                System.Console.ForegroundColor = original;
            }
        }
    }

    internal void Fail(string? message = null)
    {
        lock (_lock)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
            if (_disabled)
            {
                return;
            }

            ClearCurrentLine();
            if (!string.IsNullOrWhiteSpace(message))
            {
                var original = System.Console.ForegroundColor;
                System.Console.ForegroundColor = ConsoleColor.Red;
                System.Console.WriteLine($"  {(UseUnicodeGlyphs() ? "✖" : "X")} {message}");
                System.Console.ForegroundColor = original;
            }
        }
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Complete();
        }
    }

    private void Render()
    {
        if (_disabled)
        {
            return;
        }

        var currentPages = Math.Max(1, _currentChapterTotalPages);
        var chapterFraction = _currentChapterTotalPages == 0
            ? 0d
            : Math.Min(1d, (_currentChapterCompletedPages + _currentChapterFailedPages) / (double)currentPages);
        var overallPercent = (int)Math.Round(((_completedChapters + chapterFraction) / _totalChapters) * 100d, MidpointRounding.AwayFromZero);
        overallPercent = Math.Clamp(overallPercent, 0, 100);

        const int barWidth = 24;
        var filled = (int)Math.Round((overallPercent / 100d) * barWidth, MidpointRounding.AwayFromZero);
        filled = Math.Clamp(filled, 0, barWidth);
        var bar = new string(_filledChar, filled) + new string(_emptyChar, barWidth - filled);
        var pageStatus = _currentChapterTotalPages > 0
            ? $"{_currentChapterCompletedPages}/{_currentChapterTotalPages} pages"
            : "resolving pages";
        var failureStatus = _currentChapterFailedPages > 0
            ? $" | fail {_currentChapterFailedPages}"
            : string.Empty;

        var line = $"\r  [{bar}] {overallPercent,3}% | Ch {_currentChapterIndex}/{_totalChapters} | Current {DownloadDisplayFormatter.FormatNumber(_currentChapterNumber)} {pageStatus}{failureStatus}";
        WriteLine(line);
    }

    private void WriteLine(string line)
    {
        var clear = new string(' ', Math.Max(_lastRenderLength, line.Length));
        System.Console.Write($"\r{clear}");
        System.Console.ForegroundColor = ConsoleColor.Cyan;
        System.Console.Write(line);
        System.Console.ResetColor();
        _lastRenderLength = line.Length;
    }

    private void ClearCurrentLine()
    {
        var clear = new string(' ', _lastRenderLength + 10);
        System.Console.Write($"\r{clear}\r");
    }

    private bool UseUnicodeGlyphs()
    {
        return !_disabled && System.Console.OutputEncoding.CodePage == Encoding.UTF8.CodePage;
    }
}
