// Author: Ilgaz Mehmetoğlu
// Main window for the cross-platform Koware video player using LibVLCSharp.
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Koware.Player;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private LibVLC? _libVLC;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    private DispatcherTimer? _progressTimer;
    private bool _isFullscreen;
    private bool _isDraggingProgress;
    private WindowState _previousWindowState;
    
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

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        
        Opened += OnWindowOpened;
        Closing += OnWindowClosing;
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
            PlayPauseButton.Content = "II";
            _progressTimer?.Start();
        });
    }

    private void OnPaused(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PlayPauseButton.Content = ">";
        });
    }

    private void OnStopped(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            PlayPauseButton.Content = ">";
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
        if (_mediaPlayer is null) return;
        _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 10000);
    }
    
    private void OnForwardClick(object? sender, RoutedEventArgs e)
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 10000);
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
        MuteButton.Content = _mediaPlayer.Mute || _mediaPlayer.Volume == 0 ? "🔇" : "🔊";
    }

    private void OnFullscreenClick(object? sender, RoutedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void OnVideoDoubleTapped(object? sender, TappedEventArgs e)
    {
        ToggleFullscreen();
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen)
        {
            WindowState = _previousWindowState;
            ControlsPanel.IsVisible = true;
            _isFullscreen = false;
            FullscreenButton.Content = "⛶";
        }
        else
        {
            _previousWindowState = WindowState;
            WindowState = WindowState.FullScreen;
            ControlsPanel.IsVisible = false;
            _isFullscreen = true;
            FullscreenButton.Content = "⛶"; // Exit fullscreen icon
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_mediaPlayer is null) return;
        
        switch (e.Key)
        {
            case Key.Space:
                OnPlayPauseClick(null, null!);
                e.Handled = true;
                break;
                
            case Key.Left:
                _mediaPlayer.Time = Math.Max(0, _mediaPlayer.Time - 10000); // -10s
                e.Handled = true;
                break;
                
            case Key.Right:
                _mediaPlayer.Time = Math.Min(_mediaPlayer.Length, _mediaPlayer.Time + 10000); // +10s
                e.Handled = true;
                break;
                
            case Key.Up:
                _mediaPlayer.Volume = Math.Min(100, _mediaPlayer.Volume + 5);
                VolumeSlider.Value = _mediaPlayer.Volume;
                e.Handled = true;
                break;
                
            case Key.Down:
                _mediaPlayer.Volume = Math.Max(0, _mediaPlayer.Volume - 5);
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
        _progressTimer?.Stop();
        _mediaPlayer?.Stop();
        _media?.Dispose();
        _mediaPlayer?.Dispose();
        _libVLC?.Dispose();
        
        // Set environment exit code for CLI to detect episode navigation
        Environment.ExitCode = _exitCode;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
