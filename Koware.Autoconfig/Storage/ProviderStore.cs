// Author: Ilgaz Mehmetoğlu
using System.Text.Json;
using System.Text.Json.Serialization;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Storage;

/// <summary>
/// File-based provider configuration storage.
/// Stores providers in ~/.config/koware/providers/ as JSON files.
/// </summary>
public sealed class ProviderStore : IProviderStore
{
    private readonly string _providersDirectory;
    private readonly string _activeConfigPath;
    private readonly ILogger<ProviderStore> _logger;
    private readonly JsonSerializerOptions _jsonOptions;
    
    private ActiveProviders? _activeProviders;

    public ProviderStore(ILogger<ProviderStore> logger)
    {
        _logger = logger;
        
        var configBase = GetConfigDirectory();
        _providersDirectory = Path.Combine(configBase, "providers");
        _activeConfigPath = Path.Combine(configBase, "active-providers.json");
        
        _jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };
        
        EnsureDirectoriesExist();
    }

    public async Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct = default)
    {
        var providers = new List<ProviderInfo>();
        var active = await LoadActiveProvidersAsync(ct);
        
        // Load built-in providers
        var builtInDir = Path.Combine(_providersDirectory, "builtin");
        if (Directory.Exists(builtInDir))
        {
            foreach (var file in Directory.GetFiles(builtInDir, "*.json"))
            {
                var config = await LoadConfigAsync(file, ct);
                if (config != null)
                {
                    var isActive = IsActive(config.Slug, config.Type, active);
                    providers.Add(ProviderInfo.FromConfig(config with { IsBuiltIn = true }, isActive));
                }
            }
        }
        
        // Load custom providers
        var customDir = Path.Combine(_providersDirectory, "custom");
        if (Directory.Exists(customDir))
        {
            foreach (var file in Directory.GetFiles(customDir, "*.json"))
            {
                var config = await LoadConfigAsync(file, ct);
                if (config != null)
                {
                    var isActive = IsActive(config.Slug, config.Type, active);
                    providers.Add(ProviderInfo.FromConfig(config, isActive));
                }
            }
        }
        
        return providers.OrderBy(p => p.Name).ToList();
    }

    public async Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = NormalizeSlug(slug);
        
        // Check custom first, then built-in
        var customPath = GetCustomProviderPath(normalizedSlug);
        if (File.Exists(customPath))
        {
            return await LoadConfigAsync(customPath, ct);
        }
        
        var builtInPath = GetBuiltInProviderPath(normalizedSlug);
        if (File.Exists(builtInPath))
        {
            var config = await LoadConfigAsync(builtInPath, ct);
            return config != null ? config with { IsBuiltIn = true } : null;
        }
        
        return null;
    }

    public async Task SaveAsync(DynamicProviderConfig config, CancellationToken ct = default)
    {
        var normalizedSlug = NormalizeSlug(config.Slug);
        var path = GetCustomProviderPath(normalizedSlug);
        
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        
        var json = JsonSerializer.Serialize(config, _jsonOptions);
        await File.WriteAllTextAsync(path, json, ct);
        
        _logger.LogInformation("Saved provider '{Slug}' to {Path}", normalizedSlug, path);
    }

    public Task<bool> DeleteAsync(string slug, CancellationToken ct = default)
    {
        var normalizedSlug = NormalizeSlug(slug);
        var customPath = GetCustomProviderPath(normalizedSlug);
        
        if (File.Exists(customPath))
        {
            File.Delete(customPath);
            _logger.LogInformation("Deleted provider '{Slug}'", normalizedSlug);
            return Task.FromResult(true);
        }
        
        // Cannot delete built-in providers
        var builtInPath = GetBuiltInProviderPath(normalizedSlug);
        if (File.Exists(builtInPath))
        {
            _logger.LogWarning("Cannot delete built-in provider '{Slug}'", normalizedSlug);
            return Task.FromResult(false);
        }
        
        return Task.FromResult(false);
    }

    public async Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct = default)
    {
        var active = await LoadActiveProvidersAsync(ct);
        
        switch (type)
        {
            case ProviderType.Anime:
                active.AnimeProvider = slug;
                break;
            case ProviderType.Manga:
                active.MangaProvider = slug;
                break;
            case ProviderType.Both:
                active.AnimeProvider = slug;
                active.MangaProvider = slug;
                break;
        }
        
        await SaveActiveProvidersAsync(active, ct);
        _logger.LogInformation("Set '{Slug}' as active {Type} provider", slug, type);
    }

    public async Task<DynamicProviderConfig?> GetActiveAsync(ProviderType type, CancellationToken ct = default)
    {
        var active = await LoadActiveProvidersAsync(ct);
        
        var slug = type switch
        {
            ProviderType.Anime => active.AnimeProvider,
            ProviderType.Manga => active.MangaProvider,
            _ => active.AnimeProvider
        };
        
        if (string.IsNullOrEmpty(slug))
            return null;
            
        return await GetAsync(slug, ct);
    }

    public async Task<bool> ExistsAsync(string slug, CancellationToken ct = default)
    {
        var config = await GetAsync(slug, ct);
        return config != null;
    }

    public async Task<string> ExportAsync(string slug, CancellationToken ct = default)
    {
        var config = await GetAsync(slug, ct);
        if (config == null)
            throw new InvalidOperationException($"Provider '{slug}' not found");
            
        return JsonSerializer.Serialize(config, _jsonOptions);
    }

    public Task<DynamicProviderConfig> ImportAsync(string json, CancellationToken ct = default)
    {
        var config = JsonSerializer.Deserialize<DynamicProviderConfig>(json, _jsonOptions);
        if (config == null)
            throw new InvalidOperationException("Failed to parse provider configuration");
            
        return Task.FromResult(config);
    }

    private async Task<DynamicProviderConfig?> LoadConfigAsync(string path, CancellationToken ct)
    {
        try
        {
            var json = await File.ReadAllTextAsync(path, ct);
            return JsonSerializer.Deserialize<DynamicProviderConfig>(json, _jsonOptions);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load provider config from {Path}", path);
            return null;
        }
    }

    private async Task<ActiveProviders> LoadActiveProvidersAsync(CancellationToken ct)
    {
        if (_activeProviders != null)
            return _activeProviders;
            
        if (File.Exists(_activeConfigPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(_activeConfigPath, ct);
                _activeProviders = JsonSerializer.Deserialize<ActiveProviders>(json, _jsonOptions) 
                    ?? new ActiveProviders();
            }
            catch
            {
                _activeProviders = new ActiveProviders();
            }
        }
        else
        {
            _activeProviders = new ActiveProviders();
        }
        
        return _activeProviders;
    }

    private async Task SaveActiveProvidersAsync(ActiveProviders active, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(active, _jsonOptions);
        await File.WriteAllTextAsync(_activeConfigPath, json, ct);
        _activeProviders = active;
    }

    private bool IsActive(string slug, ProviderType type, ActiveProviders active)
    {
        return type switch
        {
            ProviderType.Anime => active.AnimeProvider == slug,
            ProviderType.Manga => active.MangaProvider == slug,
            ProviderType.Both => active.AnimeProvider == slug || active.MangaProvider == slug,
            _ => false
        };
    }

    private string GetCustomProviderPath(string slug) =>
        Path.Combine(_providersDirectory, "custom", $"{slug}.json");

    private string GetBuiltInProviderPath(string slug) =>
        Path.Combine(_providersDirectory, "builtin", $"{slug}.json");

    private static string NormalizeSlug(string slug) =>
        slug.ToLowerInvariant().Replace(' ', '-');

    private static string GetConfigDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "koware");
        }
        
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "koware");
    }

    private void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(_providersDirectory);
        Directory.CreateDirectory(Path.Combine(_providersDirectory, "builtin"));
        Directory.CreateDirectory(Path.Combine(_providersDirectory, "custom"));
    }

    private sealed class ActiveProviders
    {
        public string? AnimeProvider { get; set; }
        public string? MangaProvider { get; set; }
    }
}
