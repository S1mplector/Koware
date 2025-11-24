using System.Windows;

namespace Koware.Player.Win;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (!PlayerArguments.TryParse(e.Args, out var args, out var error))
        {
            var message = string.IsNullOrWhiteSpace(error)
                ? "Usage: Koware.Player.Win.exe <url> <title> [--referer <value>] [--user-agent <value>]"
                : error;

            MessageBox.Show(message, "Koware Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var window = new MainWindow(args!);
        MainWindow = window;
        window.Show();
    }
}
