// Author: Ilgaz Mehmetoğlu
// Entry point for the cross-platform Koware manga reader.
using Avalonia;
using System;

namespace Koware.Reader;

internal class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
