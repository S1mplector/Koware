// Author: Ilgaz Mehmetoğlu 
// Handles installer UI interactions and invokes InstallerEngine operations.
using System;
using System.IO;
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
    private readonly string _defaultInstallDir;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new InstallerEngine();

        _defaultInstallDir = new InstallOptions().InstallDir;
        InstallPathBox.Text = _defaultInstallDir;

        // Detect existing installation and adjust UI accordingly
        if (_engine.IsInstalled(_defaultInstallDir))
        {
            InstallButton.Content = "Re-install";
            UninstallButton.Visibility = Visibility.Visible;
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        // Load usage notice markdown from the installer output directory
        var noticePath = Path.Combine(AppContext.BaseDirectory, "Usage-Notice.md");
        string noticeText;
        try
        {
            noticeText = File.Exists(noticePath)
                ? File.ReadAllText(noticePath)
                : "Koware usage notice could not be loaded. Please see the repository for details.";
        }
        catch
        {
            noticeText = "Koware usage notice could not be loaded. Please see the repository for details.";
        }

        var dialog = new LegalDialog(noticeText) { Owner = this };
        var accepted = dialog.ShowDialog() == true && dialog.Accepted;
        if (!accepted)
        {
            WpfMessageBox.Show(this,
                "You must accept the Usage Notice to install Koware.",
                "Usage Notice",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

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
        UninstallButton.IsEnabled = !isBusy;
    }

    private async void OnUninstall(object sender, RoutedEventArgs e)
    {
        var result = WpfMessageBox.Show(this,
            "This will remove Koware from this machine, including its files and PATH entry. Do you want to continue?",
            "Remove Koware",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        SetUiState(isBusy: true);
        AppendLog("Starting uninstall...");

        var installDir = InstallPathBox.Text.Trim();

        try
        {
            await _engine.UninstallAsync(installDir, new Progress<string>(AppendLog), _cts.Token);
            AppendLog("Uninstall completed.");
            WpfMessageBox.Show(this, "Koware was removed from this machine.", "Koware Installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Uninstall canceled.");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            WpfMessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiState(isBusy: false);
        }
    }
}
