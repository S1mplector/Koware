// Author: Ilgaz Mehmetoğlu
// Application bootstrap for the WPF manga reader, parsing arguments and launching the main window.
using System.Windows;

using Koware.Reader.Win.Startup;

namespace Koware.Reader.Win;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!ReaderArguments.TryParse(e.Args, out var args, out var error))
        {
            var message = string.IsNullOrWhiteSpace(error)
                ? "Usage: Koware.Reader.Win.exe <pages-json> <title> [--referer <value>] [--user-agent <value>]"
                : error;

            MessageBox.Show(message, "Koware Reader", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(args!);
        MainWindow = window;
        window.Show();
    }
}
