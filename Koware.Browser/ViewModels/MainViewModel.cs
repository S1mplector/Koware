// Author: Ilgaz Mehmetoğlu
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Koware.Browser.Services;
using Koware.Domain.Models;

namespace Koware.Browser.ViewModels;

public enum BrowseMode
{
    Anime,
    Manga
}

public enum ViewState
{
    Search,
    Detail,
    List,
    Settings
}

/// <summary>
/// Main view model for the browser application.
/// Handles mode switching, search, and navigation state.
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly CatalogService _catalogService;
    private CancellationTokenSource? _searchCts;

    [ObservableProperty]
    private BrowseMode _currentMode = BrowseMode.Manga;

    [ObservableProperty]
    private ViewState _currentView = ViewState.Search;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private bool _isSearching;

    [ObservableProperty]
    private bool _isLoadingDetail;

    [ObservableProperty]
    private string? _statusMessage;

    // Search results
    [ObservableProperty]
    private ObservableCollection<AnimeViewModel> _animeResults = new();

    [ObservableProperty]
    private ObservableCollection<MangaViewModel> _mangaResults = new();

    // Selected item for detail view
    [ObservableProperty]
    private AnimeViewModel? _selectedAnime;

    [ObservableProperty]
    private MangaViewModel? _selectedManga;

    // Episodes/Chapters for detail view
    [ObservableProperty]
    private ObservableCollection<EpisodeViewModel> _episodes = new();

    [ObservableProperty]
    private ObservableCollection<ChapterViewModel> _chapters = new();

    public MainViewModel(CatalogService catalogService)
    {
        _catalogService = catalogService;
    }

    public bool IsMangaMode => CurrentMode == BrowseMode.Manga;
    public bool IsAnimeMode => CurrentMode == BrowseMode.Anime;
    public bool IsSearchView => CurrentView == ViewState.Search;
    public bool IsDetailView => CurrentView == ViewState.Detail;
    public bool IsListView => CurrentView == ViewState.List;
    public bool IsSettingsView => CurrentView == ViewState.Settings;

    partial void OnCurrentModeChanged(BrowseMode value)
    {
        OnPropertyChanged(nameof(IsMangaMode));
        OnPropertyChanged(nameof(IsAnimeMode));
        
        // Clear results when switching modes
        AnimeResults.Clear();
        MangaResults.Clear();
        SearchQuery = string.Empty;
        CurrentView = ViewState.Search;
    }

    partial void OnCurrentViewChanged(ViewState value)
    {
        OnPropertyChanged(nameof(IsSearchView));
        OnPropertyChanged(nameof(IsDetailView));
        OnPropertyChanged(nameof(IsListView));
        OnPropertyChanged(nameof(IsSettingsView));
    }

    [RelayCommand]
    private void SetMangaMode()
    {
        CurrentMode = BrowseMode.Manga;
    }

    [RelayCommand]
    private void SetAnimeMode()
    {
        CurrentMode = BrowseMode.Anime;
    }

    [RelayCommand]
    private void ShowList()
    {
        CurrentView = ViewState.List;
    }

    [RelayCommand]
    private void ShowSettings()
    {
        CurrentView = ViewState.Settings;
    }

    [RelayCommand]
    private void ShowSearch()
    {
        CurrentView = ViewState.Search;
        SelectedAnime = null;
        SelectedManga = null;
    }

    [RelayCommand]
    private void GoBack()
    {
        if (CurrentView == ViewState.Detail)
        {
            CurrentView = ViewState.Search;
            SelectedAnime = null;
            SelectedManga = null;
        }
        else if (CurrentView == ViewState.List || CurrentView == ViewState.Settings)
        {
            CurrentView = ViewState.Search;
        }
    }

    [RelayCommand]
    private async Task SearchAsync()
    {
        if (string.IsNullOrWhiteSpace(SearchQuery))
        {
            return;
        }

        // Cancel any ongoing search
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        IsSearching = true;
        StatusMessage = $"Searching for '{SearchQuery}'...";

        try
        {
            if (CurrentMode == BrowseMode.Manga)
            {
                var results = await _catalogService.SearchMangaAsync(SearchQuery, ct);
                if (ct.IsCancellationRequested) return;

                MangaResults.Clear();
                foreach (var manga in results)
                {
                    MangaResults.Add(new MangaViewModel(manga));
                }
                StatusMessage = $"Found {results.Count} manga";
            }
            else
            {
                var results = await _catalogService.SearchAnimeAsync(SearchQuery, ct);
                if (ct.IsCancellationRequested) return;

                AnimeResults.Clear();
                foreach (var anime in results)
                {
                    AnimeResults.Add(new AnimeViewModel(anime));
                }
                StatusMessage = $"Found {results.Count} anime";
            }

            CurrentView = ViewState.Search;
        }
        catch (OperationCanceledException)
        {
            // Search was cancelled, ignore
        }
        catch (Exception ex)
        {
            StatusMessage = $"Search failed: {ex.Message}";
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private async Task SelectAnimeAsync(AnimeViewModel anime)
    {
        SelectedAnime = anime;
        SelectedManga = null;
        CurrentView = ViewState.Detail;
        IsLoadingDetail = true;
        Episodes.Clear();

        try
        {
            var episodes = await _catalogService.GetEpisodesAsync(anime.Model);
            foreach (var ep in episodes)
            {
                Episodes.Add(new EpisodeViewModel(ep));
            }
            StatusMessage = $"Loaded {episodes.Count} episodes";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load episodes: {ex.Message}";
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    [RelayCommand]
    private async Task SelectMangaAsync(MangaViewModel manga)
    {
        SelectedManga = manga;
        SelectedAnime = null;
        CurrentView = ViewState.Detail;
        IsLoadingDetail = true;
        Chapters.Clear();

        try
        {
            var chapters = await _catalogService.GetChaptersAsync(manga.Model);
            foreach (var ch in chapters)
            {
                Chapters.Add(new ChapterViewModel(ch));
            }
            StatusMessage = $"Loaded {chapters.Count} chapters";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to load chapters: {ex.Message}";
        }
        finally
        {
            IsLoadingDetail = false;
        }
    }

    [RelayCommand]
    private async Task PlayEpisodeAsync(EpisodeViewModel episode)
    {
        StatusMessage = $"Loading streams for {episode.Title}...";
        
        try
        {
            var streams = await _catalogService.GetStreamsAsync(episode.Model);
            if (streams.Count == 0)
            {
                StatusMessage = "No streams found for this episode";
                return;
            }

            // Get the best stream (first one, usually highest quality)
            var stream = streams.First();
            StatusMessage = $"Opening player for {episode.Title}...";

            // Launch the player (similar to CLI behavior)
            await LaunchPlayerAsync(stream);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to play episode: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ReadChapterAsync(ChapterViewModel chapter)
    {
        StatusMessage = $"Loading pages for {chapter.Title}...";

        try
        {
            var pages = await _catalogService.GetPagesAsync(chapter.Model);
            if (pages.Count == 0)
            {
                StatusMessage = "No pages found for this chapter";
                return;
            }

            StatusMessage = $"Opening reader for {chapter.Title}...";

            // Launch the reader
            await LaunchReaderAsync(pages, chapter.Title);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to read chapter: {ex.Message}";
        }
    }

    private async Task LaunchPlayerAsync(StreamLink stream)
    {
        // Try to find and launch Koware.Player
        var playerPath = FindExecutable("Koware.Player");
        if (playerPath == null)
        {
            StatusMessage = "Player not found. Opening stream in browser...";
            OpenInBrowser(stream.Url.ToString());
            return;
        }

        var args = $"\"{stream.Url}\"";
        if (!string.IsNullOrEmpty(stream.Referrer))
        {
            args += $" --referrer \"{stream.Referrer}\"";
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = playerPath,
                Arguments = args,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
            StatusMessage = "Player launched";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to launch player: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    private async Task LaunchReaderAsync(IReadOnlyCollection<ChapterPage> pages, string title)
    {
        // Try to find and launch Koware.Reader
        var readerPath = FindExecutable("Koware.Reader");
        if (readerPath == null)
        {
            StatusMessage = "Reader not found. Opening first page in browser...";
            if (pages.Count > 0)
            {
                OpenInBrowser(pages.First().ImageUrl.ToString());
            }
            return;
        }

        // Build JSON array of pages
        var pagesJson = System.Text.Json.JsonSerializer.Serialize(
            pages.Select(p => new { url = p.ImageUrl.ToString(), pageNumber = p.PageNumber }));
        
        // Get referer from first page (all pages typically use same referer)
        var referer = pages.FirstOrDefault()?.Referrer;
        
        var args = $"'{pagesJson}' \"{title}\"";
        if (!string.IsNullOrEmpty(referer))
        {
            args += $" --referer \"{referer}\"";
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = readerPath,
                Arguments = args,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);
            StatusMessage = "Reader launched";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to launch reader: {ex.Message}";
        }

        await Task.CompletedTask;
    }

    private static string? FindExecutable(string name)
    {
        var basePath = AppContext.BaseDirectory;
        var candidates = new List<string>();
        
        // Get short name for folder (e.g., "player" from "Koware.Player")
        var shortName = name.Replace("Koware.", "").ToLowerInvariant();
        
        if (OperatingSystem.IsMacOS())
        {
            // macOS app bundle structure:
            // /Applications/Koware.app/Contents/MacOS/Koware.Browser (basePath = MacOS/)
            // /Applications/Koware.app/Contents/Resources/reader/Koware.Reader
            // /Applications/Koware.app/Contents/Resources/player/Koware.Player
            
            // Go up from MacOS/ to Contents/
            var contentsDir = Path.GetFullPath(Path.Combine(basePath, ".."));
            var resourcesDir = Path.Combine(contentsDir, "Resources");
            
            // Primary: bundled in app Resources
            candidates.Add(Path.Combine(resourcesDir, shortName, name));
            // Fallback: sibling folder (for dev builds)
            candidates.Add(Path.Combine(basePath, "..", shortName, name));
            // Fallback: same directory
            candidates.Add(Path.Combine(basePath, name));
            // Fallback: CLI install location
            candidates.Add($"/usr/local/bin/koware/{shortName}/{name}");
        }
        else if (OperatingSystem.IsWindows())
        {
            // Windows: Check relative to app, then AppData install location
            candidates.Add(Path.Combine(basePath, name + ".exe"));
            candidates.Add(Path.Combine(basePath, "..", shortName, name + ".exe"));
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(appData, "Koware", shortName, name + ".exe"));
        }
        else
        {
            // Linux: similar to macOS
            candidates.Add(Path.Combine(basePath, name));
            candidates.Add(Path.Combine(basePath, "..", shortName, name));
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            candidates.Add(Path.Combine(home, ".local", "bin", "koware", shortName, name));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch
        {
            // Ignore browser launch errors
        }
    }
}

public partial class AnimeViewModel : ObservableObject
{
    public Anime Model { get; }

    public string Title => Model.Title;
    public string? Synopsis => Model.Synopsis;
    public Uri? CoverImage => Model.CoverImage;
    public Uri DetailPage => Model.DetailPage;

    public AnimeViewModel(Anime model)
    {
        Model = model;
    }
}

public partial class MangaViewModel : ObservableObject
{
    public Manga Model { get; }

    public string Title => Model.Title;
    public string? Synopsis => Model.Synopsis;
    public Uri? CoverImage => Model.CoverImage;
    public Uri DetailPage => Model.DetailPage;

    public MangaViewModel(Manga model)
    {
        Model = model;
    }
}

public partial class EpisodeViewModel : ObservableObject
{
    public Episode Model { get; }

    public string Title => Model.Title;
    public int Number => Model.Number;

    public EpisodeViewModel(Episode model)
    {
        Model = model;
    }
}

public partial class ChapterViewModel : ObservableObject
{
    public Chapter Model { get; }

    public string Title => Model.Title;
    public float Number => Model.Number;

    public ChapterViewModel(Chapter model)
    {
        Model = model;
    }
}
