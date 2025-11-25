// Author: Ilgaz Mehmetoğlu 
// Handles installer UI interactions and invokes InstallerEngine operations.
using System;
using System.Threading;
using System.Windows;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using Koware.Installer.Win.Models;
using Koware.Installer.Win.Services;

namespace Koware.Installer.Win.UI;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine;
    private readonly CancellationTokenSource _cts = new();

    public MainWindow()
    {
        InitializeComponent();
        _engine = new InstallerEngine();
        InstallPathBox.Text = new InstallOptions().InstallDir;
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        WelcomeScreen.Visibility = Visibility.Collapsed;
        InstallerScreen.Visibility = Visibility.Visible;
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        SetUiState(isBusy: true);
        AppendLog("Starting install...");

        var options = new InstallOptions
        {
            InstallDir = InstallPathBox.Text.Trim(),
            Publish = true,
            IncludePlayer = true,
            AddToPath = true,
            CleanTarget = true
        };

        try
        {
            await _engine.InstallAsync(options, new Progress<string>(AppendLog), _cts.Token);
            AppendLog("Install completed.");
            WpfMessageBox.Show(this, "Koware installed successfully.", "Koware Installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Install canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            WpfMessageBox.Show(this, ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiState(isBusy: false);
        }
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        using var dialog = new WinForms.FolderBrowserDialog
        {
            Description = "Select install directory",
            SelectedPath = InstallPathBox.Text
        };

        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
        {
            InstallPathBox.Text = dialog.SelectedPath;
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LogBlock.Text += $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}";
        });
    }

    private void SetUiState(bool isBusy)
    {
        InstallButton.IsEnabled = !isBusy;
        BrowseButton.IsEnabled = !isBusy;
    }
}
