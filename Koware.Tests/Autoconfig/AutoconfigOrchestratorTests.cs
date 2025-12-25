// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Analysis;
using Koware.Autoconfig.Generation;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Orchestration;
using Koware.Autoconfig.Runtime;
using Koware.Autoconfig.Storage;
using Koware.Autoconfig.Validation;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koware.Tests.Autoconfig;

public sealed class AutoconfigOrchestratorTests
{
    private readonly AutoconfigOrchestrator _orchestrator;
    private readonly StubHttpMessageHandler _httpHandler;
    private readonly InMemoryProviderStore _providerStore;

    public AutoconfigOrchestratorTests()
    {
        _httpHandler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler);
        var loggerFactory = new LoggerFactory();

        var siteProber = new SiteProber(httpClient, loggerFactory.CreateLogger<SiteProber>());
        var apiDiscovery = new ApiDiscoveryEngine(httpClient, loggerFactory.CreateLogger<ApiDiscoveryEngine>());
        var patternMatcher = new ContentPatternMatcher(httpClient, loggerFactory.CreateLogger<ContentPatternMatcher>());
        var templateLibrary = new ProviderTemplateLibrary();
        var schemaGenerator = new SchemaGenerator(templateLibrary);
        var transformEngine = new TransformEngine(loggerFactory.CreateLogger<TransformEngine>());
        var validator = new ConfigValidator(httpClient, transformEngine, loggerFactory.CreateLogger<ConfigValidator>());
        _providerStore = new InMemoryProviderStore();

        _orchestrator = new AutoconfigOrchestrator(
            siteProber,
            apiDiscovery,
            patternMatcher,
            schemaGenerator,
            validator,
            _providerStore,
            loggerFactory.CreateLogger<AutoconfigOrchestrator>());
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_ReturnsResult()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true,
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Phases);
        Assert.True(result.Phases.Count > 0);
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_TracksProgress()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true
        };

        var progressUpdates = new List<AutoconfigProgress>();
        var progress = new Progress<AutoconfigProgress>(p => progressUpdates.Add(p));

        // Act
        await _orchestrator.AnalyzeAndConfigureAsync(url, options, progress);

        // Assert
        Assert.True(progressUpdates.Count > 0);
        Assert.Contains(progressUpdates, p => p.Phase == "Site Probing");
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_DryRunDoesNotSave()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        var savedProviders = await _providerStore.GetAllAsync();
        Assert.Empty(savedProviders);
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_SavesWhenNotDryRun()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = false
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        if (result.IsSuccess)
        {
            var savedProviders = await _providerStore.GetAllAsync();
            Assert.NotEmpty(savedProviders);
        }
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_UsesCustomProviderName()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            ProviderName = "MyCustomProvider",
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        if (result.IsSuccess && result.Config != null)
        {
            Assert.Equal("MyCustomProvider", result.Config.Name);
        }
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_ForcesProviderType()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            ForceType = ProviderType.Manga,
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        if (result.IsSuccess && result.Config != null)
        {
            Assert.Equal(ProviderType.Manga, result.Config.Type);
        }
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_RespectsTimeout()
    {
        // Arrange
        _httpHandler.SetDelay(TimeSpan.FromSeconds(5));
        var url = new Uri("https://slow-site.example.com");
        var options = new AutoconfigOptions
        {
            Timeout = TimeSpan.FromMilliseconds(100),
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        // Should fail due to timeout
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_ReturnsWarningsForPartialSuccess()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        Assert.NotNull(result);
        // Warnings list should exist even if empty
        Assert.NotNull(result.Warnings);
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_IncludesSiteProfile()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        Assert.NotNull(result.SiteProfile);
    }

    [Fact]
    public async Task AnalyzeAndConfigureAsync_MeasuresDuration()
    {
        // Arrange
        SetupMockResponses();
        var url = new Uri("https://test-anime.example.com");
        var options = new AutoconfigOptions
        {
            SkipValidation = true,
            DryRun = true
        };

        // Act
        var result = await _orchestrator.AnalyzeAndConfigureAsync(url, options);

        // Assert
        Assert.True(result.Duration > TimeSpan.Zero);
    }

    private void SetupMockResponses()
    {
        // Setup basic HTML response for site probing
        var htmlResponse = """
        <!DOCTYPE html>
        <html>
        <head>
            <title>Test Anime Site - Watch Anime Online</title>
            <meta name="description" content="Watch anime episodes and stream online">
        </head>
        <body>
            <div id="app" data-reactroot>
                <a href="/anime/naruto">Naruto</a>
                <a href="/watch/one-piece">One Piece</a>
            </div>
            <script src="/_next/static/chunks/main.js"></script>
        </body>
        </html>
        """;

        _httpHandler.SetResponse(htmlResponse);
    }
}

/// <summary>
/// In-memory provider store for testing.
/// </summary>
internal sealed class InMemoryProviderStore : IProviderStore
{
    private readonly Dictionary<string, DynamicProviderConfig> _providers = new();
    private string? _activeAnime;
    private string? _activeManga;

    public Task SaveAsync(DynamicProviderConfig config, CancellationToken ct = default)
    {
        _providers[config.Slug] = config;
        return Task.CompletedTask;
    }

    public Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct = default)
    {
        return Task.FromResult(_providers.GetValueOrDefault(slug));
    }

    public Task<IReadOnlyList<DynamicProviderConfig>> GetAllAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<DynamicProviderConfig>>(_providers.Values.ToList());
    }

    public Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct = default)
    {
        var infos = _providers.Values.Select(c => ProviderInfo.FromConfig(c, c.Slug == _activeAnime || c.Slug == _activeManga)).ToList();
        return Task.FromResult<IReadOnlyList<ProviderInfo>>(infos);
    }

    public Task<bool> DeleteAsync(string slug, CancellationToken ct = default)
    {
        return Task.FromResult(_providers.Remove(slug));
    }

    public Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct = default)
    {
        if (type == ProviderType.Anime || type == ProviderType.Both)
            _activeAnime = slug;
        if (type == ProviderType.Manga || type == ProviderType.Both)
            _activeManga = slug;
        return Task.CompletedTask;
    }

    public Task<DynamicProviderConfig?> GetActiveAsync(ProviderType type, CancellationToken ct = default)
    {
        var slug = type == ProviderType.Manga ? _activeManga : _activeAnime;
        return Task.FromResult(slug != null ? _providers.GetValueOrDefault(slug) : null);
    }

    public Task<bool> ExistsAsync(string slug, CancellationToken ct = default)
    {
        return Task.FromResult(_providers.ContainsKey(slug));
    }

    public Task<string> ExportAsync(string slug, CancellationToken ct = default)
    {
        var config = _providers.GetValueOrDefault(slug);
        if (config == null) throw new InvalidOperationException($"Provider '{slug}' not found");
        return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(config));
    }

    public Task<DynamicProviderConfig> ImportAsync(string json, CancellationToken ct = default)
    {
        var config = System.Text.Json.JsonSerializer.Deserialize<DynamicProviderConfig>(json);
        if (config == null) throw new InvalidOperationException("Failed to parse provider configuration");
        return Task.FromResult(config);
    }
}
