// Author: Ilgaz Mehmetoğlu
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Cli.Console;

/// <summary>
/// Simple reusable console spinner/step logger for friendlier progress output.
/// Shows an animated spinner while an operation is in progress, then replaces it with a success/fail icon.
/// </summary>
/// <example>
/// using var step = ConsoleStep.Start("Loading data");
/// await LoadDataAsync();
/// step.Succeed("Data loaded");
/// </example>
internal sealed class ConsoleStep : IDisposable
{
    private static readonly string[] Spinner = { "-", "\\", "|", "/" };
    private readonly string _message;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private bool _completed;
    private ConsoleColor _originalColor;
    private int _lastRenderLength;

    /// <summary>Create and start a new console step.</summary>
    private ConsoleStep(string message)
    {
        _message = message;
        _originalColor = System.Console.ForegroundColor;
        _loop = Task.Run(SpinAsync);
    }

    /// <summary>
    /// Start a new console step with an animated spinner.
    /// </summary>
    /// <param name="message">Message to display next to the spinner.</param>
    /// <returns>A disposable ConsoleStep; call Succeed/Fail when done.</returns>
    public static ConsoleStep Start(string message)
    {
        var step = new ConsoleStep(message);
        return step;
    }

    /// <summary>Mark the step as successful with an optional completion message.</summary>
    /// <param name="text">Message to display; defaults to original message.</param>
    public void Succeed(string? text = null) => Stop(text ?? _message, ConsoleColor.Green, "✔");

    /// <summary>Mark the step as failed with an optional error message.</summary>
    /// <param name="text">Message to display; defaults to original message.</param>
    public void Fail(string? text = null) => Stop(text ?? _message, ConsoleColor.Red, "✖");

    /// <summary>Async loop that animates the spinner until stopped.</summary>
    private async Task SpinAsync()
    {
        var i = 0;
        while (!_cts.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (_completed) break;
                System.Console.ForegroundColor = ConsoleColor.DarkYellow;
                var text = $"{Spinner[i % Spinner.Length]} {_message}";
                _lastRenderLength = text.Length;
                System.Console.Write($"\r{text}");
                System.Console.ForegroundColor = _originalColor;
            }

            i++;
            try
            {
                await Task.Delay(120, _cts.Token);
            }
            catch
            {
                break;
            }
        }
    }

    /// <summary>Stop the spinner and display final status.</summary>
    private void Stop(string text, ConsoleColor color, string symbol)
    {
        lock (_gate)
        {
            if (_completed)
            {
                return;
            }

            _completed = true;
        }

        try
        {
            _cts.Cancel();
            _loop.Wait(500);
        }
        catch
        {
            // ignore spinner shutdown issues
        }

        lock (_gate)
        {
            var clear = new string(' ', Math.Max(_lastRenderLength, symbol.Length + 1 + text.Length));
            System.Console.Write($"\r{clear}\r");
            System.Console.ForegroundColor = color;
            var line = $"{symbol} {text}";
            System.Console.Write(line);
            _lastRenderLength = line.Length;
            System.Console.ForegroundColor = _originalColor;
            System.Console.WriteLine();
        }
    }

    /// <summary>Dispose the step, calling Succeed if not already completed.</summary>
    public void Dispose()
    {
        if (!_completed)
        {
            Succeed();
        }
        _cts.Dispose();
    }
}
 
