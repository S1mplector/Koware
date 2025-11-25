// Author: Ilgaz Mehmetoğlu
// Simple reusable console spinner/step logger for friendlier progress output.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Cli.Console;

internal sealed class ConsoleStep : IDisposable
{
    private static readonly string[] Spinner = { "-", "\\", "|", "/" };
    private readonly string _message;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private readonly object _gate = new();
    private bool _completed;
    private ConsoleColor _originalColor;

    private ConsoleStep(string message)
    {
        _message = message;
        _originalColor = System.Console.ForegroundColor;
        _loop = Task.Run(SpinAsync);
    }

    public static ConsoleStep Start(string message)
    {
        var step = new ConsoleStep(message);
        return step;
    }

    public void Succeed(string? text = null) => Stop(text ?? _message, ConsoleColor.Green, "✔");
    public void Fail(string? text = null) => Stop(text ?? _message, ConsoleColor.Red, "✖");

    private async Task SpinAsync()
    {
        var i = 0;
        while (!_cts.IsCancellationRequested)
        {
            lock (_gate)
            {
                if (_completed) break;
                System.Console.ForegroundColor = ConsoleColor.DarkYellow;
                System.Console.Write($"\r{Spinner[i % Spinner.Length]} {_message}");
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
            System.Console.ForegroundColor = color;
            System.Console.Write($"\r{symbol} {text}");
            System.Console.ForegroundColor = _originalColor;
            System.Console.WriteLine();
        }
    }

    public void Dispose()
    {
        if (!_completed)
        {
            Succeed();
        }
        _cts.Dispose();
    }
}
