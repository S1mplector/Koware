// Author: Ilgaz Mehmetoğlu
// Main window for the cross-platform Koware manga reader.
// Rewritten to match Windows WebView reader behavior.
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
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Koware.Reader;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient;
    private readonly List<Image> _pageImages = new();
    private readonly Dictionary<int, Bitmap?> _loadedBitmaps = new();
    private readonly Dictionary<int, Border> _pagePlaceholders = new();
    private readonly List<Border> _sepiaOverlays = new();
    private readonly List<Grid> _pageGrids = new(); // Grid wrapper containing image + sepia overlay
    private CancellationTokenSource? _loadCts;
    
    // State
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private int _currentPage = 1;
    private FitMode _fitMode = FitMode.FitWidth;
    private int _zoomLevel = 100; // 100, 125, 150, 175, 200
    private bool _showHelp;
    private bool _isScrollTracking = true;
    private bool _singlePageMode; // true = page-by-page, false = scroll mode
    private bool _rtlMode;
    private bool _doublePageMode;
    private DateTime _lastScrollTime = DateTime.MinValue;
    private bool _autoHideUi;
    private readonly DispatcherTimer _uiHideTimer;
    private readonly DispatcherTimer _toastTimer;
    private int _loadedCount;
    private float _currentChapterNumber;
    private string _currentTheme = "dark";
    private bool _chaptersOpen;
    private bool _zenMode;
    private readonly DispatcherTimer _zenHideTimer;
    private readonly DispatcherTimer _zenToastTimer;

    public List<PageInfo> Pages { get; set; } = new();
    public List<ChapterInfo> Chapters { get; set; } = new();
    public string? HttpReferer { get; set; }
    public string? HttpUserAgent { get; set; }
    public ChapterNavigationRequest ChapterNavigation { get; private set; } = ChapterNavigationRequest.None;
    public float? TargetChapterNumber { get; private set; }
    public string? NavResultPath { get; set; }
    public int StartPage { get; set; } = 1;

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

    // Preference storage paths
    private static string PrefsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Koware", "reader-prefs.json");
    
    private string PositionKey => $"koware.reader.pos.{Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Title ?? ""))[..Math.Min(32, (Title ?? "").Length)]}";

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
        PointerMoved += OnWindowPointerMoved;
        
        // Toast timer
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            PageToast.IsVisible = false;
        };
        
        // Zen mode hide timer (auto-hide UI after 2.5s)
        _zenHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2500) };
        _zenHideTimer.Tick += (_, _) =>
        {
            _zenHideTimer.Stop();
            if (_zenMode) SetUiVisibility(false);
        };
        
        // Zen toast timer
        _zenToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _zenToastTimer.Tick += (_, _) =>
        {
            _zenToastTimer.Stop();
            ZenToast.IsVisible = false;
        };
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
        _currentChapterNumber = Chapters.FirstOrDefault(c => !c.IsRead)?.Number
                                 ?? Chapters.LastOrDefault()?.Number
                                 ?? 0;
        
        PageSlider.Maximum = Pages.Count;
        PageSlider.Value = 1;
        UpdatePageIndicator();
        
        // Load saved preferences
        LoadPrefs();
        
        // Setup chapter navigation
        InitChapters();
        
        // Navigate to start page if specified (for resume functionality)
        if (StartPage > 1 && StartPage <= Pages.Count)
        {
            _currentPage = StartPage;
            PageSlider.Value = StartPage;
            UpdatePageIndicator();
        }
        else
        {
            // Restore position if saved
            RestorePosition();
        }

        // Start loading pages
        _loadCts = new CancellationTokenSource();
        _ = LoadAllPagesAsync(_loadCts.Token);
    }
    
    private void InitChapters()
    {
        if (Chapters.Count == 0)
        {
            // Show but disable chapter controls when no chapters available
            ChaptersButton.IsEnabled = false;
            ChaptersButton.Opacity = 0.5;
            PrevChapterButton.IsEnabled = false;
            NextChapterButton.IsEnabled = false;
            return;
        }
        
        // Build chapters list
        ChaptersList.Children.Clear();
        var currentIdx = GetCurrentChapterIndex();
        
        foreach (var (ch, idx) in Chapters.OrderBy(c => c.Number).Select((c, i) => (c, i)))
        {
            var isCurrent = idx == currentIdx;
            var item = new Border
            {
                Classes = { "chapter-item" },
                Cursor = new Cursor(StandardCursorType.Hand),
                Tag = idx
            };
            if (isCurrent) item.Classes.Add("current");
            if (ch.IsRead) item.Opacity = 0.6;
            
            var chapterNumStr = ch.Number % 1 == 0 ? $"{(int)ch.Number}" : $"{ch.Number:0.#}";
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new TextBlock 
            { 
                Text = $"Ch. {chapterNumStr}", 
                FontWeight = FontWeight.Bold, 
                FontSize = 13, 
                MinWidth = 50,
                Foreground = new SolidColorBrush(Color.Parse("#38bdf8"))
            });
            row.Children.Add(new TextBlock 
            { 
                Text = string.IsNullOrWhiteSpace(ch.Title) ? $"Chapter {chapterNumStr}" : ch.Title,
                FontSize = 13,
                Foreground = new SolidColorBrush(Color.Parse("#e2e8f0")),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            if (ch.IsRead)
            {
                row.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1e3a5f")),
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 2),
                    Child = new TextBlock 
                    { 
                        Text = "Read", 
                        FontSize = 10, 
                        Foreground = new SolidColorBrush(Color.Parse("#38bdf8"))
                    }
                });
            }
            
            item.Child = row;
            item.PointerPressed += (s, _) =>
            {
                if (s is Border b && b.Tag is int targetIdx)
                {
                    NavigateToChapter(targetIdx);
                }
            };
            ChaptersList.Children.Add(item);
        }
        
        UpdateChapterNavButtons();
    }
    
    private int GetCurrentChapterIndex()
    {
        var idx = Chapters.Select((c, i) => (c, i)).FirstOrDefault(x => Math.Abs(x.c.Number - _currentChapterNumber) < 0.001f).i;
        return idx >= 0 ? idx : 0;
    }
    
    private void UpdateChapterNavButtons()
    {
        var idx = GetCurrentChapterIndex();
        PrevChapterButton.IsEnabled = idx > 0;
        NextChapterButton.IsEnabled = idx < Chapters.Count - 1;
    }
    
    private void NavigateToChapter(int targetIdx)
    {
        var currentIdx = GetCurrentChapterIndex();
        if (targetIdx == currentIdx)
        {
            ToggleChaptersPanel(false);
            return;
        }
        
        // Store the target chapter number for direct jump
        var orderedChapters = Chapters.OrderBy(c => c.Number).ToList();
        if (targetIdx >= 0 && targetIdx < orderedChapters.Count)
        {
            TargetChapterNumber = orderedChapters[targetIdx].Number;
        }
        WriteNavigationResult();
        Close();
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

                // Use a Grid to layer the image and sepia overlay
                var grid = new Grid();
                
                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    Tag = page.PageNumber,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                
                // Sepia overlay - semi-transparent warm tint that covers only the image
                // Uses a Rectangle with binding to match the image's actual rendered size
                var sepiaOverlay = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#c4956a")),
                    Opacity = 0,
                    IsHitTestVisible = false,
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
                };
                
                // Bind overlay size to image size
                sepiaOverlay.Bind(WidthProperty, new Avalonia.Data.Binding("Bounds.Width") { Source = image });
                sepiaOverlay.Bind(HeightProperty, new Avalonia.Data.Binding("Bounds.Height") { Source = image });
                
                grid.Children.Add(image);
                grid.Children.Add(sepiaOverlay);
                
                _pageImages.Add(image);
                _sepiaOverlays.Add(sepiaOverlay);
                _pageGrids.Add(grid);
                container.Child = grid;
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

                        if (_loadedCount >= totalPages)
                        {
                            LoadingOverlay.IsVisible = false;
                            
                            // Apply single-page mode if enabled
                            if (_singlePageMode)
                            {
                                UpdatePageVisibility();
                            }
                            
                            // Apply theme to page containers
                            SetTheme(_currentTheme);
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


    private void ApplyFitMode(Image image)
    {
        if (image.Source is not Bitmap bitmap) return;

        var containerWidth = ScrollViewer.Bounds.Width - 40; // Padding
        var containerHeight = ScrollViewer.Bounds.Height - 40;
        var scale = _zoomLevel / 100.0;

        switch (_fitMode)
        {
            case FitMode.FitWidth:
                image.Width = Math.Min(containerWidth, 900) * scale;
                image.Height = double.NaN;
                break;
            case FitMode.FitHeight:
                image.Width = double.NaN;
                image.Height = containerHeight * scale;
                break;
            case FitMode.Original:
                image.Width = bitmap.PixelSize.Width * scale;
                image.Height = bitmap.PixelSize.Height * scale;
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
        var step = _doublePageMode ? 2 : 1;
        var delta = _rtlMode ? step : -step;
        NavigateToPage(_currentPage + delta);
    }

    private void OnNextClick(object? sender, RoutedEventArgs e)
    {
        var step = _doublePageMode ? 2 : 1;
        var delta = _rtlMode ? -step : step;
        NavigateToPage(_currentPage + delta);
    }
    
    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void OnPageSliderChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        var page = (int)e.NewValue;
        if (page != _currentPage)
        {
            NavigateToPage(page, updateSlider: false, showToast: true);
        }
    }

    private void NavigateToPage(int page, bool updateSlider = true, bool showToast = false)
    {
        if (page < 1 || page > Pages.Count) return;
        
        _currentPage = page;
        
        if (updateSlider)
        {
            PageSlider.Value = page;
        }
        
        UpdatePageIndicator();
        UpdateNavButtons();
        
        if (showToast)
        {
            ShowToast($"{page} / {Pages.Count}");
        }
        
        if (_singlePageMode)
        {
            // Update visibility for single-page mode
            UpdatePageVisibility();
        }
        else if (page - 1 < _pageImages.Count)
        {
            // Scroll to page in scroll mode
            var target = _pageImages[page - 1];
            target.BringIntoView();
        }
        
        // Save position periodically
        SavePosition();
    }

    private void UpdatePageIndicator()
    {
        PageIndicator.Text = $"{_currentPage} / {Pages.Count}";
    }
    
    private void ShowToast(string text)
    {
        PageToastText.Text = text;
        PageToast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // RTL-aware navigation
        var navRight = _rtlMode ? -1 : 1;
        var navLeft = _rtlMode ? 1 : -1;
        var step = _doublePageMode ? 2 : 1;
        
        switch (e.Key)
        {
            case Key.Left:
            case Key.A:
                NavigateToPage(_currentPage + (navLeft * step), showToast: true);
                e.Handled = true;
                break;
                
            case Key.Right:
            case Key.D:
                NavigateToPage(_currentPage + (navRight * step), showToast: true);
                e.Handled = true;
                break;
                
            case Key.Home:
                NavigateToPage(1, showToast: true);
                e.Handled = true;
                break;
                
            case Key.End:
                NavigateToPage(Pages.Count, showToast: true);
                e.Handled = true;
                break;
                
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
                
            case Key.Escape:
                if (_chaptersOpen)
                {
                    ToggleChaptersPanel(false);
                }
                else if (_showHelp)
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
                SetZoom(100);
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
                
            // RTL mode
            case Key.R:
                ToggleRtl();
                e.Handled = true;
                break;
                
            // Double-page mode
            case Key.P:
                ToggleDoublePageMode();
                e.Handled = true;
                break;
                
            // Chapters panel
            case Key.C:
                if (Chapters.Count > 0)
                {
                    ToggleChaptersPanel();
                }
                e.Handled = true;
                break;
                
            // Zen mode
            case Key.Z:
                ToggleZenMode();
                e.Handled = true;
                break;
        }
    }
    
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        try
        {
            if (!_isScrollTracking || _pageImages.Count == 0 || _singlePageMode) return;
            
            // Throttle scroll updates
            var now = DateTime.UtcNow;
            if ((now - _lastScrollTime).TotalMilliseconds < 100) return;
            _lastScrollTime = now;
            
            // Find which page is most visible using transform
            var viewportHeight = ScrollViewer.Viewport.Height;
            var viewportCenter = viewportHeight / 2;
            
            int bestPage = _currentPage;
            double bestDistance = double.MaxValue;
            
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var image = _pageImages[i];
                if (image.Parent is not Visual parent) continue;
                
                try
                {
                    // Transform image bounds to ScrollViewer coordinates
                    var transform = image.TransformToVisual(ScrollViewer);
                    if (transform == null) continue;
                    
                    var topLeft = transform.Value.Transform(new Point(0, 0));
                    var imageCenter = topLeft.Y + image.Bounds.Height / 2;
                    var distance = Math.Abs(imageCenter - viewportCenter);
                    
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestPage = i + 1;
                    }
                }
                catch
                {
                    // Skip if transform fails
                }
            }
            
            if (bestPage != _currentPage)
            {
                _currentPage = bestPage;
                PageSlider.Value = bestPage;
                UpdatePageIndicator();
            }
        }
        catch
        {
            // Silently ignore scroll tracking errors
        }
    }
    
    private void ZoomIn()
    {
        var levels = new[] { 100, 125, 150, 175, 200 };
        var idx = Array.IndexOf(levels, _zoomLevel);
        if (idx < levels.Length - 1) SetZoom(levels[idx + 1]);
    }
    
    private void ZoomOut()
    {
        var levels = new[] { 100, 125, 150, 175, 200 };
        var idx = Array.IndexOf(levels, _zoomLevel);
        if (idx > 0) SetZoom(levels[idx - 1]);
    }
    
    private void SetZoom(int zoom)
    {
        _zoomLevel = zoom;
        ZoomText.Text = $"{zoom}%";
        ApplyFitModeToAll();
        PersistPrefs();
    }
    
    private void OnZoomSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && int.TryParse(tag, out var zoom))
        {
            SetZoom(zoom);
        }
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
        
        UpdateFitModeUi();
        ApplyFitModeToAll();
        PersistPrefs();
    }
    
    private void UpdateFitModeUi()
    {
        FitModeText.Text = _fitMode switch
        {
            FitMode.FitWidth => "Fit Width",
            FitMode.FitHeight => "Fit Height",
            FitMode.Original => "Original",
            _ => "Fit Width"
        };
    }
    
    private void OnFitModeSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _fitMode = tag switch
            {
                "width" => FitMode.FitWidth,
                "height" => FitMode.FitHeight,
                "original" => FitMode.Original,
                _ => FitMode.FitWidth
            };
            UpdateFitModeUi();
            ApplyFitModeToAll();
            PersistPrefs();
        }
    }
    
    private void ToggleHelp()
    {
        _showHelp = !_showHelp;
        HelpOverlay.IsVisible = _showHelp;
    }
    
    private void ToggleRtl()
    {
        _rtlMode = !_rtlMode;
        if (_rtlMode)
            RtlButton.Classes.Add("active");
        else
            RtlButton.Classes.Remove("active");
        
        // Update navigation button state
        UpdateNavButtons();
        PersistPrefs();
    }
    
    private void OnRtlClick(object? sender, RoutedEventArgs e)
    {
        ToggleRtl();
    }
    
    private void UpdateNavButtons()
    {
        // In RTL mode, swap the visual meaning of prev/next
        if (_rtlMode)
        {
            PrevPageButton.IsEnabled = _currentPage < Pages.Count;
            NextPageButton.IsEnabled = _currentPage > 1;
        }
        else
        {
            PrevPageButton.IsEnabled = _currentPage > 1;
            NextPageButton.IsEnabled = _currentPage < Pages.Count;
        }
    }
    
    private void ToggleDoublePageMode()
    {
        _doublePageMode = !_doublePageMode;
        DoublePageText.Text = _doublePageMode ? "2-Page" : "1-Page";
        if (_doublePageMode)
            DoublePageButton.Classes.Add("active");
        else
            DoublePageButton.Classes.Remove("active");
        
        RebuildPageLayout();
        PersistPrefs();
    }
    
    private void OnDoublePageClick(object? sender, RoutedEventArgs e)
    {
        ToggleDoublePageMode();
    }
    
    private void ToggleMode()
    {
        _singlePageMode = !_singlePageMode;
        ModeText.Text = _singlePageMode ? "Page" : "Scroll";
        
        if (_singlePageMode)
        {
            ModeButton.Classes.Add("active");
        }
        else
        {
            ModeButton.Classes.Remove("active");
        }
        
        UpdatePageVisibility();
        PersistPrefs();
    }
    
    private void UpdatePageVisibility()
    {
        if (_singlePageMode)
        {
            // Single-page mode: show only current page(s)
            var step = _doublePageMode ? 2 : 1;
            var startPage = ((_currentPage - 1) / step) * step + 1;
            
            for (int i = 0; i < _pageImages.Count; i++)
            {
                var pageNum = i + 1;
                var shouldShow = pageNum >= startPage && pageNum < startPage + step;
                
                if (_pageImages[i].Parent is Border border)
                {
                    border.IsVisible = shouldShow;
                }
                else if (_pageImages[i].Parent is Panel panel && panel.Parent is Control parentControl)
                {
                    // For double-page mode rows
                    var rowStart = (i / 2) * 2 + 1;
                    parentControl.IsVisible = _currentPage >= rowStart && _currentPage < rowStart + 2;
                }
            }
            
            // Reset scroll position
            ScrollViewer.Offset = new Vector(0, 0);
        }
        else
        {
            // Scroll mode: show all pages
            foreach (var image in _pageImages)
            {
                if (image.Parent is Border border)
                {
                    border.IsVisible = true;
                }
                else if (image.Parent is Panel panel && panel.Parent is Control parentControl)
                {
                    parentControl.IsVisible = true;
                }
            }
            
            // Scroll to current page
            if (_currentPage - 1 < _pageImages.Count)
            {
                _pageImages[_currentPage - 1].BringIntoView();
            }
        }
    }
    
    private void OnModeClick(object? sender, RoutedEventArgs e)
    {
        ToggleMode();
    }
    
    private void RebuildPageLayout()
    {
        // First, detach all page grids from their current parents
        foreach (var grid in _pageGrids)
        {
            if (grid.Parent is Border border)
            {
                border.Child = null;
            }
            else if (grid.Parent is Panel panel)
            {
                panel.Children.Remove(grid);
            }
        }
        
        PagesContainer.Children.Clear();
        _pagePlaceholders.Clear();
        
        // Get current theme background color
        var containerBg = _currentTheme switch
        {
            "sepia" => "#e8dfc9",
            "light" => "#e2e8f0",
            "contrast" => "#1a1a1a",
            _ => "#101020"
        };
        
        if (_doublePageMode)
        {
            // Double-page layout: two grids per row in borders
            PagesContainer.Orientation = Avalonia.Layout.Orientation.Vertical;
            
            for (int i = 0; i < _pageGrids.Count; i += 2)
            {
                var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center };
                
                var border1 = new Border
                {
                    Background = new SolidColorBrush(Color.Parse(containerBg)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Margin = new Thickness(2)
                };
                border1.Child = _pageGrids[i];
                row.Children.Add(border1);
                
                if (i + 1 < _pageGrids.Count)
                {
                    var border2 = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse(containerBg)),
                        CornerRadius = new CornerRadius(8),
                        Padding = new Thickness(6),
                        Margin = new Thickness(2)
                    };
                    border2.Child = _pageGrids[i + 1];
                    row.Children.Add(border2);
                }
                
                PagesContainer.Children.Add(row);
            }
        }
        else
        {
            // Single-page layout with borders
            PagesContainer.Orientation = Avalonia.Layout.Orientation.Vertical;
            for (int i = 0; i < _pageGrids.Count; i++)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse(containerBg)),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(6),
                    Margin = new Thickness(0, 2, 0, 2)
                };
                border.Child = _pageGrids[i];
                PagesContainer.Children.Add(border);
                _pagePlaceholders[i + 1] = border;
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
        WriteNavigationResult();
        Close();
    }

    private void OnNextChapterClick(object? sender, RoutedEventArgs e)
    {
        ChapterNavigation = ChapterNavigationRequest.Next;
        WriteNavigationResult();
        Close();
    }
    
    private void OnThemeSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            SetTheme(tag);
        }
    }

    private void SetTheme(string theme)
    {
        _currentTheme = theme;
        ThemeText.Text = theme switch
        {
            "dark" => "Dark",
            "sepia" => "Sepia",
            "light" => "Light",
            "contrast" => "High Contrast",
            _ => "Dark"
        };
        
        // Define theme colors
        var (bg, panelBg, borderColor, text, muted, accent, btnBg, btnBorder) = theme switch
        {
            "sepia" => ("#f4ecd8", "#e8dfc9", "#d4c5a9", "#5c4b37", "#8b7355", "#b8860b", "#ddd4c0", "#c9bda0"),
            "light" => ("#f8fafc", "#ffffff", "#e2e8f0", "#1e293b", "#64748b", "#0284c7", "#f1f5f9", "#e2e8f0"),
            "contrast" => ("#000000", "#0a0a0a", "#333333", "#ffffff", "#cccccc", "#ffff00", "#1a1a1a", "#444444"),
            _ => ("#0f172a", "#0f172a", "#1e293b", "#e2e8f0", "#94a3b8", "#38bdf8", "#141e32", "#2a3a52") // dark
        };

        // Apply to content area
        ContentWrapper.Background = new SolidColorBrush(Color.Parse(bg));
        ScrollViewer.Background = new SolidColorBrush(Color.Parse(bg));
        
        // Apply to toolbars
        HeaderBar.Background = new SolidColorBrush(Color.Parse(panelBg));
        HeaderBar.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        FooterBar.Background = new SolidColorBrush(Color.Parse(panelBg));
        FooterBar.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        
        // Apply to text elements
        TitleText.Foreground = new SolidColorBrush(Color.Parse(text));
        PageIndicator.Foreground = new SolidColorBrush(Color.Parse(muted));
        
        // Apply sepia filter overlay to images (simulates CSS sepia(30%) brightness(0.95))
        if (theme == "sepia")
        {
            // Show sepia overlay with warm brownish tint to simulate sepia filter
            foreach (var overlay in _sepiaOverlays)
            {
                overlay.Background = new SolidColorBrush(Color.Parse("#c4956a")); // Warm sepia brown
                overlay.Opacity = 0.25; // Matches approximately sepia(30%)
            }
        }
        else
        {
            // Hide sepia overlay
            foreach (var overlay in _sepiaOverlays)
            {
                overlay.Opacity = 0;
            }
        }
        
        // Update page container backgrounds for theme
        foreach (var placeholder in _pagePlaceholders.Values)
        {
            placeholder.Background = theme switch
            {
                "sepia" => new SolidColorBrush(Color.Parse("#e8dfc9")),
                "light" => new SolidColorBrush(Color.Parse("#e2e8f0")),
                "contrast" => new SolidColorBrush(Color.Parse("#1a1a1a")),
                _ => new SolidColorBrush(Color.Parse("#101020"))
            };
        }
        
        // Update chapters panel
        ChaptersPanel.Background = new SolidColorBrush(Color.Parse(panelBg));
        ChaptersPanel.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        
        // Apply theme to toolbar buttons
        var btnBgBrush = new SolidColorBrush(Color.Parse(btnBg));
        var btnBorderBrush = new SolidColorBrush(Color.Parse(btnBorder));
        var textBrush = new SolidColorBrush(Color.Parse(text));
        var accentBrush = new SolidColorBrush(Color.Parse(accent));
        
        // Header toolbar buttons
        foreach (var btn in new[] { RtlButton, DoublePageButton, FitModeButton, ZoomButton, ModeButton, ThemeButton, ZenButton })
        {
            btn.Background = btnBgBrush;
            btn.BorderBrush = btnBorderBrush;
            btn.Foreground = textBrush;
        }
        
        // Chapters button (has accent styling when active)
        ChaptersButton.Background = new SolidColorBrush(Color.Parse(theme switch
        {
            "sepia" => "#c9b896",
            "light" => "#dbeafe",
            "contrast" => "#333300",
            _ => "#1e3a5f"
        }));
        ChaptersButton.BorderBrush = accentBrush;
        ChaptersButton.Foreground = accentBrush;
        
        // Footer nav buttons
        foreach (var btn in new[] { PrevChapterButton, PrevPageButton, NextPageButton, NextChapterButton })
        {
            btn.Background = btnBgBrush;
            btn.BorderBrush = btnBorderBrush;
            btn.Foreground = textBrush;
        }
        
        // Loading progress bar - use theme accent color
        LoadingProgressBar.Foreground = accentBrush;
        LoadingProgressBar.Background = new SolidColorBrush(Color.Parse(theme switch
        {
            "sepia" => "#d4c5a9",
            "light" => "#e2e8f0",
            "contrast" => "#333333",
            _ => "#2a2a3a"
        }));
        
        // Zen toast and page toast - theme sensitive backgrounds
        var toastBg = theme switch
        {
            "sepia" => "#d9e8dfc9",
            "light" => "#d9f1f5f9",
            "contrast" => "#d9000000",
            _ => "#d9000000"
        };
        var toastText = theme switch
        {
            "sepia" => "#5c4b37",
            "light" => "#1e293b",
            "contrast" => "#ffffff",
            _ => "#e2e8f0"
        };
        ZenToast.Background = new SolidColorBrush(Color.Parse(toastBg));
        ZenToastText.Foreground = new SolidColorBrush(Color.Parse(toastText));
        PageToast.Background = new SolidColorBrush(Color.Parse(toastBg));
        PageToastText.Foreground = new SolidColorBrush(Color.Parse(toastText));
        
        PersistPrefs();
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_zenMode)
        {
            // In zen mode, only show UI when hovering near the top (header area)
            var pos = e.GetPosition(this);
            var headerHeight = HeaderBar.Bounds.Height + 20; // Add some margin
            if (pos.Y <= headerHeight)
            {
                SetUiVisibility(true);
                _zenHideTimer.Stop();
                _zenHideTimer.Start();
            }
        }
        else if (_autoHideUi)
        {
            SetUiVisibility(true);
            _uiHideTimer.Stop();
            _uiHideTimer.Start();
        }
    }
    
    private void ToggleZenMode()
    {
        _zenMode = !_zenMode;
        
        if (_zenMode)
        {
            ZenButton.Classes.Add("active");
            ShowZenToast("Zen Mode ON — Hover top to show controls");
            SetUiVisibility(false); // Immediately hide UI
            _zenHideTimer.Start();
        }
        else
        {
            ZenButton.Classes.Remove("active");
            ShowZenToast("Zen Mode OFF");
            _zenHideTimer.Stop();
            SetUiVisibility(true);
        }
        
        PersistPrefs();
    }
    
    private void OnZenClick(object? sender, RoutedEventArgs e)
    {
        ToggleZenMode();
    }
    
    private void ShowZenToast(string text)
    {
        ZenToastText.Text = text;
        ZenToast.IsVisible = true;
        _zenToastTimer.Stop();
        _zenToastTimer.Start();
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
        ToggleChaptersPanel(true);
    }
    
    private void OnChaptersPanelClose(object? sender, RoutedEventArgs e)
    {
        ToggleChaptersPanel(false);
    }
    
    private void OnChaptersOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        ToggleChaptersPanel(false);
    }
    
    private void ToggleChaptersPanel() => ToggleChaptersPanel(!_chaptersOpen);
    
    private void ToggleChaptersPanel(bool open)
    {
        _chaptersOpen = open;
        ChaptersPanel.IsVisible = open;
        ChaptersOverlay.IsVisible = open;
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
        SavePosition();
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

        string nav;
        if (TargetChapterNumber.HasValue)
        {
            nav = $"goto:{TargetChapterNumber.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }
        else
        {
            nav = ChapterNavigation switch
            {
                ChapterNavigationRequest.Next => "next",
                ChapterNavigationRequest.Previous => "prev",
                _ => "none"
            };
        }

        // Include current page and chapter for resume functionality
        // Format: nav:page:chapter (e.g., "none:15:1.5")
        var value = $"{nav}:{_currentPage}:{_currentChapterNumber}";

        try
        {
            File.WriteAllText(NavResultPath, value);
        }
        catch
        {
            // ignore write errors
        }
    }
    
    // ===== Preference Persistence =====
    
    private void LoadPrefs()
    {
        try
        {
            if (!File.Exists(PrefsPath)) return;
            var json = File.ReadAllText(PrefsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            if (root.TryGetProperty("fit", out var fit))
            {
                _fitMode = fit.GetString() switch
                {
                    "width" => FitMode.FitWidth,
                    "height" => FitMode.FitHeight,
                    "original" => FitMode.Original,
                    _ => FitMode.FitWidth
                };
                UpdateFitModeUi();
            }
            
            if (root.TryGetProperty("zoom", out var zoom))
            {
                _zoomLevel = zoom.GetInt32();
                ZoomText.Text = $"{_zoomLevel}%";
            }
            
            if (root.TryGetProperty("theme", out var theme))
            {
                SetTheme(theme.GetString() ?? "dark");
            }
            
            if (root.TryGetProperty("singlePage", out var sp) && sp.GetBoolean())
            {
                _singlePageMode = true;
                ModeText.Text = "Page";
                ModeButton.Classes.Add("active");
            }
            
            if (root.TryGetProperty("rtl", out var rtl) && rtl.GetBoolean())
            {
                _rtlMode = true;
                RtlButton.Classes.Add("active");
            }
            
            if (root.TryGetProperty("doublePage", out var dp) && dp.GetBoolean())
            {
                _doublePageMode = true;
                DoublePageText.Text = "2-Page";
                DoublePageButton.Classes.Add("active");
            }
            
            if (root.TryGetProperty("zenMode", out var zen) && zen.GetBoolean())
            {
                _zenMode = true;
                ZenButton.Classes.Add("active");
                _zenHideTimer.Start();
            }
        }
        catch
        {
            // ignore load errors
        }
    }
    
    private void PersistPrefs()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            var fitStr = _fitMode switch
            {
                FitMode.FitWidth => "width",
                FitMode.FitHeight => "height",
                FitMode.Original => "original",
                _ => "width"
            };
            
            var prefs = new
            {
                fit = fitStr,
                zoom = _zoomLevel,
                theme = _currentTheme,
                singlePage = _singlePageMode,
                rtl = _rtlMode,
                doublePage = _doublePageMode,
                zenMode = _zenMode
            };
            
            File.WriteAllText(PrefsPath, JsonSerializer.Serialize(prefs));
        }
        catch
        {
            // ignore save errors
        }
    }
    
    private void SavePosition()
    {
        try
        {
            var posPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Koware", "reader-positions.json");
            
            var dir = Path.GetDirectoryName(posPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            Dictionary<string, JsonElement> positions = new();
            if (File.Exists(posPath))
            {
                try
                {
                    var existing = File.ReadAllText(posPath);
                    positions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing) ?? new();
                }
                catch { }
            }
            
            var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Title ?? ""))[..Math.Min(32, (Title ?? "").Length + 10)];
            var posData = new
            {
                page = _currentPage,
                total = Pages.Count,
                savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
            // Update with new position (as raw JSON)
            var updatedJson = JsonSerializer.Serialize(posData);
            using var newDoc = JsonDocument.Parse(updatedJson);
            positions[key] = newDoc.RootElement.Clone();
            
            File.WriteAllText(posPath, JsonSerializer.Serialize(positions));
        }
        catch
        {
            // ignore save errors
        }
    }
    
    private void RestorePosition()
    {
        try
        {
            var posPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Koware", "reader-positions.json");
            
            if (!File.Exists(posPath)) return;
            
            var json = File.ReadAllText(posPath);
            using var doc = JsonDocument.Parse(json);
            
            var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Title ?? ""))[..Math.Min(32, (Title ?? "").Length + 10)];
            
            if (doc.RootElement.TryGetProperty(key, out var pos))
            {
                var page = pos.GetProperty("page").GetInt32();
                var total = pos.GetProperty("total").GetInt32();
                var savedAt = pos.GetProperty("savedAt").GetInt64();
                
                // Only restore if saved within last 30 days and same chapter
                var thirtyDaysMs = 30L * 24 * 60 * 60 * 1000;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedAt < thirtyDaysMs && total == Pages.Count && page > 1)
                {
                    NavigateToPage(page, showToast: true);
                }
            }
        }
        catch
        {
            // ignore restore errors
        }
    }
}
