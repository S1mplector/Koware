// Author: Ilgaz Mehmetoğlu
// Main window for the cross-platform Koware video player using LibVLCSharp.
// Rewritten to match Windows WebView player behavior.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Koware.Player;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private DispatcherTimer? _progressTimer;
    private DispatcherTimer? _controlsHideTimer;
    private DispatcherTimer? _skipIndicatorTimer;
    private bool _isFullscreen;
    private bool _isDraggingProgress;
    private WindowState _previousWindowState;
    private double _savedVolume = 100;
    private bool _subtitlesEnabled = true;
    private float _playbackSpeed = 1.0f;
    
    // Subtitle settings
    private int _subtitleFontSize = 22;
    private string _subtitleFontFamily = "Segoe UI";
    private int _subtitleBgOpacity = 45;
    
    // Exit codes for episode navigation
    public const int ExitCodeNormal = 0;
    public const int ExitCodePrevEpisode = 10;
    public const int ExitCodeNextEpisode = 11;
    private int _exitCode = ExitCodeNormal;

    public string? StreamUrl { get; set; }
    public string? HttpReferer { get; set; }
    public string? HttpUserAgent { get; set; }
    public string? SubtitleUrl { get; set; }

    public MediaPlayer? MediaPlayer
    {
        get => _mediaPlayer;
        private set
        {
            _mediaPlayer = value;
            OnPropertyChanged();
        }
    }

    public new event PropertyChangedEventHandler? PropertyChanged;
    
    // Preference storage path
    private static string PrefsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Koware", "player-prefs.json");

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
        
        // Controls auto-hide timer
        _controlsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _controlsHideTimer.Tick += (_, _) =>
        {
            if (_isFullscreen && _mediaPlayer?.IsPlaying == true)
            {
                SetControlsVisibility(false);
            }
        };
        
        // Skip indicator timer
        _skipIndicatorTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _skipIndicatorTimer.Tick += (_, _) =>
        {
            _skipIndicatorTimer.Stop();
            SkipIndicator.IsVisible = false;
        };
    }
    
    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        ShowControls();
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        InitializePlayer();
    }

    private void InitializePlayer()
    {
        try
        {
            Core.Initialize();
            
            _libVLC = new LibVLC(
                "--no-xlib",
                "--quiet",
                "--no-video-title-show"
            );
            
            MediaPlayer = new MediaPlayer(_libVLC);
            
            // Set up event handlers
            _mediaPlayer!.Playing += OnPlaying;
            _mediaPlayer.Paused += OnPaused;
            _mediaPlayer.Stopped += OnStopped;
            _mediaPlayer.EndReached += OnEndReached;
            _mediaPlayer.EncounteredError += OnError;
            _mediaPlayer.TimeChanged += OnTimeChanged;
            _mediaPlayer.LengthChanged += OnLengthChanged;
            _mediaPlayer.Buffering += OnBuffering;
            
            // Set up progress timer
            _progressTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _progressTimer.Tick += OnProgressTimerTick;
            
            // Load preferences
            LoadPrefs();
            
            // Set title
            TitleText.Text = Title;
            
            // Start playback if URL provided
            if (!string.IsNullOrWhiteSpace(StreamUrl))
            {
                PlayStream(StreamUrl);
            }
            else
            {
                ShowError("No stream URL provided.");
            }
        }
        catch (Exception ex)
        {
            ShowError($"Failed to initialize player: {ex.Message}");
        }
    }

    private void PlayStream(string url)
    {
        try
        {
            StatusText.Text = "Connecting...";
            
            // Build media options
            var options = new List<string>();
            
            if (!string.IsNullOrWhiteSpace(HttpReferer))
            {
                options.Add($":http-referrer={HttpReferer}");
            }
            
            if (!string.IsNullOrWhiteSpace(HttpUserAgent))
            {
                options.Add($":http-user-agent={HttpUserAgent}");
            }
            
            // Enable hardware decoding
            options.Add(":avcodec-hw=any");
            
            _media = new Media(_libVLC!, new Uri(url), options.ToArray());
            
            // Add subtitles if provided
            if (!string.IsNullOrWhiteSpace(SubtitleUrl))
            {
                _media.AddSlave(MediaSlaveType.Subtitle, 0, SubtitleUrl);
            }
            
            _mediaPlayer!.Play(_media);
        }
        catch (Exception ex)
        {
            ShowError($"Failed to play: {ex.Message}");
        }
    }

    private void OnPlaying(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LoadingOverlay.IsVisible = false;
            // Pause icon (two vertical bars)
            PlayPauseIcon.Data = Geometry.Parse("M6 4h4v16H6V4zm8 0h4v16h-4V4z");
            _progressTimer?.Start();
            
            // Apply saved playback speed
            if (_playbackSpeed != 1.0f)
            {
                _mediaPlayer?.SetRate(_playbackSpeed);
            }
            
            // Restore position on first play
            RestorePosition();
        });
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Play icon (triangle)
            PlayPauseIcon.Data = Geometry.Parse("M5 3L19 12L5 21Z");
        });
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            // Play icon (triangle)
            PlayPauseIcon.Data = Geometry.Parse("M5 3L19 12L5 21Z");
            _progressTimer?.Stop();
        });
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _progressTimer?.Stop();
            Close();
        });
    }

    private void OnError(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ShowError("Playback error occurred.");
        });
    }

    private void OnBuffering(object? sender, MediaPlayerBufferingEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Cache < 100)
            {
                StatusText.Text = $"Buffering... {e.Cache:F0}%";
                LoadingOverlay.IsVisible = true;
            }
            else
            {
                LoadingOverlay.IsVisible = false;
            }
        });
    }

    private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs e)
    {
        // Handled by timer for smoother updates
    }

    private void OnLengthChanged(object? sender, MediaPlayerLengthChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateTimeDisplay();
        });
    }

    private void OnProgressTimerTick(object? sender, EventArgs e)
    {
        if (_isDraggingProgress || _mediaPlayer is null) return;
        
        var time = _mediaPlayer.Time;
        var length = _mediaPlayer.Length;
        
        if (length > 0)
        {
            ProgressSlider.Value = (time / (double)length) * 100;
        }
        
        UpdateTimeDisplay();
    }

    private void UpdateTimeDisplay()
    {
        if (_mediaPlayer is null) return;
        
        var current = TimeSpan.FromMilliseconds(_mediaPlayer.Time);
        var total = TimeSpan.FromMilliseconds(_mediaPlayer.Length);
        
        TimeDisplay.Text = $"{FormatTime(current)} / {FormatTime(total)}";
    }

    private static string FormatTime(TimeSpan time)
    {
        return time.Hours > 0
            ? $"{time.Hours:D2}:{time.Minutes:D2}:{time.Seconds:D2}"
            : $"{time.Minutes:D2}:{time.Seconds:D2}";
    }

    private void OnPlayPauseClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        
        if (_mediaPlayer.IsPlaying)
        {
            _mediaPlayer.Pause();
        }
        else
        {
            _mediaPlayer.Play();
        }
    }

    private void OnProgressPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isDraggingProgress = true;
        
        // Seek immediately on click
        if (_mediaPlayer is not null && _mediaPlayer.Length > 0)
        {
            var point = e.GetPosition(ProgressSlider);
            var ratio = Math.Clamp(point.X / ProgressSlider.Bounds.Width, 0, 1);
            ProgressSlider.Value = ratio * 100;
            _mediaPlayer.Position = (float)ratio;
        }
    }

    private void OnProgressPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mediaPlayer is null || _mediaPlayer.Length <= 0)
        {
            _isDraggingProgress = false;
            return;
        }
        
        var position = ProgressSlider.Value / 100.0;
        _mediaPlayer.Position = (float)position;
        _isDraggingProgress = false;
    }
    
    private void OnRewindClick(object? sender, RoutedEventArgs e)
    {
        Skip(-10);
    }
    
    private void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        Skip(10);
    }
    
    private void OnPrevEpisodeClick(object? sender, RoutedEventArgs e)
    {
        _exitCode = ExitCodePrevEpisode;
        Close();
    }
    
    private void OnNextEpisodeClick(object? sender, RoutedEventArgs e)
    {
        _exitCode = ExitCodeNextEpisode;
        Close();
    }

    private void OnVolumeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Volume = (int)e.NewValue;
        UpdateMuteIcon();
    }

    private void OnMuteClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Mute = !_mediaPlayer.Mute;
        UpdateMuteIcon();
    }

    private void UpdateMuteIcon()
    {
        if (_mediaPlayer is null) return;
        var isMuted = _mediaPlayer.Mute || _mediaPlayer.Volume == 0;
        VolumeIcon.Data = isMuted
            ? Geometry.Parse("M16.5 12c0-1.77-1.02-3.29-2.5-4.03v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51C20.63 14.91 21 13.5 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3L3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06c1.38-.31 2.63-.95 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4L9.91 6.09 12 8.18V4z")
            : Geometry.Parse("M3 9v6h4l5 5V4L7 9H3zm13.5 3c0-1.77-1.02-3.29-2.5-4.03v8.06c1.48-.74 2.5-2.26 2.5-4.03zM14 3.23v2.06c2.89.86 5 3.54 5 6.71s-2.11 5.85-5 6.71v2.06c4.01-.91 7-4.49 7-8.77s-2.99-7.86-7-8.77z");
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        ToggleFullscreen();
    }
    
    private void OnVideoClicked(object? sender, PointerPressedEventArgs e)
    {
        // Toggle play/pause on video click
        if (_mediaPlayer is null) return;
        if (_mediaPlayer.IsPlaying)
            _mediaPlayer.Pause();
        else
            _mediaPlayer.Play();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowState = _previousWindowState;
            _isFullscreen = false;
            FullscreenIcon.Data = Geometry.Parse("M8 3H5a2 2 0 0 0-2 2v3m18 0V5a2 2 0 0 0-2-2h-3m0 18h3a2 2 0 0 0 2-2v-3M3 16v3a2 2 0 0 0 2 2h3");
        }
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
            _isFullscreen = true;
            FullscreenIcon.Data = Geometry.Parse("M4 14h6v6m10-10h-6V4m0 6 7-7M3 21l7-7");
        }
    }
    
    private void SetControlsVisibility(bool show)
    {
        HeaderBar.Opacity = show ? 1 : 0;
        ControlsPanel.Opacity = show ? 1 : 0;
        TopControls.Opacity = show ? 1 : 0;
        HeaderBar.IsHitTestVisible = show;
        ControlsPanel.IsHitTestVisible = show;
        TopControls.IsHitTestVisible = show;
    }
    
    private void ShowControls()
    {
        SetControlsVisibility(true);
        _controlsHideTimer?.Stop();
        _controlsHideTimer?.Start();
    }
    
    private void ShowSkipIndicator(string text)
    {
        SkipIndicatorText.Text = text;
        SkipIndicator.IsVisible = true;
        _skipIndicatorTimer?.Stop();
        _skipIndicatorTimer?.Start();
    }
    
    private void Skip(int seconds)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Time = Math.Max(0, Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + seconds * 1000));
        ShowSkipIndicator(seconds > 0 ? $"+{seconds}s" : $"{seconds}s");
    }
    
    // ===== Speed Control =====
    
    private void OnSpeedSelected(object? sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag && float.TryParse(tag, out var speed))
        {
            SetSpeed(speed);
        }
    }
    
    private void SetSpeed(float speed)
    {
        _playbackSpeed = speed;
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetRate(speed);
        }
        SpeedText.Text = speed == 1 ? "1x" : $"{speed}x";
        SavePrefs();
    }
    
    // ===== Subtitle Controls =====
    
    private void OnCcClick(object? sender, RoutedEventArgs e)
    {
        _subtitlesEnabled = !_subtitlesEnabled;
        if (_subtitlesEnabled)
        {
            CcButton.Content = "CC On";
            CcButton.Classes.Add("active");
        }
        else
        {
            CcButton.Content = "CC Off";
            CcButton.Classes.Remove("active");
        }
        
        // Toggle subtitle track in VLC
        if (_mediaPlayer is not null)
        {
            _mediaPlayer.SetSpu(_subtitlesEnabled ? 1 : -1);
        }
    }
    
    private void OnFontSizeChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _subtitleFontSize = (int)e.NewValue;
        FontSizeValue.Text = $"{_subtitleFontSize}px";
        SavePrefs();
    }
    
    private void OnFontFamilyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FontFamilyCombo.SelectedItem is ComboBoxItem item)
        {
            _subtitleFontFamily = item.Content?.ToString() ?? "Segoe UI";
            SavePrefs();
        }
    }
    
    private void OnBgOpacityChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        _subtitleBgOpacity = (int)e.NewValue;
        BgOpacityValue.Text = $"{_subtitleBgOpacity}%";
        SavePrefs();
    }
    
    // ===== Buffer Bar Update =====
    
    private void UpdateBufferBar()
    {
        // VLC doesn't provide buffer info easily, so we estimate based on cache
        // For now, just show a placeholder
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_mediaPlayer is null) return;
        
        switch (e.Key)
        {
            case Key.Space:
            case Key.K:
                OnPlayPauseClick(null, null!);
                e.Handled = true;
                break;
                
            case Key.Left:
            case Key.J:
                Skip(-10);
                e.Handled = true;
                break;
                
            case Key.Right:
            case Key.L:
                Skip(10);
                e.Handled = true;
                break;
                
            case Key.Up:
                _mediaPlayer.Volume = Math.Min(100, _mediaPlayer.Volume + 10);
                VolumeSlider.Value = _mediaPlayer.Volume;
                e.Handled = true;
                break;
                
            case Key.Down:
                _mediaPlayer.Volume = Math.Max(0, _mediaPlayer.Volume - 10);
                VolumeSlider.Value = _mediaPlayer.Volume;
                e.Handled = true;
                break;
                
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;
                
            case Key.M:
                OnMuteClick(null, null!);
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
                
            // Number keys for position jump
            case Key.D0: case Key.NumPad0:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = 0;
                e.Handled = true;
                break;
            case Key.D1: case Key.NumPad1:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.1);
                e.Handled = true;
                break;
            case Key.D2: case Key.NumPad2:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.2);
                e.Handled = true;
                break;
            case Key.D3: case Key.NumPad3:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.3);
                e.Handled = true;
                break;
            case Key.D4: case Key.NumPad4:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.4);
                e.Handled = true;
                break;
            case Key.D5: case Key.NumPad5:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.5);
                e.Handled = true;
                break;
            case Key.D6: case Key.NumPad6:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.6);
                e.Handled = true;
                break;
            case Key.D7: case Key.NumPad7:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.7);
                e.Handled = true;
                break;
            case Key.D8: case Key.NumPad8:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.8);
                e.Handled = true;
                break;
            case Key.D9: case Key.NumPad9:
                if (_mediaPlayer.Length > 0) _mediaPlayer.Time = (long)(_mediaPlayer.Length * 0.9);
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
        SavePosition();
        _progressTimer?.Stop();
        _controlsHideTimer?.Stop();
        _skipIndicatorTimer?.Stop();
        _mediaPlayer?.Stop();
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        
        // Set environment exit code for CLI to detect episode navigation
        Environment.ExitCode = _exitCode;
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
            
            if (root.TryGetProperty("speed", out var speed))
            {
                _playbackSpeed = speed.GetSingle();
                SpeedText.Text = _playbackSpeed == 1 ? "1x" : $"{_playbackSpeed}x";
            }
            
            if (root.TryGetProperty("subtitleFontSize", out var fs))
            {
                _subtitleFontSize = fs.GetInt32();
                FontSizeSlider.Value = _subtitleFontSize;
                FontSizeValue.Text = $"{_subtitleFontSize}px";
            }
            
            if (root.TryGetProperty("subtitleBgOpacity", out var bg))
            {
                _subtitleBgOpacity = bg.GetInt32();
                BgOpacitySlider.Value = _subtitleBgOpacity;
                BgOpacityValue.Text = $"{_subtitleBgOpacity}%";
            }
        }
        catch
        {
            // ignore load errors
        }
    }
    
    private void SavePrefs()
    {
        try
        {
            var dir = Path.GetDirectoryName(PrefsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            
            var prefs = new
            {
                speed = _playbackSpeed,
                subtitleFontSize = _subtitleFontSize,
                subtitleFontFamily = _subtitleFontFamily,
                subtitleBgOpacity = _subtitleBgOpacity
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
        if (_mediaPlayer is null || _mediaPlayer.Length <= 0 || _mediaPlayer.Time < 5000) return;
        
        try
        {
            var posPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Koware", "player-positions.json");
            
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
            
            var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(StreamUrl ?? ""))[..Math.Min(32, (StreamUrl ?? "").Length + 10)];
            var posData = new
            {
                time = _mediaPlayer.Time,
                duration = _mediaPlayer.Length,
                savedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            };
            
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
        if (_mediaPlayer is null || string.IsNullOrWhiteSpace(StreamUrl)) return;
        
        try
        {
            var posPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Koware", "player-positions.json");
            
            if (!File.Exists(posPath)) return;
            
            var json = File.ReadAllText(posPath);
            using var doc = JsonDocument.Parse(json);
            
            var key = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(StreamUrl))[..Math.Min(32, StreamUrl.Length + 10)];
            
            if (doc.RootElement.TryGetProperty(key, out var pos))
            {
                var time = pos.GetProperty("time").GetInt64();
                var duration = pos.GetProperty("duration").GetInt64();
                var savedAt = pos.GetProperty("savedAt").GetInt64();
                
                // Only restore if saved within last 7 days and not near the end
                var sevenDaysMs = 7L * 24 * 60 * 60 * 1000;
                if (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - savedAt < sevenDaysMs && time < duration - 30000)
                {
                    _mediaPlayer.Time = time;
                    ShowSkipIndicator($"Resumed at {FormatTime(TimeSpan.FromMilliseconds(time))}");
                }
            }
        }
        catch
        {
            // ignore restore errors
        }
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
