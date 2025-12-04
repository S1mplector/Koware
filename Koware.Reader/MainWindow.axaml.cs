// Author: Ilgaz Mehmetoğlu
// Main window for the cross-platform Koware manga reader.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls.Primitives.PopupPositioning;

namespace Koware.Reader;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient;
    private readonly List<Image> _pageImages = new();
    private readonly Dictionary<int, Bitmap?> _loadedBitmaps = new();
    private readonly Dictionary<int, Border> _pagePlaceholders = new();
    private CancellationTokenSource? _loadCts;
    
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private int _currentPage = 1;
    private FitMode _fitMode = FitMode.FitWidth;
    private double _zoomLevel = 1.0;
    private bool _showHelp;
    private bool _isScrollTracking = true;
    private bool _doublePageMode;
    private DateTime _lastScrollTime = DateTime.MinValue;
    private bool _autoHideUi;
    private readonly DispatcherTimer _uiHideTimer;
    private int _loadedCount;
    private float _currentChapterNumber;

    public List<PageInfo> Pages { get; set; } = new();
    public List<ChapterInfo> Chapters { get; set; } = new();
    public string? HttpReferer { get; set; }
    public string? HttpUserAgent { get; set; }
    public ChapterNavigationRequest ChapterNavigation { get; private set; } = ChapterNavigationRequest.None;
    public string? NavResultPath { get; set; }

    private enum FitMode
    {
        FitWidth,
        FitHeight,
        Original
    }

    public enum ChapterNavigationRequest
    {
        None,
        Previous,
        Next
    }

    public MainWindow()
    {
        InitializeComponent();
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "image/*,*/*");
        
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        SizeChanged += OnSizeChanged;
        
        // Track scroll position to update current page
        ScrollViewer.ScrollChanged += OnScrollChanged;
        
        // Mouse wheel zoom with Ctrl
        ScrollViewer.PointerWheelChanged += OnPointerWheelChanged;

        // Auto-hide header/footer when idle
        _uiHideTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _uiHideTimer.Tick += (_, _) => SetUiVisibility(false);
        PointerMoved += (_, _) => ResetUiHideTimer();
        KeyDown += (_, _) => ResetUiHideTimer();
    }
    
    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl+Scroll for zoom
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (e.Delta.Y > 0)
                ZoomIn();
            else if (e.Delta.Y < 0)
                ZoomOut();
            e.Handled = true;
        }
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        if (Pages.Count == 0)
        {
            ShowError("No pages provided.");
            return;
        }

        // Configure HTTP headers
        if (!string.IsNullOrWhiteSpace(HttpReferer))
        {
            _httpClient.DefaultRequestHeaders.Referrer = new Uri(HttpReferer);
        }
        if (!string.IsNullOrWhiteSpace(HttpUserAgent))
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(HttpUserAgent);
        }

        TitleText.Text = Title;
        ChapterLabel.Text = Title;
        ChapterNameLabel.Text = Title;
        _currentChapterNumber = Chapters.FirstOrDefault(c => !c.IsRead)?.Number
                                 ?? Chapters.LastOrDefault()?.Number
                                 ?? 0;
        if (_currentChapterNumber > 0)
        {
            ChapterLabel.Text = $"Chapter {_currentChapterNumber}";
            ChapterNameLabel.Text = $"Chapter {_currentChapterNumber}";
        }
        PageSlider.Maximum = Pages.Count;
        PageSlider.Value = 1;
        UpdatePageIndicator();
        UpdateProgressLabel();
        ThemeSelector.SelectedIndex = 0;

        // Start loading pages
        _loadCts = new CancellationTokenSource();
        _ = LoadAllPagesAsync(_loadCts.Token);
    }

    private async Task LoadAllPagesAsync(CancellationToken cancellationToken)
    {
        var totalPages = Pages.Count;
        _loadedCount = 0;

        try
        {
            // Create placeholder images for all pages
            foreach (var page in Pages.OrderBy(p => p.PageNumber))
            {
                var container = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#101020")),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 2, 0, 2)
                };

                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    Tag = page.PageNumber
                };
                _pageImages.Add(image);
                container.Child = image;
                PagesContainer.Children.Add(container);
                _pagePlaceholders[page.PageNumber] = container;
            }

            // Load images in parallel (limited concurrency)
            var semaphore = new SemaphoreSlim(3); // 3 concurrent downloads
            var loadTasks = Pages.OrderBy(p => p.PageNumber).Select(async page =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var bitmap = await LoadImageAsync(page.Url, cancellationToken);
                    
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var index = page.PageNumber - 1;
                        if (index >= 0 && index < _pageImages.Count)
                        {
                            _loadedBitmaps[page.PageNumber] = bitmap;
                            if (bitmap is not null)
                            {
                                _pageImages[index].Source = bitmap;
                                ClearPlaceholder(page.PageNumber);
                                ApplyFitMode(_pageImages[index]);
                            }
                            else
                            {
                                ShowRetryPlaceholder(page.PageNumber, page.Url);
                            }
                        }

                        _loadedCount++;
                        LoadingProgress.Text = $"{_loadedCount} / {totalPages}";
                        LoadingProgressBar.Value = (_loadedCount * 100.0) / totalPages;
                        PrefetchDot.IsVisible = _loadedCount < totalPages;
                        UpdateProgressLabel();

                        if (_loadedCount >= totalPages)
                        {
                            LoadingOverlay.IsVisible = false;
                            PrefetchDot.IsVisible = false;
                        }
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(loadTasks);
        }
        catch (OperationCanceledException)
        {
            // Loading cancelled
        }
        catch (Exception ex)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ShowError($"Failed to load pages: {ex.Message}");
            });
        }
    }

    private async Task<Bitmap?> LoadImageAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _httpClient.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream, cancellationToken);
            memoryStream.Position = 0;
            
            return new Bitmap(memoryStream);
        }
        catch
        {
            return null;
        }
    }

    private void ShowRetryPlaceholder(int pageNumber, string url)
    {
        if (!_pagePlaceholders.TryGetValue(pageNumber, out var container)) return;

        var retryButton = new Button
        {
            Content = "Retry",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            Padding = new Thickness(10, 6),
            Classes = { "toolbar" }
        };
        retryButton.Click += async (_, _) =>
        {
            retryButton.IsEnabled = false;
            var bitmap = await LoadImageAsync(url, CancellationToken.None);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (bitmap is not null)
                {
                    var image = _pageImages[pageNumber - 1];
                    _loadedBitmaps[pageNumber] = bitmap;
                    image.Source = bitmap;
                    ClearPlaceholder(pageNumber);
                    ApplyFitMode(image);
                }
                else
                {
                    retryButton.IsEnabled = true;
                }
            });
        };

        container.Child = new StackPanel
        {
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Spacing = 6,
            Children =
            {
                new TextBlock
                {
                    Text = "Failed to load page",
                    Foreground = Brushes.LightGray,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                },
                retryButton
            }
        };
    }

    private void ClearPlaceholder(int pageNumber)
    {
        if (!_pagePlaceholders.TryGetValue(pageNumber, out var container)) return;
        var image = _pageImages[pageNumber - 1];
        container.Child = image;
    }

    private void ApplyFitMode(Image image)
    {
        if (image.Source is not Bitmap bitmap) return;

        var containerWidth = ScrollViewer.Bounds.Width - 40; // Padding
        var containerHeight = ScrollViewer.Bounds.Height - 40;

        switch (_fitMode)
        {
            case FitMode.FitWidth:
                image.Width = containerWidth * _zoomLevel;
                image.Height = double.NaN;
                break;
            case FitMode.FitHeight:
                image.Width = double.NaN;
                image.Height = containerHeight * _zoomLevel;
                break;
            case FitMode.Original:
                image.Width = bitmap.PixelSize.Width * _zoomLevel;
                image.Height = bitmap.PixelSize.Height * _zoomLevel;
                break;
        }
    }

    private void ApplyFitModeToAll()
    {
        foreach (var image in _pageImages)
        {
            ApplyFitMode(image);
        }
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        ApplyFitModeToAll();
    }

    private void OnFitModeClick(object? sender, RoutedEventArgs e)
    {
        CycleFitMode();
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowState = _previousWindowState;
            _isFullscreen = false;
        }
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
            _isFullscreen = true;
        }
    }

    private void OnPreviousClick(object? sender, RoutedEventArgs e)
    {
        NavigateToPage(_currentPage - 1);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        NavigateToPage(_currentPage + 1);
    }

    private void OnPageSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var page = (int)e.NewValue;
        if (page != _currentPage)
        {
            NavigateToPage(page, updateSlider: false);
        }
    }

    private void NavigateToPage(int page, bool updateSlider = true)
    {
        if (page < 1 || page > Pages.Count) return;
        
        _currentPage = page;
        
        if (updateSlider)
        {
            PageSlider.Value = page;
        }
        
        UpdatePageIndicator();
        UpdateProgressLabel();
        
        // Scroll to page
        if (page - 1 < _pageImages.Count)
        {
            var target = _pageImages[page - 1];
            target.BringIntoView();
        }
    }

    private void UpdatePageIndicator()
    {
        PageIndicator.Text = $"Page {_currentPage} / {Pages.Count}";
    }

    private void UpdateProgressLabel()
    {
        var percent = (int)Math.Round((_currentPage / (double)Math.Max(1, Pages.Count)) * 100);
        ProgressLabel.Text = $"{percent}%";
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
            case Key.PageUp:
                NavigateToPage(_currentPage - 1);
                e.Handled = true;
                break;
                
            case Key.Right:
            case Key.PageDown:
            case Key.Space:
                NavigateToPage(_currentPage + 1);
                e.Handled = true;
                break;
                
            case Key.Home:
                NavigateToPage(1);
                e.Handled = true;
                break;
                
            case Key.End:
                NavigateToPage(Pages.Count);
                e.Handled = true;
                break;
                
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
                
            case Key.Escape:
                if (_showHelp)
                {
                    ToggleHelp();
                }
                else if (_isFullscreen)
                {
                    ToggleFullscreen();
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
                
            case Key.Q:
                Close();
                e.Handled = true;
                break;
                
            // Zoom controls
            case Key.OemPlus:
            case Key.Add:
                ZoomIn();
                e.Handled = true;
                break;
                
            case Key.OemMinus:
            case Key.Subtract:
                ZoomOut();
                e.Handled = true;
                break;
                
            case Key.D0:
            case Key.NumPad0:
                ResetZoom();
                e.Handled = true;
                break;
                
            // Help toggle
            case Key.OemQuestion:
            case Key.H:
                ToggleHelp();
                e.Handled = true;
                break;
                
            // Fit mode cycling
            case Key.W:
                CycleFitMode();
                e.Handled = true;
                break;
                
            // Double-page mode
            case Key.D:
                ToggleDoublePageMode();
                e.Handled = true;
                break;
                
            // Jump to specific page (number keys 1-9)
            case Key.D1: NavigateToPage(1); e.Handled = true; break;
            case Key.D2: NavigateToPage(Math.Max(1, Pages.Count / 4)); e.Handled = true; break;
            case Key.D3: NavigateToPage(Math.Max(1, Pages.Count / 3)); e.Handled = true; break;
            case Key.D4: NavigateToPage(Math.Max(1, Pages.Count / 2)); e.Handled = true; break;
            case Key.D5: NavigateToPage(Math.Max(1, (Pages.Count * 2) / 3)); e.Handled = true; break;
            case Key.D9: NavigateToPage(Pages.Count); e.Handled = true; break;
        }
    }
    
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (!_isScrollTracking || _pageImages.Count == 0) return;
            
            // Throttle scroll updates
            var now = DateTime.UtcNow;
            if ((now - _lastScrollTime).TotalMilliseconds < 100) return;
            _lastScrollTime = now;
            
            // Find which page is most visible
            var scrollOffset = ScrollViewer.Offset.Y;
            var viewportHeight = ScrollViewer.Viewport.Height;
            var viewportCenter = scrollOffset + viewportHeight / 2;
            
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var image = _pageImages[i];
                if (image.Parent == null) continue; // Skip if not attached
                
                var bounds = image.Bounds;
                var imageTop = bounds.Top;
                var imageBottom = bounds.Bottom;
                
                if (viewportCenter >= imageTop && viewportCenter <= imageBottom)
                {
                    var newPage = i + 1;
                    if (newPage != _currentPage)
                    {
                        _currentPage = newPage;
                        PageSlider.Value = newPage;
                        UpdatePageIndicator();
                    }
                    break;
                }
            }
        }
        catch
        {
            // Silently ignore scroll tracking errors
        }
    }
    
    private void ZoomIn()
    {
        _zoomLevel = Math.Min(_zoomLevel + 0.1, 3.0);
        ApplyZoom();
    }
    
    private void ZoomOut()
    {
        _zoomLevel = Math.Max(_zoomLevel - 0.1, 0.3);
        ApplyZoom();
    }
    
    private void ResetZoom()
    {
        _zoomLevel = 1.0;
        ApplyZoom();
    }
    
    private void ApplyZoom()
    {
        ZoomIndicator.Text = $"{(int)(_zoomLevel * 100)}%";
        ApplyFitModeToAll();
    }
    
    private void CycleFitMode()
    {
        _fitMode = _fitMode switch
        {
            FitMode.FitWidth => FitMode.FitHeight,
            FitMode.FitHeight => FitMode.Original,
            FitMode.Original => FitMode.FitWidth,
            _ => FitMode.FitWidth
        };
        
        FitModeButton.Content = _fitMode switch
        {
            FitMode.FitWidth => "Fit Width",
            FitMode.FitHeight => "Fit Height",
            FitMode.Original => "Original",
            _ => "Fit Width"
        };
        
        ApplyFitModeToAll();
    }
    
    private void ToggleHelp()
    {
        _showHelp = !_showHelp;
        HelpOverlay.IsVisible = _showHelp;
    }
    
    private void ToggleDoublePageMode()
    {
        _doublePageMode = !_doublePageMode;
        DoublePageButton.Content = _doublePageMode ? "Double" : "Single";
        RebuildPageLayout();
    }
    
    private void OnDoublePageClick(object? sender, RoutedEventArgs e)
    {
        ToggleDoublePageMode();
    }
    
    private void RebuildPageLayout()
    {
        // First, detach all images from their current parents
        foreach (var image in _pageImages)
        {
            if (image.Parent is Panel parent)
            {
                parent.Children.Remove(image);
            }
        }
        
        PagesContainer.Children.Clear();
        
        if (_doublePageMode)
        {
            // Double-page layout: two images per row
            PagesContainer.Orientation = Avalonia.Layout.Orientation.Vertical;
            
            for (int i = 0; i < _pageImages.Count; i += 2)
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                row.Children.Add(_pageImages[i]);
                
                if (i + 1 < _pageImages.Count)
                {
                    row.Children.Add(_pageImages[i + 1]);
                }
                
                PagesContainer.Children.Add(row);
            }
        }
        else
        {
            // Single-page layout
            PagesContainer.Orientation = Avalonia.Layout.Orientation.Vertical;
            foreach (var image in _pageImages)
            {
                PagesContainer.Children.Add(image);
            }
        }
        
        ApplyFitModeToAll();
    }
    
    private void OnHelpCloseClick(object? sender, RoutedEventArgs e)
    {
        ToggleHelp();
    }

    private void OnPrevChapterClick(object? sender, RoutedEventArgs e)
    {
        ChapterNavigation = ChapterNavigationRequest.Previous;
        Close();
    }

    private void OnNextChapterClick(object? sender, RoutedEventArgs e)
    {
        ChapterNavigation = ChapterNavigationRequest.Next;
        Close();
    }
    
    private void OnZoomInClick(object? sender, RoutedEventArgs e)
    {
        ZoomIn();
    }
    
    private void OnZoomOutClick(object? sender, RoutedEventArgs e)
    {
        ZoomOut();
    }

    private void OnSettingsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control control)
        {
            FlyoutBase.ShowAttachedFlyout(control);
        }
    }

    private void OnThemeChanged(object? sender, SelectionChangedEventArgs e)
    {
        var selection = (ThemeSelector.SelectedItem as ComboBoxItem)?.Content?.ToString()?.ToLowerInvariant();
        switch (selection)
        {
            case "sepia":
                SetTheme("#f3e7d3", "#1b1307");
                break;
            case "light":
                SetTheme("#f5f7fb", "#111827");
                break;
            default:
                SetTheme("#1a1a2e", "#dfe6ff");
                break;
        }
    }

    private void SetTheme(string backgroundHex, string foregroundHex)
    {
        var bg = Color.Parse(backgroundHex);
        var fg = Color.Parse(foregroundHex);

        ContentWrapper.Background = new SolidColorBrush(bg);
        ScrollViewer.Background = new SolidColorBrush(bg);
        TitleText.Foreground = new SolidColorBrush(fg);
        PageIndicator.Foreground = new SolidColorBrush(fg);
        ChapterLabel.Foreground = new SolidColorBrush(fg);
        ChapterNameLabel.Foreground = new SolidColorBrush(fg);
    }

    private void OnComfortChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        ComfortOverlay.Opacity = e.NewValue;
    }

    private void OnAutoHideToggled(object? sender, RoutedEventArgs e)
    {
        _autoHideUi = AutoHideToggle.IsChecked == true;
        if (_autoHideUi)
        {
            ResetUiHideTimer();
        }
        else
        {
            _uiHideTimer.Stop();
            SetUiVisibility(true);
        }
    }

    private void ResetUiHideTimer()
    {
        if (!_autoHideUi) return;
        SetUiVisibility(true);
        _uiHideTimer.Stop();
        _uiHideTimer.Start();
    }

    private void SetUiVisibility(bool show)
    {
        HeaderBar.Opacity = show ? 1 : 0;
        FooterBar.Opacity = show ? 1 : 0;
        HeaderBar.IsHitTestVisible = show;
        FooterBar.IsHitTestVisible = show;
    }

    private void OnChaptersClick(object? sender, RoutedEventArgs e)
    {
        if (Chapters.Count == 0)
        {
            var toast = new Window
            {
                Width = 300,
                Height = 120,
                Background = Brushes.Black,
                Opacity = 0.8,
                CanResize = false,
                ShowInTaskbar = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Content = new TextBlock
                {
                    Text = "No chapter list provided.",
                    Foreground = Brushes.White,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                }
            };
            toast.ShowDialog(this);
            return;
        }

        var overlay = new Window
        {
            Title = "Chapters",
            Width = 380,
            Height = 500,
            Background = new SolidColorBrush(Color.Parse("#111527")),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };

        var list = new StackPanel { Margin = new Thickness(12), Spacing = 8 };
        foreach (var ch in Chapters.OrderBy(c => c.Number))
        {
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var badge = new Border
            {
                Background = ch.IsRead ? Brushes.Green : Brushes.Gray,
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            row.Children.Add(badge);
            row.Children.Add(new TextBlock
            {
                Text = $"Ch {ch.Number}",
                Foreground = Brushes.White,
                Width = 70
            });
            row.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(ch.Title) ? "Untitled" : ch.Title,
                Foreground = Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (Math.Abs(ch.Number - _currentChapterNumber) < 0.001f)
            {
                row.Background = new SolidColorBrush(Color.Parse("#1f2a48"));
            }
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer { Content = list };
        overlay.Content = scroll;
        overlay.ShowDialog(this);
    }
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorOverlay.IsVisible = true;
        LoadingOverlay.IsVisible = false;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        WriteNavigationResult();
        _loadCts?.Cancel();
        _httpClient.Dispose();
        
        foreach (var bitmap in _loadedBitmaps.Values)
        {
            bitmap?.Dispose();
        }
    }

    private void WriteNavigationResult()
    {
        if (string.IsNullOrWhiteSpace(NavResultPath))
        {
            return;
        }

        var value = ChapterNavigation switch
        {
            ChapterNavigationRequest.Next => "next",
            ChapterNavigationRequest.Previous => "prev",
            _ => "none"
        };

        try
        {
            File.WriteAllText(NavResultPath, value);
        }
        catch
        {
            // ignore write errors
        }
    }
}
