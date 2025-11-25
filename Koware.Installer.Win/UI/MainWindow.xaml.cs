// Author: Ilgaz Mehmetoğlu 
// Handles installer UI interactions and invokes InstallerEngine operations.
using System;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
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

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        SetUiState(isBusy: true);
        AppendLog("Starting install...");

        var options = new InstallOptions
        {
            InstallDir = InstallPathBox.Text.Trim(),
            Publish = PublishCheckbox.IsChecked == true,
            IncludePlayer = IncludePlayerCheckbox.IsChecked == true,
            AddToPath = AddToPathCheckbox.IsChecked == true,
            CleanTarget = CleanCheckbox.IsChecked == true
        };

        try
        {
            await _engine.InstallAsync(options, new Progress<string>(AppendLog), _cts.Token);
            AppendLog("Install completed.");
            MessageBox.Show(this, "Koware installed successfully.", "Koware Installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Install canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Install failed", MessageBoxButton.OK, MessageBoxImage.Error);
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
        using var dialog = new FolderBrowserDialog
        {
            Description = "Select install directory",
            SelectedPath = InstallPathBox.Text
        };

        if (dialog.ShowDialog() == DialogResult.OK)
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
        PublishCheckbox.IsEnabled = !isBusy;
        IncludePlayerCheckbox.IsEnabled = !isBusy;
        AddToPathCheckbox.IsEnabled = !isBusy;
        CleanCheckbox.IsEnabled = !isBusy;
    }
}
