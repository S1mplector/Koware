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

namespace Koware.Reader;

public partial class MainWindow : Window
{
    private readonly HttpClient _httpClient;
    private readonly List<Image> _pageImages = new();
    private readonly Dictionary<int, Bitmap?> _loadedBitmaps = new();
    private CancellationTokenSource? _loadCts;
    
    private bool _isFullscreen;
    private WindowState _previousWindowState;
    private int _currentPage = 1;
    private FitMode _fitMode = FitMode.FitWidth;

    public List<PageInfo> Pages { get; set; } = new();
    public string? HttpReferer { get; set; }
    public string? HttpUserAgent { get; set; }

    private enum FitMode
    {
        FitWidth,
        FitHeight,
        Original
    }

    public MainWindow()
    {
        InitializeComponent();
        
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.Add("Accept", "image/*,*/*");
        
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        SizeChanged += OnSizeChanged;
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
        PageSlider.Maximum = Pages.Count;
        PageSlider.Value = 1;
        UpdatePageIndicator();

        // Start loading pages
        _loadCts = new CancellationTokenSource();
        _ = LoadAllPagesAsync(_loadCts.Token);
    }

    private async Task LoadAllPagesAsync(CancellationToken cancellationToken)
    {
        var loadedCount = 0;
        var totalPages = Pages.Count;

        try
        {
            // Create placeholder images for all pages
            foreach (var page in Pages.OrderBy(p => p.PageNumber))
            {
                var image = new Image
                {
                    Stretch = Stretch.Uniform,
                    Margin = new Thickness(0, 2, 0, 2),
                    Tag = page.PageNumber
                };
                _pageImages.Add(image);
                PagesContainer.Children.Add(image);
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
                            _pageImages[index].Source = bitmap;
                            ApplyFitMode(_pageImages[index]);
                        }
                        
                        loadedCount++;
                        LoadingProgress.Text = $"{loadedCount} / {totalPages}";
                        
                        if (loadedCount >= totalPages)
                        {
                            LoadingOverlay.IsVisible = false;
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

    private void ApplyFitMode(Image image)
    {
        if (image.Source is not Bitmap bitmap) return;

        var containerWidth = ScrollViewer.Bounds.Width - 40; // Padding
        var containerHeight = ScrollViewer.Bounds.Height - 40;

        switch (_fitMode)
        {
            case FitMode.FitWidth:
                image.Width = containerWidth;
                image.Height = double.NaN;
                break;
            case FitMode.FitHeight:
                image.Width = double.NaN;
                image.Height = containerHeight;
                break;
            case FitMode.Original:
                image.Width = bitmap.PixelSize.Width;
                image.Height = bitmap.PixelSize.Height;
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
                if (_isFullscreen)
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
        }
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
        _loadCts?.Cancel();
        _httpClient.Dispose();
        
        foreach (var bitmap in _loadedBitmaps.Values)
        {
            bitmap?.Dispose();
        }
    }
}
