// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Analysis;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Koware.Tests.Autoconfig;

public sealed class IntelligentPatternEngineTests
{
    private readonly IntelligentPatternEngine _engine;
    private readonly StubHttpMessageHandler _httpHandler;

    public IntelligentPatternEngineTests()
    {
        _httpHandler = new StubHttpMessageHandler();
        var httpClient = new HttpClient(_httpHandler);
        var logger = new LoggerFactory().CreateLogger<IntelligentPatternEngine>();
        _engine = new IntelligentPatternEngine(httpClient, logger);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsGraphQLSite()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: true,
            endpoints: ["/graphql", "/api/graphql"],
            title: "TestAnime - Watch Anime Online"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        Assert.NotNull(result.Fingerprint);
        Assert.Contains("GraphQL", result.Fingerprint.Technologies);
        Assert.True(result.OverallConfidence > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsAnimeSite()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: false,
            endpoints: ["/api/anime", "/api/episodes", "/watch"],
            title: "AnimeStream - Watch Episodes Online",
            description: "Watch anime episodes free"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        var contentPattern = result.Patterns.FirstOrDefault(p => p.Type == "ContentType");
        // Patterns should be detected
        Assert.True(result.Patterns.Count > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsMangaSite()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: false,
            endpoints: ["/api/manga", "/api/chapters", "/read"],
            title: "MangaRead - Read Manga Online",
            description: "Read manga chapters free"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Patterns.Count > 0);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsSearchEndpoint()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: false,
            endpoints: ["/api/search?q=test", "/search"],
            title: "Test Site"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        var searchPattern = result.Patterns.FirstOrDefault(p => p.Type == "SearchEndpoint");
        Assert.NotNull(searchPattern);
        Assert.True(searchPattern.Confidence > 0.5f);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsCloudflareProtection()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: false,
            endpoints: [],
            title: "Protected Site",
            hasCloudflare: true
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("Cloudflare", result.Fingerprint.Technologies);
        Assert.Contains(result.Recommendations, r => r.Contains("Cloudflare"));
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsSPASite()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: false,
            endpoints: [],
            title: "SPA Site",
            requiresJs: true,
            jsFramework: "React"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        Assert.Contains("React", result.Fingerprint.Technologies);
        Assert.Contains("SPA", result.Fingerprint.Technologies);
    }

    [Fact]
    public async Task AnalyzeAsync_GeneratesRecommendations()
    {
        // Arrange
        var profile = CreateTestProfile(
            hasGraphQL: true,
            endpoints: ["/graphql"],
            title: "GraphQL Site",
            hasCloudflare: true,
            requiresJs: true
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Recommendations.Count > 0);
    }

    [Fact]
    public void GetPatternConfidence_ReturnsHighConfidenceForKnownPatterns()
    {
        // Act & Assert
        Assert.True(_engine.GetPatternConfidence("GraphQLEndpoint", "/graphql") > 0.8f);
        Assert.True(_engine.GetPatternConfidence("SearchEndpoint", "/api/search") > 0.8f);
        Assert.True(_engine.GetPatternConfidence("EpisodeEndpoint", "/episodes") > 0.7f);
    }

    [Fact]
    public void GetPatternConfidence_ReturnsLowConfidenceForUnknownPatterns()
    {
        // Act & Assert
        Assert.True(_engine.GetPatternConfidence("GraphQLEndpoint", "/random/path") < 0.5f);
        Assert.True(_engine.GetPatternConfidence("SearchEndpoint", "/unknown") < 0.5f);
    }

    [Fact]
    public async Task AnalyzeAsync_CreatesUniqueFingerprint()
    {
        // Arrange
        var profile1 = CreateTestProfile(
            hasGraphQL: true,
            endpoints: ["/graphql"],
            title: "Site 1"
        );
        
        var profile2 = CreateTestProfile(
            hasGraphQL: false,
            endpoints: ["/api"],
            title: "Site 2"
        );

        // Act
        var result1 = await _engine.AnalyzeAsync(profile1);
        var result2 = await _engine.AnalyzeAsync(profile2);

        // Assert
        Assert.NotNull(result1.Fingerprint.Hash);
        Assert.NotNull(result2.Fingerprint.Hash);
        Assert.NotEqual(result1.Fingerprint.Hash, result2.Fingerprint.Hash);
    }

    [Fact]
    public async Task AnalyzeAsync_DetectsEncodingPatterns()
    {
        // Arrange - AllAnime-style encoded URL
        var profile = CreateTestProfile(
            hasGraphQL: true,
            endpoints: ["/graphql", "-01a2b3c4d5e6f7890abcdef12345"],
            title: "Encoded Site"
        );

        // Act
        var result = await _engine.AnalyzeAsync(profile);

        // Assert
        Assert.NotNull(result);
        var encodingPattern = result.Patterns.FirstOrDefault(p => p.Type == "Encoding");
        Assert.NotNull(encodingPattern);
    }

    private static SiteProfile CreateTestProfile(
        bool hasGraphQL,
        string[] endpoints,
        string title,
        string? description = null,
        bool hasCloudflare = false,
        bool requiresJs = false,
        string? jsFramework = null)
    {
        return new SiteProfile
        {
            BaseUrl = new Uri("https://test.example.com"),
            Type = requiresJs ? SiteType.SPA : SiteType.Static,
            Category = ContentCategory.Unknown,
            RequiresJavaScript = requiresJs,
            HasCloudflareProtection = hasCloudflare,
            HasGraphQL = hasGraphQL,
            ServerSoftware = null,
            JsFramework = jsFramework,
            DetectedApiEndpoints = endpoints.ToList(),
            DetectedCdnHosts = [],
            RequiredHeaders = new Dictionary<string, string>(),
            SiteTitle = title,
            SiteDescription = description,
            RobotsTxt = null,
            Errors = []
        };
    }
}
