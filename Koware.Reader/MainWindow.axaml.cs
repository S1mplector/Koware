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
using Avalonia.VisualTree;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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
    private readonly Dictionary<int, string> _failedPageUrls = new();
    
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
    private BookmarkAnchor? _pendingBookmarkRestore;
    private BookmarkAnchor? _pendingPositionRestore;
    private BookmarkAnchor? _activeBookmark;
    private bool _bookmarkRestoreQueued;
    private bool _positionRestoreQueued;
    private bool _bookmarkPlacementMode;
    private Point _lastPointerPosition;
    private Border? _bookmarkMarkerVisual;
    private Border? _bookmarkCursorGhostVisual;
    private readonly List<Canvas> _bookmarkLayers = new();

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

    private static string PositionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Koware", "reader-positions.json");

    private static string BookmarkPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Koware", "reader-bookmarks.json");

    private sealed record BookmarkAnchor(int Page, double OffsetRatio, double HorizontalRatio, int TotalPages, long SavedAtMs);

    public MainWindow()
    {
        InitializeComponent();
        
        // Use SocketsHttpHandler for better cross-platform compatibility and connection pooling
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 6,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
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

        _bookmarkCursorGhostVisual = CreateBookmarkVisual(isGhost: true);
        _bookmarkCursorGhostVisual.IsVisible = false;
        BookmarkOverlayCanvas.Children.Add(_bookmarkCursorGhostVisual);
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
        NormalizePages();

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
        
        // Bookmark has priority over generic chapter resume.
        var bookmark = LoadBookmark();
        if (bookmark is not null)
        {
            _pendingBookmarkRestore = bookmark;
            _activeBookmark = bookmark;
            SetCurrentPageState(bookmark.Page);
            UpdateBookmarkButtonState(true);
        }
        else if (StartPage > 1 && StartPage <= Pages.Count)
        {
            _activeBookmark = null;
            SetCurrentPageState(StartPage);
            UpdateBookmarkButtonState(false);
        }
        else
        {
            // Restore page-level position when no explicit bookmark exists.
            _activeBookmark = null;
            RestorePosition();
            UpdateBookmarkButtonState(false);
        }

        // Start loading pages
        _loadCts = new CancellationTokenSource();
        UpdateLoadingUi();
        _ = LoadAllPagesAsync(_loadCts.Token);
    }

    private void NormalizePages()
    {
        Pages = Pages
            .OrderBy(page => page.PageNumber)
            .Select((page, index) => new PageInfo(index + 1, page.Url))
            .ToList();
    }

    private void SetBrushResource(string key, string color)
    {
        if (Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = Color.Parse(color);
            return;
        }

        Resources[key] = new SolidColorBrush(Color.Parse(color));
    }

    private SolidColorBrush GetBrushResource(string key, string fallbackColor)
    {
        if (Resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            return brush;
        }

        var created = new SolidColorBrush(Color.Parse(fallbackColor));
        Resources[key] = created;
        return created;
    }

    private static string WithAlpha(string hexColor, byte alpha)
    {
        var color = Color.Parse(hexColor);
        return Color.FromArgb(alpha, color.R, color.G, color.B).ToString();
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
        _failedPageUrls.Clear();
        _bookmarkLayers.Clear();

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

                var bookmarkLayer = new Canvas
                {
                    IsHitTestVisible = false
                };
                
                grid.Children.Add(image);
                grid.Children.Add(sepiaOverlay);
                grid.Children.Add(bookmarkLayer);
                
                _pageImages.Add(image);
                _sepiaOverlays.Add(sepiaOverlay);
                _pageGrids.Add(grid);
                _bookmarkLayers.Add(bookmarkLayer);
                container.Child = grid;
                PagesContainer.Children.Add(container);
                _pagePlaceholders[page.PageNumber] = container;
            }

            // Load images in parallel (limited concurrency)
            var semaphore = new SemaphoreSlim(3); // 3 concurrent downloads
            var loadTasks = GetPageLoadOrder().Select(async page =>
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
                                _failedPageUrls.Remove(page.PageNumber);
                                _pageImages[index].Source = bitmap;
                                RestorePageContent(page.PageNumber);
                                ApplyFitMode(_pageImages[index]);
                                if (_activeBookmark?.Page == page.PageNumber)
                                {
                                    RefreshBookmarkVisual();
                                }

                                if (_pendingBookmarkRestore?.Page == page.PageNumber)
                                {
                                    QueueBookmarkRestore();
                                }
                                else if (_pendingPositionRestore?.Page == page.PageNumber)
                                {
                                    QueuePositionRestore();
                                }
                            }
                            else
                            {
                                _failedPageUrls[page.PageNumber] = page.Url;
                                ShowRetryPlaceholder(page.PageNumber, page.Url);
                            }
                        }

                        _loadedCount++;
                        UpdateLoadingUi();

                        if (_loadedCount >= totalPages)
                        {
                            SetTheme(_currentTheme);
                            QueueBookmarkRestore();
                            QueuePositionRestore();
                            RefreshBookmarkVisual();

                            if (_singlePageMode)
                            {
                                UpdatePageVisibility();
                            }

                            if (GetSuccessfulPageCount() == 0)
                            {
                                ShowError("Failed to load any pages.");
                            }
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

    private async Task<Bitmap?> LoadImageAsync(string url, CancellationToken cancellationToken, int maxRetries = 3)
    {
        // Handle local file paths (file:// URIs) for offline/downloaded manga
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            try
            {
                var localPath = uri.LocalPath;
                if (File.Exists(localPath))
                {
                    await using var fileStream = File.OpenRead(localPath);
                    using var memoryStream = new MemoryStream();
                    await fileStream.CopyToAsync(memoryStream, cancellationToken);
                    memoryStream.Position = 0;
                    return new Bitmap(memoryStream);
                }
            }
            catch
            {
                // Fall through to return null
            }
            return null;
        }
        
        // Handle HTTP/HTTPS URLs for online manga with retry logic
        for (int attempt = 0; attempt < maxRetries; attempt++)
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
            catch (OperationCanceledException)
            {
                throw; // Don't retry on cancellation
            }
            catch when (attempt < maxRetries - 1)
            {
                // Exponential backoff: 500ms, 1s, 2s
                await Task.Delay(TimeSpan.FromMilliseconds(500 * Math.Pow(2, attempt)), cancellationToken);
            }
            catch
            {
                // Final attempt failed
            }
        }
        
        return null;
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
                    _failedPageUrls.Remove(pageNumber);
                    RestorePageContent(pageNumber);
                    image.Source = bitmap;
                    ApplyFitMode(image);
                    if (_pendingBookmarkRestore?.Page == pageNumber)
                    {
                        QueueBookmarkRestore();
                    }
                    else if (_pendingPositionRestore?.Page == pageNumber)
                    {
                        QueuePositionRestore();
                    }
                    UpdateLoadingUi();
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
                    Foreground = GetBrushResource("ReaderMuted", "#94a3b8"),
                    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center
                },
                retryButton
            }
        };
    }

    private IEnumerable<PageInfo> GetPageLoadOrder()
    {
        var preferredPage = Math.Clamp(_currentPage, 1, Math.Max(1, Pages.Count));
        return Pages
            .OrderBy(page => Math.Abs(page.PageNumber - preferredPage))
            .ThenBy(page => page.PageNumber);
    }

    private int GetSuccessfulPageCount() => _loadedBitmaps.Values.Count(bitmap => bitmap is not null);

    private bool IsPageLoaded(int pageNumber) =>
        pageNumber >= 1 &&
        pageNumber <= Pages.Count &&
        _loadedBitmaps.TryGetValue(pageNumber, out var bitmap) &&
        bitmap is not null;

    private bool IsCurrentViewReady()
    {
        if (Pages.Count == 0)
        {
            return false;
        }

        var currentPage = Math.Clamp(_currentPage, 1, Pages.Count);
        if (IsPageLoaded(currentPage))
        {
            return true;
        }

        if (_doublePageMode)
        {
            var spreadStart = ((currentPage - 1) / 2) * 2 + 1;
            return IsPageLoaded(spreadStart) || IsPageLoaded(spreadStart + 1);
        }

        return false;
    }

    private void UpdateLoadingUi()
    {
        var totalPages = Math.Max(1, Pages.Count);
        var completedPages = Math.Min(_loadedCount, totalPages);
        var successfulPages = GetSuccessfulPageCount();
        var failedPages = _failedPageUrls.Count;
        var progress = (completedPages * 100.0) / totalPages;

        LoadingProgress.Text = $"{completedPages} / {totalPages}";
        LoadingProgressBar.Value = progress;

        LoadingHudProgressBar.Value = progress;
        LoadingHudText.Text = failedPages > 0
            ? $"Ready: {successfulPages}/{totalPages} • Failed: {failedPages}"
            : $"Ready: {successfulPages}/{totalPages}";

        RetryFailedPagesButton.IsVisible = failedPages > 0;
        RetryFailedPagesButton.Content = failedPages == 1 ? "Retry Failed" : $"Retry Failed ({failedPages})";

        var currentViewReady = IsCurrentViewReady();
        LoadingOverlay.IsVisible = !currentViewReady && completedPages < totalPages;
        LoadingHud.IsVisible = currentViewReady
            ? completedPages < totalPages || failedPages > 0
            : completedPages >= totalPages && failedPages > 0;
    }

    private void RestorePageContent(int pageNumber)
    {
        if (!_pagePlaceholders.TryGetValue(pageNumber, out var container))
        {
            return;
        }

        var pageIndex = pageNumber - 1;
        if (pageIndex < 0 || pageIndex >= _pageGrids.Count)
        {
            return;
        }

        var pageGrid = _pageGrids[pageIndex];
        if (!ReferenceEquals(container.Child, pageGrid))
        {
            DetachFromCurrentParent(pageGrid);
            container.Child = pageGrid;
        }
    }

    private static void DetachFromCurrentParent(Control control)
    {
        switch (control.Parent)
        {
            case null:
                return;
            case Border border when ReferenceEquals(border.Child, control):
                border.Child = null;
                return;
            case Panel panel:
                panel.Children.Remove(control);
                return;
        }
    }

    private async void OnRetryFailedPagesClick(object? sender, RoutedEventArgs e)
    {
        if (_failedPageUrls.Count == 0 || sender is not Button retryButton)
        {
            return;
        }

        retryButton.IsEnabled = false;

        try
        {
            foreach (var (pageNumber, url) in _failedPageUrls.ToList())
            {
                var bitmap = await LoadImageAsync(url, CancellationToken.None);
                if (bitmap is null)
                {
                    continue;
                }

                var pageIndex = pageNumber - 1;
                if (pageIndex < 0 || pageIndex >= _pageImages.Count)
                {
                    continue;
                }

                _loadedBitmaps[pageNumber] = bitmap;
                _failedPageUrls.Remove(pageNumber);
                RestorePageContent(pageNumber);
                _pageImages[pageIndex].Source = bitmap;
                ApplyFitMode(_pageImages[pageIndex]);

                if (_pendingBookmarkRestore?.Page == pageNumber)
                {
                    QueueBookmarkRestore();
                }
                else if (_pendingPositionRestore?.Page == pageNumber)
                {
                    QueuePositionRestore();
                }
            }
        }
        finally
        {
            retryButton.IsEnabled = true;
            UpdateLoadingUi();
        }
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

        RefreshBookmarkVisual();
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

        SetCurrentPageState(page, updateSlider);
        
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

    private void SetCurrentPageState(int page, bool updateSlider = true)
    {
        _currentPage = page;
        if (updateSlider)
        {
            PageSlider.Value = page;
        }

        UpdatePageIndicator();
        UpdateNavButtons();
    }

    private void UpdateBookmarkButtonState(bool hasBookmark)
    {
        if (hasBookmark)
        {
            if (!BookmarkButton.Classes.Contains("active"))
            {
                BookmarkButton.Classes.Add("active");
            }
        }
        else
        {
            BookmarkButton.Classes.Remove("active");
        }

        if (_bookmarkPlacementMode)
        {
            if (!BookmarkButton.Classes.Contains("placing"))
            {
                BookmarkButton.Classes.Add("placing");
            }
        }
        else
        {
            BookmarkButton.Classes.Remove("placing");
        }
    }

    private void UpdatePageIndicator()
    {
        PageIndicator.Text = $"{_currentPage} / {Pages.Count}";
    }

    private BookmarkAnchor CaptureViewportAnchor(double viewportAnchorRatio = 0.35)
    {
        var defaultPage = Math.Clamp(_currentPage, 1, Math.Max(1, Pages.Count));
        var anchorY = Math.Max(0, ScrollViewer.Viewport.Height * viewportAnchorRatio);

        if (!TryResolveAnchorAtViewportY(anchorY, out var page, out var ratio))
        {
            page = defaultPage;
            ratio = 0;
        }

        return new BookmarkAnchor(
            page,
            Math.Clamp(ratio, 0, 1),
            0.5,
            Pages.Count,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private void ExecuteWithPreservedAnchor(Action action)
    {
        BookmarkAnchor? anchor = null;
        if (Pages.Count > 0 && _loadedCount > 0 && IsCurrentViewReady())
        {
            anchor = CaptureViewportAnchor();
            if (anchor.Page != _currentPage)
            {
                SetCurrentPageState(anchor.Page);
            }
        }

        action();

        if (anchor is not null)
        {
            QueueViewportAnchorRestore(anchor);
        }
    }

    private void QueueViewportAnchorRestore(BookmarkAnchor anchor, int attempt = 0)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (TryApplyBookmark(anchor))
            {
                RefreshBookmarkVisual();
                return;
            }

            if (attempt < 4)
            {
                QueueViewportAnchorRestore(anchor, attempt + 1);
                return;
            }

            SetCurrentPageState(anchor.Page);
            if (_singlePageMode)
            {
                UpdatePageVisibility();
            }
            else if (anchor.Page - 1 < _pageImages.Count)
            {
                _pageImages[anchor.Page - 1].BringIntoView();
            }
        }, DispatcherPriority.Render);
    }
    
    private void ShowToast(string text)
    {
        PageToastText.Text = text;
        PageToast.IsVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static bool TryGetPositionHotkey(Key key, out int tenth)
    {
        tenth = key switch
        {
            Key.D1 or Key.NumPad1 => 1,
            Key.D2 or Key.NumPad2 => 2,
            Key.D3 or Key.NumPad3 => 3,
            Key.D4 or Key.NumPad4 => 4,
            Key.D5 or Key.NumPad5 => 5,
            Key.D6 or Key.NumPad6 => 6,
            Key.D7 or Key.NumPad7 => 7,
            Key.D8 or Key.NumPad8 => 8,
            Key.D9 or Key.NumPad9 => 9,
            _ => 0
        };

        return tenth > 0;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // RTL-aware navigation
        var navRight = _rtlMode ? -1 : 1;
        var navLeft = _rtlMode ? 1 : -1;
        var step = _doublePageMode ? 2 : 1;

        if (Pages.Count > 0 && TryGetPositionHotkey(e.Key, out var tenth))
        {
            var targetPage = Math.Clamp((int)Math.Ceiling(Pages.Count * (tenth / 10.0)), 1, Pages.Count);
            NavigateToPage(targetPage, showToast: true);
            e.Handled = true;
            return;
        }
        
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

            case Key.PageUp:
                NavigateToPage(_currentPage + (navLeft * step), showToast: true);
                e.Handled = true;
                break;

            case Key.PageDown:
                NavigateToPage(_currentPage + (navRight * step), showToast: true);
                e.Handled = true;
                break;

            case Key.Space when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                NavigateToPage(_currentPage + (navLeft * step), showToast: true);
                e.Handled = true;
                break;

            case Key.Space:
                NavigateToPage(_currentPage + (navRight * step), showToast: true);
                e.Handled = true;
                break;
                
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
                
            case Key.Escape:
                if (_bookmarkPlacementMode)
                {
                    CancelBookmarkPlacement(showToast: true);
                }
                else if (_chaptersOpen)
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

            case Key.M:
                ToggleMode();
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

            case Key.B when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                if (_bookmarkPlacementMode)
                {
                    CancelBookmarkPlacement(showToast: true);
                }
                else
                {
                    BeginBookmarkPlacement();
                }
                e.Handled = true;
                break;

            // Bookmark save
            case Key.B:
                if (_bookmarkPlacementMode)
                {
                    CancelBookmarkPlacement(showToast: true);
                }
                else
                {
                    SaveBookmark();
                }
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
                SetCurrentPageState(bestPage);
            }
        }
        catch
        {
            // Silently ignore scroll tracking errors
        }
    }
    
    private static readonly int[] ZoomLevels = { 50, 75, 100, 125, 150, 175, 200, 250, 300 };
    
    private void ZoomIn()
    {
        var idx = Array.IndexOf(ZoomLevels, _zoomLevel);
        if (idx < 0) idx = Array.FindIndex(ZoomLevels, z => z > _zoomLevel) - 1;
        if (idx < ZoomLevels.Length - 1) SetZoom(ZoomLevels[idx + 1]);
    }
    
    private void ZoomOut()
    {
        var idx = Array.IndexOf(ZoomLevels, _zoomLevel);
        if (idx < 0) idx = Array.FindIndex(ZoomLevels, z => z >= _zoomLevel);
        if (idx > 0) SetZoom(ZoomLevels[idx - 1]);
    }
    
    private void SetZoom(int zoom)
    {
        ExecuteWithPreservedAnchor(() =>
        {
            _zoomLevel = zoom;
            ZoomText.Text = $"{zoom}%";
            ApplyFitModeToAll();
            PersistPrefs();
        });
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
        ExecuteWithPreservedAnchor(() =>
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
        });
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
            ExecuteWithPreservedAnchor(() =>
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
            });
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
        ExecuteWithPreservedAnchor(() =>
        {
            _doublePageMode = !_doublePageMode;
            DoublePageText.Text = _doublePageMode ? "2-Page" : "1-Page";
            if (_doublePageMode)
                DoublePageButton.Classes.Add("active");
            else
                DoublePageButton.Classes.Remove("active");

            RebuildPageLayout();
            if (_singlePageMode)
            {
                UpdatePageVisibility();
            }
            PersistPrefs();
        });
    }
    
    private void OnDoublePageClick(object? sender, RoutedEventArgs e)
    {
        ToggleDoublePageMode();
    }
    
    private void ToggleMode()
    {
        ExecuteWithPreservedAnchor(() =>
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
        });
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

    private void OnBookmarkClick(object? sender, RoutedEventArgs e)
    {
        if (_bookmarkPlacementMode)
        {
            CancelBookmarkPlacement(showToast: true);
        }
        else
        {
            BeginBookmarkPlacement();
        }
    }

    private void OnReaderPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_bookmarkPlacementMode)
        {
            return;
        }

        if (!e.GetCurrentPoint(ScrollViewer).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var viewportPoint = e.GetPosition(ScrollViewer);
        if (!TryCaptureBookmarkAtViewportPoint(viewportPoint, out var bookmark))
        {
            ShowToast("Click directly on a page to place bookmark");
            e.Handled = true;
            return;
        }

        SaveBookmark(bookmark);
        CancelBookmarkPlacement(showToast: false);
        e.Handled = true;
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
        
        var (bg, panelBg, borderColor, text, muted, accent, accentStrong, btnBg, btnBorder, btnHoverBg, btnHoverBorder, activeBg, trackBg, flyoutBg, pageShellBg, listHoverBg, shortcutRowBg, shortcutRowAltBg, errorAccent) = theme switch
        {
            "sepia" => ("#f4ecd8", "#e8dfc9", "#d4c5a9", "#5c4b37", "#8b7355", "#b8860b", "#9a6d0a", "#ddd4c0", "#c9bda0", "#d4c5a9", "#bfae90", "#c9b896", "#d4c5a9", "#e8dfc9", "#e8dfc9", "#ddd4c0", "#ddd4c0", "#e8dfc9", "#c2410c"),
            "light" => ("#f8fafc", "#ffffff", "#e2e8f0", "#1e293b", "#64748b", "#0284c7", "#0369a1", "#f1f5f9", "#e2e8f0", "#e2e8f0", "#cbd5e1", "#dbeafe", "#e2e8f0", "#ffffff", "#e2e8f0", "#f1f5f9", "#f1f5f9", "#e2e8f0", "#dc2626"),
            "contrast" => ("#000000", "#0a0a0a", "#333333", "#ffffff", "#cccccc", "#ffff00", "#ffd400", "#1a1a1a", "#444444", "#262626", "#5c5c5c", "#333300", "#333333", "#0a0a0a", "#1a1a1a", "#1a1a1a", "#1a1a1a", "#0f0f0f", "#ff6b6b"),
            _ => ("#0f172a", "#0f172a", "#1e293b", "#e2e8f0", "#94a3b8", "#38bdf8", "#0ea5e9", "#141e32", "#2a3a52", "#1e2d45", "#3a4a62", "#1e3a5f", "#2a2a3a", "#202442", "#101020", "#1a2438", "#0a1220", "#0f1a2a", "#f87171")
        };
        var (dangerBg, dangerBorder, dangerText) = theme switch
        {
            "sepia" => ("#5f3a31", "#c2410c", "#fed7aa"),
            "light" => ("#fee2e2", "#ef4444", "#b91c1c"),
            "contrast" => ("#330000", "#ff6b6b", "#ffd1d1"),
            _ => ("#3a1f29", "#f87171", "#fca5a5")
        };
        var chaptersButtonBg = theme switch
        {
            "sepia" => "#c9b896",
            "light" => "#dbeafe",
            "contrast" => "#333300",
            _ => "#1e3a5f"
        };
        var toastBg = WithAlpha(panelBg, 0xD9);
        var loadingOverlayBg = WithAlpha(bg, 0xCC);
        var overlayBg = WithAlpha(bg, 0xF0);
        var chaptersOverlayBg = WithAlpha(bg, 0x80);
        var hudBg = WithAlpha(panelBg, theme == "contrast" ? (byte)0xF4 : (byte)0xEA);

        // Apply to content area and window background (for zen mode edges)
        Background = new SolidColorBrush(Color.Parse(bg));
        ContentWrapper.Background = new SolidColorBrush(Color.Parse(bg));
        ScrollViewer.Background = new SolidColorBrush(Color.Parse(bg));

        SetBrushResource("ReaderBg", bg);
        SetBrushResource("ReaderPanel", panelBg);
        SetBrushResource("ReaderBorder", borderColor);
        SetBrushResource("ReaderText", text);
        SetBrushResource("ReaderMuted", muted);
        SetBrushResource("ReaderAccent", accent);
        SetBrushResource("ReaderAccentStrong", accentStrong);
        SetBrushResource("ReaderButtonBg", btnBg);
        SetBrushResource("ReaderButtonBorder", btnBorder);
        SetBrushResource("ReaderButtonHoverBg", btnHoverBg);
        SetBrushResource("ReaderButtonHoverBorder", btnHoverBorder);
        SetBrushResource("ReaderButtonActiveBg", activeBg);
        SetBrushResource("ReaderDangerBg", dangerBg);
        SetBrushResource("ReaderDangerBorder", dangerBorder);
        SetBrushResource("ReaderDangerText", dangerText);
        SetBrushResource("ReaderTrack", trackBg);
        SetBrushResource("ReaderListHoverBg", listHoverBg);
        SetBrushResource("ReaderShortcutRowBg", shortcutRowBg);
        SetBrushResource("ReaderShortcutRowAltBg", shortcutRowAltBg);
        SetBrushResource("ReaderFlyoutBg", flyoutBg);
        SetBrushResource("ReaderFlyoutBorder", borderColor);
        
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
            placeholder.Background = new SolidColorBrush(Color.Parse(pageShellBg));
        }
        
        // Update chapters panel
        ChaptersPanel.Background = new SolidColorBrush(Color.Parse(panelBg));
        ChaptersPanel.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        
        var textBrush = new SolidColorBrush(Color.Parse(text));
        var accentBrush = new SolidColorBrush(Color.Parse(accent));
        
        // Chapters button (has accent styling when active)
        ChaptersButton.Background = new SolidColorBrush(Color.Parse(chaptersButtonBg));
        ChaptersButton.BorderBrush = accentBrush;
        ChaptersButton.Foreground = accentBrush;
        
        // Loading progress bar - use theme accent color
        LoadingProgressBar.Foreground = accentBrush;
        LoadingProgressBar.Background = new SolidColorBrush(Color.Parse(trackBg));
        LoadingHudProgressBar.Foreground = accentBrush;
        LoadingHudProgressBar.Background = new SolidColorBrush(Color.Parse(trackBg));
        LoadingTitleText.Foreground = textBrush;
        LoadingHudTitle.Foreground = textBrush;
        LoadingHudText.Foreground = new SolidColorBrush(Color.Parse(muted));
        LoadingHud.Background = new SolidColorBrush(Color.Parse(hudBg));
        LoadingHud.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));

        // Zen toast and page toast - theme sensitive backgrounds
        var toastText = text;
        ZenToast.Background = new SolidColorBrush(Color.Parse(toastBg));
        ZenToastText.Foreground = new SolidColorBrush(Color.Parse(toastText));
        PageToast.Background = new SolidColorBrush(Color.Parse(toastBg));
        PageToastText.Foreground = new SolidColorBrush(Color.Parse(toastText));
        
        // Loading overlay - theme sensitive
        LoadingOverlay.Background = new SolidColorBrush(Color.Parse(loadingOverlayBg));
        LoadingProgress.Foreground = new SolidColorBrush(Color.Parse(muted));

        FitModeFlyoutPanel.Background = new SolidColorBrush(Color.Parse(flyoutBg));
        FitModeFlyoutPanel.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        ZoomFlyoutPanel.Background = new SolidColorBrush(Color.Parse(flyoutBg));
        ZoomFlyoutPanel.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
        ThemeFlyoutPanel.Background = new SolidColorBrush(Color.Parse(flyoutBg));
        ThemeFlyoutPanel.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));

        // Help overlay - theme sensitive
        var helpPanelBg = flyoutBg;
        HelpOverlay.Background = new SolidColorBrush(Color.Parse(overlayBg));
        if (HelpOverlay.Child is Border helpPanel)
        {
            helpPanel.Background = new SolidColorBrush(Color.Parse(helpPanelBg));
            ApplyThemeToHelpPanel(helpPanel, text, muted, accent, theme);
        }
        
        // Error overlay - theme sensitive
        ErrorOverlay.Background = new SolidColorBrush(Color.Parse(overlayBg));
        ErrorPanel.Background = new SolidColorBrush(Color.Parse(flyoutBg));
        ErrorIcon.Foreground = new SolidColorBrush(Color.Parse(errorAccent));
        ErrorText.Foreground = new SolidColorBrush(Color.Parse(errorAccent));
        
        // Chapters panel header and content
        if (ChaptersPanel.Child is Grid chaptersGrid)
        {
            // Header
            if (chaptersGrid.Children.Count > 0 && chaptersGrid.Children[0] is Border headerBorder)
            {
                headerBorder.BorderBrush = new SolidColorBrush(Color.Parse(borderColor));
                foreach (var child in headerBorder.GetVisualDescendants())
                {
                    if (child is TextBlock tb && tb.Text == "Chapters")
                    {
                        tb.Foreground = textBrush;
                    }
                    else if (child is Button closeBtn)
                    {
                        closeBtn.Foreground = new SolidColorBrush(Color.Parse(muted));
                    }
                }
            }
            
            // Update chapter items
            UpdateChapterItemsTheme(text, muted, accent, theme);
        }
        
        // Chapters overlay background
        ChaptersOverlay.Background = new SolidColorBrush(Color.Parse(chaptersOverlayBg));
        
        // Slider track colors
        PageSlider.Background = new SolidColorBrush(Color.Parse(trackBg));
        PageSlider.Foreground = accentBrush;
        
        PersistPrefs();
    }
    
    private void ApplyThemeToHelpPanel(Border helpPanel, string text, string muted, string accent, string theme)
    {
        var textBrush = new SolidColorBrush(Color.Parse(text));
        var mutedBrush = new SolidColorBrush(Color.Parse(muted));
        var accentBrush = new SolidColorBrush(Color.Parse(accent));
        
        var shortcutRowBg = theme switch
        {
            "sepia" => "#ddd4c0",
            "light" => "#f1f5f9",
            "contrast" => "#1a1a1a",
            _ => "#0a1220"
        };
        var shortcutRowAltBg = theme switch
        {
            "sepia" => "#e8dfc9",
            "light" => "#e2e8f0",
            "contrast" => "#0f0f0f",
            _ => "#0f1a2a"
        };
        
        foreach (var descendant in helpPanel.GetVisualDescendants())
        {
            switch (descendant)
            {
                case TextBlock tb:
                    if (tb.FontWeight == FontWeight.SemiBold && tb.FontSize >= 18)
                    {
                        // Title
                        tb.Foreground = textBrush;
                    }
                    else if (tb.FontFamily?.Name == "Consolas")
                    {
                        // Shortcut keys
                        tb.Foreground = accentBrush;
                    }
                    else if (tb.Foreground is SolidColorBrush brush && 
                             (brush.Color.ToString().Contains("888") || brush.Color.ToString().Contains("ccc")))
                    {
                        // Muted text or description
                        tb.Foreground = mutedBrush;
                    }
                    break;
                    
                case Border border when border.Classes.Contains("shortcut-row"):
                    border.Background = new SolidColorBrush(Color.Parse(
                        border.Classes.Contains("alt") ? shortcutRowAltBg : shortcutRowBg));
                    break;
                    
                case Button btn when btn.Content is StackPanel:
                    // Got it button
                    btn.Foreground = textBrush;
                    break;
            }
        }
    }
    
    private void UpdateChapterItemsTheme(string text, string muted, string accent, string theme)
    {
        var chapterBg = theme switch
        {
            "sepia" => "#ddd4c0",
            "light" => "#f1f5f9",
            "contrast" => "#1a1a1a",
            _ => "#1a2438"
        };
        var chapterCurrentBg = theme switch
        {
            "sepia" => "#c9b896",
            "light" => "#dbeafe",
            "contrast" => "#333300",
            _ => "#1e3a5f"
        };
        
        foreach (var child in ChaptersList.Children)
        {
            if (child is Border border)
            {
                var isCurrent = border.Classes.Contains("current");
                if (isCurrent)
                {
                    border.Background = new SolidColorBrush(Color.Parse(chapterCurrentBg));
                    border.BorderBrush = new SolidColorBrush(Color.Parse(accent));
                }
                
                // Update text colors within chapter items
                foreach (var descendant in border.GetVisualDescendants())
                {
                    if (descendant is TextBlock tb)
                    {
                        if (tb.FontWeight == FontWeight.Bold)
                        {
                            // Chapter number
                            tb.Foreground = new SolidColorBrush(Color.Parse(accent));
                        }
                        else if (tb.FontSize == 13)
                        {
                            // Chapter title
                            tb.Foreground = new SolidColorBrush(Color.Parse(text));
                        }
                        else if (tb.FontSize == 10)
                        {
                            // "Read" badge text
                            tb.Foreground = new SolidColorBrush(Color.Parse(accent));
                        }
                    }
                    else if (descendant is Border badge && badge.CornerRadius.TopLeft == 4)
                    {
                        // "Read" badge background
                        badge.Background = new SolidColorBrush(Color.Parse(chapterCurrentBg));
                    }
                }
            }
        }
    }

    private void OnWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        _lastPointerPosition = e.GetPosition(this);
        if (_bookmarkPlacementMode)
        {
            UpdateBookmarkGhostPosition(_lastPointerPosition);
        }

        if (_zenMode)
        {
            // In zen mode, only show UI when hovering near the top (header area)
            var headerHeight = HeaderBar.Bounds.Height + 20; // Add some margin
            if (_lastPointerPosition.Y <= headerHeight)
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

    // ===== Chapter Bookmark Persistence =====

    private string BuildPositionKey()
    {
        var title = Title ?? string.Empty;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(title));
        if (string.IsNullOrEmpty(encoded))
        {
            return "koware.reader.default";
        }

        var desiredLength = Math.Min(32, title.Length + 10);
        var safeLength = Math.Clamp(desiredLength, 1, encoded.Length);
        return encoded[..safeLength];
    }

    private string BuildBookmarkKey()
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(Title ?? string.Empty));
        return string.IsNullOrWhiteSpace(encoded)
            ? "koware.reader.bookmark.default"
            : $"koware.reader.bookmark.{encoded}";
    }

    private void BeginBookmarkPlacement()
    {
        _bookmarkPlacementMode = true;
        if (_bookmarkCursorGhostVisual is not null)
        {
            _bookmarkCursorGhostVisual.IsVisible = true;
            UpdateBookmarkGhostPosition(_lastPointerPosition);
        }

        Cursor = new Cursor(StandardCursorType.Cross);
        UpdateBookmarkButtonState(_activeBookmark is not null);
        ShowToast("Click on a page to place bookmark");
    }

    private void CancelBookmarkPlacement(bool showToast)
    {
        _bookmarkPlacementMode = false;
        if (_bookmarkCursorGhostVisual is not null)
        {
            _bookmarkCursorGhostVisual.IsVisible = false;
        }

        Cursor = null;
        UpdateBookmarkButtonState(_activeBookmark is not null);

        if (showToast)
        {
            ShowToast("Bookmark placement cancelled");
        }
    }

    private void UpdateBookmarkGhostPosition(Point position)
    {
        if (_bookmarkCursorGhostVisual is null)
        {
            return;
        }

        const double offsetX = 8;
        const double offsetY = -6;
        Canvas.SetLeft(_bookmarkCursorGhostVisual, position.X + offsetX);
        Canvas.SetTop(_bookmarkCursorGhostVisual, position.Y + offsetY);
    }

    private void SaveBookmark(BookmarkAnchor? explicitBookmark = null)
    {
        var bookmark = explicitBookmark ?? CaptureBookmarkAnchor();
        PersistBookmark(bookmark);
        _pendingBookmarkRestore = bookmark;
        _activeBookmark = bookmark;
        RefreshBookmarkVisual();
        UpdateBookmarkButtonState(true);

        if (bookmark.Page != _currentPage)
        {
            SetCurrentPageState(bookmark.Page);
        }

        var offsetPct = (int)Math.Round(bookmark.OffsetRatio * 100);
        ShowToast($"Bookmarked: {bookmark.Page}/{Pages.Count} ({offsetPct}%)");
    }

    private BookmarkAnchor CaptureBookmarkAnchor()
    {
        var defaultPage = Math.Clamp(_currentPage, 1, Math.Max(1, Pages.Count));
        const double viewportAnchorRatio = 0.35;
        var anchorY = Math.Max(0, ScrollViewer.Viewport.Height * viewportAnchorRatio);

        if (!TryResolveAnchorAtViewportY(anchorY, out var page, out var ratio))
        {
            page = defaultPage;
            ratio = 0;
        }

        return new BookmarkAnchor(
            page,
            Math.Clamp(ratio, 0, 1),
            0.5,
            Pages.Count,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    private bool TryCaptureBookmarkAtViewportPoint(Point viewportPoint, out BookmarkAnchor bookmark)
    {
        bookmark = CaptureBookmarkAnchor();

        for (int i = 0; i < _pageImages.Count; i++)
        {
            var image = _pageImages[i];
            if (!image.IsVisible || image.Bounds.Width <= 1 || image.Bounds.Height <= 1)
            {
                continue;
            }

            try
            {
                var transform = image.TransformToVisual(ScrollViewer);
                if (transform is null)
                {
                    continue;
                }

                var topLeft = transform.Value.Transform(new Point(0, 0));
                var imageRect = new Rect(topLeft, image.Bounds.Size);
                if (!imageRect.Contains(viewportPoint))
                {
                    continue;
                }

                var xRatio = Math.Clamp((viewportPoint.X - imageRect.X) / imageRect.Width, 0, 1);
                var yRatio = Math.Clamp((viewportPoint.Y - imageRect.Y) / imageRect.Height, 0, 1);
                bookmark = new BookmarkAnchor(
                    i + 1,
                    yRatio,
                    xRatio,
                    Pages.Count,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                return true;
            }
            catch
            {
                // skip invalid transforms
            }
        }

        return false;
    }

    private bool TryResolveAnchorAtViewportY(double viewportY, out int page, out double offsetRatio)
    {
        page = Math.Clamp(_currentPage, 1, Math.Max(1, Pages.Count));
        offsetRatio = 0;

        double bestDistance = double.MaxValue;
        int bestPage = page;
        double bestRatio = 0;

        for (int i = 0; i < _pageImages.Count; i++)
        {
            var image = _pageImages[i];
            if (!image.IsVisible || image.Bounds.Height <= 1)
            {
                continue;
            }

            try
            {
                var transform = image.TransformToVisual(ScrollViewer);
                if (transform is null)
                {
                    continue;
                }

                var topLeft = transform.Value.Transform(new Point(0, 0));
                var imageTop = topLeft.Y;
                var imageHeight = Math.Max(1, image.Bounds.Height);
                var imageBottom = imageTop + imageHeight;

                if (viewportY >= imageTop && viewportY <= imageBottom)
                {
                    page = i + 1;
                    offsetRatio = Math.Clamp((viewportY - imageTop) / imageHeight, 0, 1);
                    return true;
                }

                var imageCenter = imageTop + (imageHeight / 2);
                var distance = Math.Abs(imageCenter - viewportY);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestPage = i + 1;
                    bestRatio = Math.Clamp((viewportY - imageTop) / imageHeight, 0, 1);
                }
            }
            catch
            {
                // skip invalid transforms
            }
        }

        if (bestDistance < double.MaxValue)
        {
            page = bestPage;
            offsetRatio = bestRatio;
            return true;
        }

        return false;
    }

    private Border CreateBookmarkVisual(bool isGhost)
    {
        var fill = isGhost ? "#80ef4444" : "#ef4444";
        var stroke = isGhost ? "#99fecaca" : "#7f1d1d";
        return new Border
        {
            Width = 18,
            Height = 24,
            IsHitTestVisible = false,
            Opacity = isGhost ? 0.55 : 1.0,
            Child = new Avalonia.Controls.Shapes.Path
            {
                Data = Geometry.Parse("M6 3h12v18l-6-4-6 4z"),
                Stretch = Stretch.Fill,
                Fill = new SolidColorBrush(Color.Parse(fill)),
                Stroke = new SolidColorBrush(Color.Parse(stroke)),
                StrokeThickness = 1.6
            }
        };
    }

    private void RefreshBookmarkVisual()
    {
        if (_activeBookmark is not { } bookmark)
        {
            ClearBookmarkVisual();
            return;
        }

        var pageIndex = bookmark.Page - 1;
        if (pageIndex < 0 || pageIndex >= _bookmarkLayers.Count || pageIndex >= _pageImages.Count || pageIndex >= _pageGrids.Count)
        {
            ClearBookmarkVisual();
            return;
        }

        var targetLayer = _bookmarkLayers[pageIndex];
        if (_bookmarkMarkerVisual is null || !ReferenceEquals(_bookmarkMarkerVisual.Parent, targetLayer))
        {
            ClearBookmarkVisual();
            _bookmarkMarkerVisual = CreateBookmarkVisual(isGhost: false);
            targetLayer.Children.Add(_bookmarkMarkerVisual);
        }

        PositionBookmarkVisual(bookmark);
    }

    private void ClearBookmarkVisual()
    {
        if (_bookmarkMarkerVisual?.Parent is Panel panel)
        {
            panel.Children.Remove(_bookmarkMarkerVisual);
        }

        _bookmarkMarkerVisual = null;
    }

    private void PositionBookmarkVisual(BookmarkAnchor bookmark)
    {
        if (_bookmarkMarkerVisual is null)
        {
            return;
        }

        var pageIndex = bookmark.Page - 1;
        if (pageIndex < 0 || pageIndex >= _pageImages.Count || pageIndex >= _pageGrids.Count)
        {
            _bookmarkMarkerVisual.IsVisible = false;
            return;
        }

        var image = _pageImages[pageIndex];
        var grid = _pageGrids[pageIndex];
        if (image.Bounds.Width <= 1 || image.Bounds.Height <= 1)
        {
            _bookmarkMarkerVisual.IsVisible = false;
            return;
        }

        var transform = image.TransformToVisual(grid);
        if (transform is null)
        {
            _bookmarkMarkerVisual.IsVisible = false;
            return;
        }

        var topLeft = transform.Value.Transform(new Point(0, 0));
        var normalizedX = Math.Clamp(bookmark.HorizontalRatio, 0, 1);
        var normalizedY = Math.Clamp(bookmark.OffsetRatio, 0, 1);
        var x = topLeft.X + (image.Bounds.Width * normalizedX) - (_bookmarkMarkerVisual.Width / 2);
        var y = topLeft.Y + (image.Bounds.Height * normalizedY) - _bookmarkMarkerVisual.Height;

        Canvas.SetLeft(_bookmarkMarkerVisual, x);
        Canvas.SetTop(_bookmarkMarkerVisual, y);
        _bookmarkMarkerVisual.IsVisible = true;
    }

    private void QueueBookmarkRestore()
    {
        if (_bookmarkRestoreQueued || _pendingBookmarkRestore is null)
        {
            return;
        }

        _bookmarkRestoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _bookmarkRestoreQueued = false;
            if (_pendingBookmarkRestore is not { } bookmark)
            {
                return;
            }

            if (TryApplyBookmark(bookmark))
            {
                _pendingBookmarkRestore = null;
                _activeBookmark = bookmark;
                UpdateBookmarkButtonState(true);
                RefreshBookmarkVisual();
                SavePosition();
                var offsetPct = (int)Math.Round(bookmark.OffsetRatio * 100);
                ShowToast($"Resumed bookmark: {bookmark.Page}/{Pages.Count} ({offsetPct}%)");
                return;
            }

            // If dimensions are still settling, try again on the next render frame.
            if (_loadedCount < Pages.Count)
            {
                QueueBookmarkRestore();
                return;
            }

            _pendingBookmarkRestore = null;
            _activeBookmark = bookmark;
            UpdateBookmarkButtonState(true);
            RefreshBookmarkVisual();
            NavigateToPage(bookmark.Page, showToast: true);
        }, DispatcherPriority.Render);
    }

    private void QueuePositionRestore()
    {
        if (_positionRestoreQueued || _pendingPositionRestore is null || _pendingBookmarkRestore is not null)
        {
            return;
        }

        _positionRestoreQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _positionRestoreQueued = false;
            if (_pendingBookmarkRestore is not null || _pendingPositionRestore is not { } position)
            {
                return;
            }

            if (TryApplyBookmark(position))
            {
                _pendingPositionRestore = null;
                SavePosition();
                ShowToast($"Resumed: {position.Page}/{Pages.Count}");
                return;
            }

            if (_loadedCount < Pages.Count)
            {
                QueuePositionRestore();
                return;
            }

            _pendingPositionRestore = null;
            NavigateToPage(position.Page, showToast: true);
        }, DispatcherPriority.Render);
    }

    private bool TryApplyBookmark(BookmarkAnchor bookmark)
    {
        if (bookmark.Page < 1 || bookmark.Page > Pages.Count || bookmark.Page - 1 >= _pageImages.Count)
        {
            return false;
        }

        var image = _pageImages[bookmark.Page - 1];
        if (image.Bounds.Height <= 1 || ScrollViewer.Viewport.Height <= 1)
        {
            return false;
        }

        var transform = image.TransformToVisual(ScrollViewer);
        if (transform is null)
        {
            return false;
        }

        var previousTracking = _isScrollTracking;
        try
        {
            const double viewportAnchorRatio = 0.35;
            var topLeft = transform.Value.Transform(new Point(0, 0));
            var absoluteY = ScrollViewer.Offset.Y + topLeft.Y + (image.Bounds.Height * Math.Clamp(bookmark.OffsetRatio, 0, 1));
            var targetY = absoluteY - (ScrollViewer.Viewport.Height * viewportAnchorRatio);
            var maxY = Math.Max(0, ScrollViewer.Extent.Height - ScrollViewer.Viewport.Height);
            var clampedY = Math.Clamp(targetY, 0, maxY);

            _isScrollTracking = false;
            ScrollViewer.Offset = new Vector(ScrollViewer.Offset.X, clampedY);
            SetCurrentPageState(bookmark.Page);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _isScrollTracking = previousTracking;
        }
    }

    private void PersistBookmark(BookmarkAnchor bookmark)
    {
        try
        {
            var dir = Path.GetDirectoryName(BookmarkPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            Dictionary<string, JsonElement> bookmarks = new();
            if (File.Exists(BookmarkPath))
            {
                try
                {
                    var existing = File.ReadAllText(BookmarkPath);
                    bookmarks = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing) ?? new();
                }
                catch
                {
                    // ignore malformed bookmark store
                }
            }

            var key = BuildBookmarkKey();
            var bookmarkData = new
            {
                page = bookmark.Page,
                offsetRatio = bookmark.OffsetRatio,
                horizontalRatio = bookmark.HorizontalRatio,
                total = bookmark.TotalPages,
                savedAt = bookmark.SavedAtMs
            };

            using var bookmarkDoc = JsonDocument.Parse(JsonSerializer.Serialize(bookmarkData));
            bookmarks[key] = bookmarkDoc.RootElement.Clone();

            File.WriteAllText(BookmarkPath, JsonSerializer.Serialize(bookmarks));
        }
        catch
        {
            // ignore bookmark save errors
        }
    }

    private BookmarkAnchor? LoadBookmark()
    {
        try
        {
            if (!File.Exists(BookmarkPath))
            {
                return null;
            }

            var json = File.ReadAllText(BookmarkPath);
            using var doc = JsonDocument.Parse(json);
            var key = BuildBookmarkKey();
            if (!doc.RootElement.TryGetProperty(key, out var bookmark))
            {
                return null;
            }

            if (!bookmark.TryGetProperty("page", out var pageEl) || !pageEl.TryGetInt32(out var page))
            {
                return null;
            }

            var total = bookmark.TryGetProperty("total", out var totalEl) && totalEl.TryGetInt32(out var totalPages)
                ? totalPages
                : Pages.Count;

            if (total != Pages.Count || page < 1 || page > Pages.Count)
            {
                return null;
            }

            var ratio = bookmark.TryGetProperty("offsetRatio", out var ratioEl) && ratioEl.TryGetDouble(out var offsetRatio)
                ? offsetRatio
                : 0;

            var horizontalRatio = bookmark.TryGetProperty("horizontalRatio", out var horizontalRatioEl) && horizontalRatioEl.TryGetDouble(out var xRatio)
                ? xRatio
                : 0.5;

            var savedAt = bookmark.TryGetProperty("savedAt", out var savedAtEl) && savedAtEl.TryGetInt64(out var savedAtMs)
                ? savedAtMs
                : 0;

            return new BookmarkAnchor(page, Math.Clamp(ratio, 0, 1), Math.Clamp(horizontalRatio, 0, 1), total, savedAt);
        }
        catch
        {
            return null;
        }
    }
    
    private void SavePosition()
    {
        if (Pages.Count == 0)
        {
            return;
        }

        try
        {
            var dir = Path.GetDirectoryName(PositionPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            Dictionary<string, JsonElement> positions = new();
            if (File.Exists(PositionPath))
            {
                try
                {
                    var existing = File.ReadAllText(PositionPath);
                    positions = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(existing) ?? new();
                }
                catch { }
            }
            
            var key = BuildPositionKey();
            var position = CaptureViewportAnchor();
            var posData = new
            {
                page = position.Page,
                offsetRatio = position.OffsetRatio,
                horizontalRatio = position.HorizontalRatio,
                total = position.TotalPages,
                savedAt = position.SavedAtMs
            };
            
            // Update with new position (as raw JSON)
            var updatedJson = JsonSerializer.Serialize(posData);
            using var newDoc = JsonDocument.Parse(updatedJson);
            positions[key] = newDoc.RootElement.Clone();
            
            File.WriteAllText(PositionPath, JsonSerializer.Serialize(positions));
        }
        catch
        {
            // ignore save errors
        }
    }
    
    private void RestorePosition()
    {
        _pendingPositionRestore = null;

        try
        {
            if (!File.Exists(PositionPath)) return;
            
            var json = File.ReadAllText(PositionPath);
            using var doc = JsonDocument.Parse(json);
            
            var key = BuildPositionKey();
            
            if (doc.RootElement.TryGetProperty(key, out var pos))
            {
                var page = pos.GetProperty("page").GetInt32();
                var total = pos.GetProperty("total").GetInt32();
                var savedAt = pos.GetProperty("savedAt").GetInt64();
                var ratio = pos.TryGetProperty("offsetRatio", out var ratioEl) && ratioEl.TryGetDouble(out var offsetRatio)
                    ? offsetRatio
                    : 0;
                var horizontalRatio = pos.TryGetProperty("horizontalRatio", out var horizontalRatioEl) && horizontalRatioEl.TryGetDouble(out var xRatio)
                    ? xRatio
                    : 0.5;
                
                // Only restore if saved within last 30 days and same chapter
                var thirtyDaysMs = 30L * 24 * 60 * 60 * 1000;
                var isRecent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedAt < thirtyDaysMs;
                var hasMeaningfulPosition = page > 1 || ratio > 0.01;
                if (isRecent && total == Pages.Count && page >= 1 && page <= Pages.Count && hasMeaningfulPosition)
                {
                    _pendingPositionRestore = new BookmarkAnchor(
                        page,
                        Math.Clamp(ratio, 0, 1),
                        Math.Clamp(horizontalRatio, 0, 1),
                        total,
                        savedAt);
                    SetCurrentPageState(page);
                }
            }
        }
        catch
        {
            // ignore restore errors
        }
    }
}
