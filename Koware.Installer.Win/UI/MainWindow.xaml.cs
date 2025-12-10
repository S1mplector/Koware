// Author: Ilgaz Mehmetoğlu 
// Handles installer UI interactions and invokes InstallerEngine operations.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WinForms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using Koware.Installer.Win.Models;
using Koware.Installer.Win.Services;
using Koware.Updater;

namespace Koware.Installer.Win.UI;

public partial class MainWindow : Window
{
    private readonly InstallerEngine _engine;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _defaultInstallDir;
    private readonly string? _installedVersion;
    
    // Step tracking
    private readonly Dictionary<string, StepItem> _steps = new();
    private string? _currentStep;

    public MainWindow()
    {
        InitializeComponent();
        _engine = new InstallerEngine();

        _defaultInstallDir = new InstallOptions().InstallDir;
        InstallPathBox.Text = _defaultInstallDir;
        _installedVersion = _engine.GetInstalledVersion(_defaultInstallDir);

        // Detect existing installation and adjust UI accordingly
        if (_engine.IsInstalled(_defaultInstallDir))
        {
            InstallButtonText.Text = "Re-install";
            UninstallButton.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(_installedVersion))
            {
                ExistingInstallInfo.Text = $"Koware is already installed at {_defaultInstallDir} (version {_installedVersion}).";
            }
            else
            {
                ExistingInstallInfo.Text = $"Koware is already installed at {_defaultInstallDir}.";
            }

            ExistingInstallInfo.Visibility = Visibility.Visible;
            BeginCheckLatestVersion();
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
        
        // Initialize installation steps
        InitializeSteps(new[]
        {
            "Preparing installation",
            "Extracting components",
            "Creating command shortcuts",
            "Updating system PATH",
            "Recording version info",
            "Creating Start Menu entry",
            "Registering application"
        });

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
            // Start first step
            SetStepStatus("Preparing installation", StepStatus.InProgress);
            
            await _engine.InstallAsync(options, new Progress<string>(ProcessProgressMessage), _cts.Token);
            
            // Mark all remaining steps as completed
            CompleteAllSteps();
            
            WpfMessageBox.Show(this, "Koware installed successfully!", "Koware Installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            FailCurrentStep();
        }
        catch (Exception ex)
        {
            FailCurrentStep();
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

    private void SetUiState(bool isBusy)
    {
        InstallButton.IsEnabled = !isBusy;
        BrowseButton.IsEnabled = !isBusy;
        UninstallButton.IsEnabled = !isBusy;
    }
    
    #region Step-based Progress UI
    
    private class StepItem
    {
        public Border Container { get; set; } = null!;
        public Ellipse Spinner { get; set; } = null!;
        public TextBlock Icon { get; set; } = null!;
        public TextBlock Label { get; set; } = null!;
        public Storyboard? SpinnerAnimation { get; set; }
        public StepStatus Status { get; set; } = StepStatus.Pending;
    }
    
    private enum StepStatus { Pending, InProgress, Completed, Failed }
    
    private void InitializeSteps(string[] stepLabels)
    {
        Dispatcher.Invoke(() =>
        {
            StepsPanel.Children.Clear();
            _steps.Clear();
            _currentStep = null;
            
            foreach (var label in stepLabels)
            {
                var step = CreateStepItem(label);
                _steps[label] = step;
                StepsPanel.Children.Add(step.Container);
            }
        });
    }
    
    private StepItem CreateStepItem(string label)
    {
        var container = new Border
        {
            Margin = new Thickness(0, 6, 0, 6),
            Padding = new Thickness(8, 10, 8, 10),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B))
        };
        
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        
        // Spinner (hidden by default)
        var spinner = new Ellipse
        {
            Width = 20,
            Height = 20,
            StrokeThickness = 2,
            Stroke = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8)),
            StrokeDashArray = new DoubleCollection { 3, 2 },
            Visibility = Visibility.Collapsed,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform()
        };
        Grid.SetColumn(spinner, 0);
        
        // Status icon (checkmark, X, or dot)
        var icon = new TextBlock
        {
            Text = "○",  // Pending dot
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B)),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetColumn(icon, 0);
        
        // Step label
        var labelBlock = new TextBlock
        {
            Text = label,
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0)
        };
        Grid.SetColumn(labelBlock, 1);
        
        grid.Children.Add(spinner);
        grid.Children.Add(icon);
        grid.Children.Add(labelBlock);
        container.Child = grid;
        
        return new StepItem
        {
            Container = container,
            Spinner = spinner,
            Icon = icon,
            Label = labelBlock
        };
    }
    
    private void SetStepStatus(string stepLabel, StepStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            if (!_steps.TryGetValue(stepLabel, out var step)) return;
            
            step.Status = status;
            
            switch (status)
            {
                case StepStatus.Pending:
                    step.Spinner.Visibility = Visibility.Collapsed;
                    step.SpinnerAnimation?.Stop();
                    step.Icon.Visibility = Visibility.Visible;
                    step.Icon.Text = "○";
                    step.Icon.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
                    step.Label.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
                    step.Container.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x29, 0x3B));
                    break;
                    
                case StepStatus.InProgress:
                    _currentStep = stepLabel;
                    step.Icon.Visibility = Visibility.Collapsed;
                    step.Spinner.Visibility = Visibility.Visible;
                    step.Label.Foreground = new SolidColorBrush(Color.FromRgb(0xE2, 0xE8, 0xF0));
                    step.Container.Background = new SolidColorBrush(Color.FromRgb(0x1E, 0x3A, 0x5F));
                    StartSpinnerAnimation(step);
                    break;
                    
                case StepStatus.Completed:
                    step.Spinner.Visibility = Visibility.Collapsed;
                    step.SpinnerAnimation?.Stop();
                    step.Icon.Visibility = Visibility.Visible;
                    step.Icon.Text = "✓";
                    step.Icon.FontFamily = new FontFamily("Segoe UI");
                    step.Icon.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                    step.Label.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                    step.Container.Background = new SolidColorBrush(Color.FromRgb(0x14, 0x53, 0x2D));
                    break;
                    
                case StepStatus.Failed:
                    step.Spinner.Visibility = Visibility.Collapsed;
                    step.SpinnerAnimation?.Stop();
                    step.Icon.Visibility = Visibility.Visible;
                    step.Icon.Text = "✗";
                    step.Icon.FontFamily = new FontFamily("Segoe UI");
                    step.Icon.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                    step.Label.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
                    step.Container.Background = new SolidColorBrush(Color.FromRgb(0x5C, 0x1D, 0x1D));
                    break;
            }
        });
    }
    
    private void StartSpinnerAnimation(StepItem step)
    {
        var animation = new DoubleAnimation
        {
            From = 0,
            To = 360,
            Duration = TimeSpan.FromSeconds(1),
            RepeatBehavior = RepeatBehavior.Forever
        };
        
        var storyboard = new Storyboard();
        storyboard.Children.Add(animation);
        Storyboard.SetTarget(animation, step.Spinner);
        Storyboard.SetTargetProperty(animation, new PropertyPath("(UIElement.RenderTransform).(RotateTransform.Angle)"));
        
        step.SpinnerAnimation = storyboard;
        storyboard.Begin();
    }
    
    private void ProcessProgressMessage(string message)
    {
        // Map engine progress messages to user-friendly step labels
        Dispatcher.Invoke(() =>
        {
            // Complete previous step if starting a new one
            if (_currentStep != null && _steps.TryGetValue(_currentStep, out var currentStepItem) 
                && currentStepItem.Status == StepStatus.InProgress)
            {
                // Check if we're moving to a new step
                var newStep = MapMessageToStep(message);
                if (newStep != null && newStep != _currentStep)
                {
                    SetStepStatus(_currentStep, StepStatus.Completed);
                }
            }
            
            // Start the new step based on message
            var stepLabel = MapMessageToStep(message);
            if (stepLabel != null && _steps.ContainsKey(stepLabel))
            {
                SetStepStatus(stepLabel, StepStatus.InProgress);
            }
        });
    }
    
    private string? MapMessageToStep(string message)
    {
        var msgLower = message.ToLowerInvariant();
        
        // Install steps
        if (msgLower.Contains("clean"))
            return "Preparing installation";
        if (msgLower.Contains("payload") || msgLower.Contains("extract"))
            return "Extracting components";
        if (msgLower.Contains("publish"))
            return "Extracting components";  // Publishing is like extracting/preparing
        if (msgLower.Contains("shim") || msgLower.Contains("command shim"))
            return "Creating command shortcuts";
        if (msgLower.Contains("path") && !msgLower.Contains("start menu"))
            return "Updating system PATH";
        if (msgLower.Contains("version") || msgLower.Contains("recorded"))
            return "Recording version info";
        if (msgLower.Contains("start menu"))
            return _steps.ContainsKey("Removing Start Menu entry") 
                ? "Removing Start Menu entry" 
                : "Creating Start Menu entry";
        if (msgLower.Contains("registered") || msgLower.Contains("programs and features"))
            return _steps.ContainsKey("Unregistering application") 
                ? "Unregistering application" 
                : "Registering application";
        
        // Uninstall steps
        if (msgLower.Contains("uninstalling") || msgLower.Contains("removed install directory"))
            return "Removing files";
        if (msgLower.Contains("removed") && msgLower.Contains("from user path"))
            return "Updating system PATH";
        if (msgLower.Contains("removed") && msgLower.Contains("start menu"))
            return "Removing Start Menu entry";
        if (msgLower.Contains("removed") && msgLower.Contains("programs and features"))
            return "Unregistering application";
            
        return null;
    }
    
    private void CompleteAllSteps()
    {
        Dispatcher.Invoke(() =>
        {
            foreach (var step in _steps.Values)
            {
                if (step.Status == StepStatus.InProgress || step.Status == StepStatus.Pending)
                {
                    SetStepStatus(step.Label.Text, StepStatus.Completed);
                }
            }
        });
    }
    
    private void FailCurrentStep()
    {
        if (_currentStep != null)
        {
            SetStepStatus(_currentStep, StepStatus.Failed);
        }
    }
    
    #endregion

    private async void BeginCheckLatestVersion()
    {
        try
        {
            var latest = await KowareUpdater.GetLatestVersionAsync(_cts.Token);
            var label = !string.IsNullOrWhiteSpace(latest.Tag) ? latest.Tag : latest.Name;
            if (string.IsNullOrWhiteSpace(label))
            {
                return;
            }

            Dispatcher.Invoke(() =>
            {
                if (ExistingInstallInfo.Visibility != Visibility.Visible)
                {
                    return;
                }

                if (!string.IsNullOrWhiteSpace(_installedVersion))
                {
                    ExistingInstallInfo.Text = $"Koware is already installed at {_defaultInstallDir} (version {_installedVersion}, latest {label}).";
                }
                else
                {
                    ExistingInstallInfo.Text = $"Koware is already installed at {_defaultInstallDir} (latest {label}).";
                }
            });
        }
        catch
        {
        }
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
        
        // Initialize uninstall steps
        InitializeSteps(new[]
        {
            "Removing files",
            "Updating system PATH",
            "Removing Start Menu entry",
            "Unregistering application"
        });

        var installDir = InstallPathBox.Text.Trim();

        try
        {
            SetStepStatus("Removing files", StepStatus.InProgress);
            
            await _engine.UninstallAsync(installDir, new Progress<string>(ProcessProgressMessage), _cts.Token);
            
            CompleteAllSteps();
            
            WpfMessageBox.Show(this, "Koware was removed from this machine.", "Koware Installer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            FailCurrentStep();
        }
        catch (Exception ex)
        {
            FailCurrentStep();
            WpfMessageBox.Show(this, ex.Message, "Uninstall failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetUiState(isBusy: false);
        }
    }
}
