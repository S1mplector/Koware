# Provider Autoconfig Architecture

> **Status**: **IMPLEMENTED** in `Koware.Autoconfig` project
>
> **Command**: `koware provider autoconfig <website-url>`

This document describes the architecture for an intelligent provider auto-configuration system that can analyze any anime/manga website and automatically generate scraping configurations.

---

## Overview

The autoconfig system performs **multi-phase intelligent analysis** of a target website to:

1. **Detect content type** (anime streaming, manga reading, hybrid)
2. **Identify site structure** (API-based, HTML scraping, hybrid)
3. **Discover data patterns** (search endpoints, content listings, media URLs)
4. **Generate a working provider configuration** automatically
5. **Validate and test** the generated configuration

---

## Architecture Layers

```
┌─────────────────────────────────────────────────────────────────────┐
│                         CLI Command Layer                            │
│                    koware provider autoconfig <url>                  │
└─────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│                    AutoconfigOrchestrator                            │
│  Coordinates the multi-phase analysis pipeline                       │
└─────────────────────────────────────────────────────────────────────┘
                                  │
        ┌─────────────────────────┼─────────────────────────┐
        ▼                         ▼                         ▼
┌───────────────┐       ┌───────────────┐       ┌───────────────┐
│  Site Prober  │       │ Content       │       │ Schema        │
│  (Phase 1)    │       │ Analyzer      │       │ Generator     │
│               │       │ (Phase 2)     │       │ (Phase 3)     │
└───────────────┘       └───────────────┘       └───────────────┘
        │                         │                         │
        ▼                         ▼                         ▼
┌───────────────┐       ┌───────────────┐       ┌───────────────┐
│ Network       │       │ Pattern       │       │ Provider      │
│ Fingerprint   │       │ Matcher       │       │ Template      │
│ Engine        │       │ Engine        │       │ Compiler      │
└───────────────┘       └───────────────┘       └───────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│                       ConfigValidator                                │
│  Tests generated config with live requests                           │
└─────────────────────────────────────────────────────────────────────┘
                                  │
                                  ▼
┌─────────────────────────────────────────────────────────────────────┐
│                       ProviderStore                                  │
│  Persists validated provider to appsettings.user.json                │
└─────────────────────────────────────────────────────────────────────┘
```

---

## Phase 1: Site Probing (`ISiteProber`)

### Purpose
Gather initial intelligence about the target website without deep crawling.

### Components

#### `NetworkFingerprinter`
- **DNS resolution** - Resolve hostname, check for CDN (Cloudflare, Akamai)
- **TLS fingerprint** - Detect anti-bot measures (JA3 fingerprinting)
- **Response headers** - Identify server stack, caching, CORS policies
- **robots.txt analysis** - Find API paths, rate limit hints

#### `PageStructureScanner`
- **Initial page load** - Capture HTML, scripts, styles
- **JavaScript detection** - SPA framework identification (React, Vue, Next.js)
- **Meta tag extraction** - Open Graph, schema.org, content hints
- **Resource enumeration** - JS bundles, API endpoints in code

### Output: `SiteProfile`
```csharp
public record SiteProfile
{
    public Uri BaseUrl { get; init; }
    public SiteType Type { get; init; }              // Static, SPA, Hybrid
    public ContentCategory Category { get; init; }    // Anime, Manga, Both, Unknown
    public bool RequiresJavaScript { get; init; }
    public bool HasCloudflareProtection { get; init; }
    public IReadOnlyList<string> DetectedApiEndpoints { get; init; }
    public IReadOnlyList<string> DetectedCdnHosts { get; init; }
    public IDictionary<string, string> RequiredHeaders { get; init; }
}
```

---

## Phase 2: Content Analysis (`IContentAnalyzer`)

### Purpose
Deep analysis of site structure, content patterns, and data extraction methods.

### Components

#### `ApiDiscoveryEngine`
Uses multiple strategies to find API endpoints:

1. **Network interception simulation** - Parse JS bundles for fetch/axios calls
2. **GraphQL introspection** - Attempt schema discovery if GraphQL detected
3. **REST pattern matching** - Look for `/api/`, `/v1/`, common REST patterns
4. **XHR pattern extraction** - Parse JavaScript for API URL construction

```csharp
public interface IApiDiscoveryEngine
{
    Task<IReadOnlyList<ApiEndpoint>> DiscoverAsync(SiteProfile profile, CancellationToken ct);
}

public record ApiEndpoint
{
    public Uri Url { get; init; }
    public ApiType Type { get; init; }       // GraphQL, REST, Custom
    public HttpMethod Method { get; init; }
    public string? SampleQuery { get; init; }
    public EndpointPurpose Purpose { get; init; }  // Search, Episodes, Streams, Chapters, Pages
}
```

#### `ContentPatternMatcher`
Identifies content structure patterns:

```csharp
public interface IContentPatternMatcher
{
    Task<ContentSchema> AnalyzeAsync(SiteProfile profile, IReadOnlyList<ApiEndpoint> endpoints, CancellationToken ct);
}

public record ContentSchema
{
    // How to search for content
    public SearchPattern SearchPattern { get; init; }
    
    // How content is identified (ID format, URL patterns)
    public ContentIdentifierPattern IdPattern { get; init; }
    
    // For anime: how episodes are listed and accessed
    public EpisodePattern? EpisodePattern { get; init; }
    
    // For manga: how chapters/pages are structured
    public ChapterPattern? ChapterPattern { get; init; }
    
    // Media delivery (CDN patterns, stream formats, image URLs)
    public MediaPattern MediaPattern { get; init; }
}
```

#### `SampleDataCollector`
Performs controlled crawling to validate discovered patterns:

- Execute a test search with a known popular title
- Extract sample content IDs
- Test episode/chapter listing
- Validate media URL resolution

---

## Phase 3: Provider Schema Generation (`ISchemaGenerator`)

### Purpose
Transform discovered patterns into a portable provider configuration.

### Dynamic Provider Definition

```csharp
public record DynamicProviderConfig
{
    public string Name { get; init; }                    // User-friendly name
    public string Slug { get; init; }                    // koware internal ID
    public ProviderType Type { get; init; }              // Anime, Manga, Both
    
    public HostConfig Hosts { get; init; }
    public AuthConfig? Auth { get; init; }
    public SearchConfig Search { get; init; }
    public ContentConfig Content { get; init; }
    public MediaConfig Media { get; init; }
    
    public IReadOnlyList<TransformRule> Transforms { get; init; }
    public ValidationResult? LastValidation { get; init; }
}

public record HostConfig
{
    public string BaseHost { get; init; }
    public string? ApiBase { get; init; }
    public string Referer { get; init; }
    public string UserAgent { get; init; }
    public IDictionary<string, string> CustomHeaders { get; init; }
}

public record SearchConfig
{
    public SearchMethod Method { get; init; }  // GraphQL, REST, HTMLScrape
    public string Endpoint { get; init; }
    public string QueryTemplate { get; init; }
    public IReadOnlyList<FieldMapping> ResultMapping { get; init; }
}

public record FieldMapping
{
    public string SourcePath { get; init; }    // JSONPath or CSS selector
    public string TargetField { get; init; }   // e.g., "Title", "Id", "CoverImage"
    public TransformType? Transform { get; init; }
}
```

### Provider Template Library

Pre-built templates for common site patterns:

| Template | Detection Signals | Examples |
|----------|-------------------|----------|
| `GraphQL-AllAnime` | GraphQL endpoint, `shows`/`episodes` queries | AllAnime clones |
| `REST-Consumet` | `/api/anime`, `/info`, `/watch` structure | Consumet-based APIs |
| `HTML-Classic` | Server-rendered, jQuery, pagination | Older sites |
| `NextJS-Hybrid` | `_next/data`, JSON page props | Modern SPA sites |
| `Cloudflare-Protected` | Challenge pages, turnstile | Protected sites |

```csharp
public interface IProviderTemplateLibrary
{
    IProviderTemplate? MatchTemplate(SiteProfile profile, ContentSchema schema);
    DynamicProviderConfig ApplyTemplate(IProviderTemplate template, ContentSchema schema);
}
```

---

## Phase 4: Validation (`IConfigValidator`)

### Purpose
Test the generated configuration with live requests before saving.

### Validation Steps

```csharp
public interface IConfigValidator
{
    Task<ValidationResult> ValidateAsync(DynamicProviderConfig config, CancellationToken ct);
}

public record ValidationResult
{
    public bool IsValid { get; init; }
    public IReadOnlyList<ValidationCheck> Checks { get; init; }
    public DynamicProviderConfig? SuggestedFixes { get; init; }
}

public record ValidationCheck
{
    public string Name { get; init; }           // "Search", "Episodes", "Streams"
    public bool Passed { get; init; }
    public string? ErrorMessage { get; init; }
    public string? SampleData { get; init; }    // Show what was returned
}
```

### Test Sequence

1. **Search test** - Search for "One Piece" or "Naruto" (high probability of results)
2. **Content resolution** - Fetch episodes/chapters for first result
3. **Media URL test** - Resolve at least one stream/page URL
4. **Header validation** - Ensure required headers produce valid responses

---

## Phase 5: Storage (`IProviderStore`)

### Purpose
Persist validated providers and manage the provider registry.

### Storage Format

Providers are stored in `~/.config/koware/providers/` as individual JSON files:

```
~/.config/koware/
├── appsettings.user.json          # Main config, references active providers
├── providers/
│   ├── builtin/
│   │   ├── allanime.json
│   │   └── allmanga.json
│   └── custom/
│       ├── mysite-abc123.json     # Auto-generated
│       └── animepahe-custom.json  # User-modified
```

### Provider Registration

```csharp
public interface IProviderStore
{
    Task<IReadOnlyList<ProviderInfo>> ListAsync(CancellationToken ct);
    Task<DynamicProviderConfig?> GetAsync(string slug, CancellationToken ct);
    Task SaveAsync(DynamicProviderConfig config, CancellationToken ct);
    Task DeleteAsync(string slug, CancellationToken ct);
    Task SetActiveAsync(string slug, ProviderType type, CancellationToken ct);
}
```

---

## CLI Command Design

### Main Command

```
koware provider autoconfig <url> [options]

Arguments:
  <url>                     Website URL to analyze (e.g., https://example.com)

Options:
  --name <name>             Custom provider name (default: derived from domain)
  --type <anime|manga|both> Force content type detection
  --test-query <query>      Custom search query for validation (default: "One Piece")
  --skip-validation         Save config without validation
  --dry-run                 Analyze and show config without saving
  --verbose                 Show detailed analysis steps
  --timeout <seconds>       Analysis timeout (default: 60)
```

### Sub-commands

```
koware provider list                    # List all providers (builtin + custom)
koware provider show <name>             # Show provider details
koware provider test <name>             # Re-validate a provider
koware provider edit <name>             # Open provider JSON in editor
koware provider remove <name>           # Remove custom provider
koware provider set-default <name>      # Set as default for anime/manga
koware provider export <name> [--file]  # Export provider config
koware provider import <file|url>       # Import provider config
```

### Interactive Flow

```
$ koware provider autoconfig https://example-anime.com

🔍 Analyzing https://example-anime.com...

Phase 1: Site Probing
  ✓ DNS resolved (Cloudflare CDN detected)
  ✓ TLS connection established
  ✓ JavaScript SPA detected (React)
  ✓ Found 3 potential API endpoints

Phase 2: Content Analysis
  ✓ GraphQL API discovered at /api
  ✓ Content type: Anime streaming
  ✓ Search pattern: GraphQL query
  ✓ Episode pattern: numbered list
  ✓ Stream pattern: HLS manifests

Phase 3: Schema Generation
  ✓ Matched template: GraphQL-AllAnime
  ✓ Generated provider config

Phase 4: Validation
  ✓ Search: Found 15 results for "One Piece"
  ✓ Episodes: Resolved 1000+ episodes
  ✓ Streams: Found 4 quality variants
  ✓ All checks passed!

Provider "example-anime" created successfully!

To use: koware watch "Naruto" --provider example-anime
To set as default: koware provider set-default example-anime
```

---

## Domain Models

### New Project: `Koware.Autoconfig`

```
Koware.Autoconfig/
├── Analysis/
│   ├── ISiteProber.cs
│   ├── SiteProber.cs
│   ├── IContentAnalyzer.cs
│   ├── ContentAnalyzer.cs
│   ├── IApiDiscoveryEngine.cs
│   └── ApiDiscoveryEngine.cs
├── Detection/
│   ├── INetworkFingerprinter.cs
│   ├── NetworkFingerprinter.cs
│   ├── IContentPatternMatcher.cs
│   ├── ContentPatternMatcher.cs
│   └── PatternLibrary.cs
├── Generation/
│   ├── ISchemaGenerator.cs
│   ├── SchemaGenerator.cs
│   ├── IProviderTemplateLibrary.cs
│   ├── ProviderTemplateLibrary.cs
│   └── Templates/
│       ├── GraphQLTemplate.cs
│       ├── RestTemplate.cs
│       └── HtmlScraperTemplate.cs
├── Validation/
│   ├── IConfigValidator.cs
│   └── ConfigValidator.cs
├── Storage/
│   ├── IProviderStore.cs
│   └── ProviderStore.cs
├── Models/
│   ├── SiteProfile.cs
│   ├── ContentSchema.cs
│   ├── DynamicProviderConfig.cs
│   └── ValidationResult.cs
└── Orchestration/
    ├── IAutoconfigOrchestrator.cs
    └── AutoconfigOrchestrator.cs
```

---

## Dynamic Provider Runtime

### Execution Engine

A new `DynamicCatalog` implementation that can execute any `DynamicProviderConfig`:

```csharp
public sealed class DynamicAnimeCatalog : IAnimeCatalog
{
    private readonly DynamicProviderConfig _config;
    private readonly HttpClient _httpClient;
    private readonly ITransformEngine _transforms;

    public async Task<IReadOnlyCollection<Anime>> SearchAsync(string query, CancellationToken ct)
    {
        var request = BuildRequest(_config.Search, query);
        var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        
        return _transforms.Extract<Anime>(json, _config.Search.ResultMapping);
    }
}
```

### Transform Engine

Handles data extraction and transformation:

```csharp
public interface ITransformEngine
{
    IReadOnlyList<T> Extract<T>(string data, IReadOnlyList<FieldMapping> mappings);
}

public enum TransformType
{
    None,
    DecodeBase64,
    DecodeHex,
    ParseJson,
    RegexExtract,
    UrlDecode,
    PrependHost,
    Custom  // For site-specific decoders
}
```

---

## Advanced Features

### 1. Cloudflare Bypass Strategy
```csharp
public interface ICloudflareHandler
{
    bool RequiresBypass(SiteProfile profile);
    Task<CookieContainer> SolveChallengeAsync(Uri url, CancellationToken ct);
}
```
- Integration with FlareSolverr for protected sites
- Cookie persistence for session management

### 2. Rate Limit Detection
```csharp
public record RateLimitConfig
{
    public int RequestsPerMinute { get; init; }
    public TimeSpan RetryAfterDefault { get; init; }
    public bool UseExponentialBackoff { get; init; }
}
```
- Analyze 429 responses and Retry-After headers
- Auto-configure rate limiting

### 3. Provider Updates
```csharp
public interface IProviderUpdater
{
    Task<bool> CheckForUpdatesAsync(string slug, CancellationToken ct);
    Task<DynamicProviderConfig> RefreshConfigAsync(string slug, CancellationToken ct);
}
```
- Detect when a site changes structure
- Suggest re-running autoconfig

### 4. Community Provider Sharing
```
koware provider share <name>           # Generate shareable config
koware provider import <url>           # Import from URL/gist
```

---

## Security Considerations

1. **URL validation** - Sanitize input URLs, prevent SSRF
2. **Content limits** - Cap response sizes to prevent memory exhaustion
3. **Timeout enforcement** - All network operations have strict timeouts
4. **No credential storage** - Providers don't store passwords
5. **Sandboxed transforms** - Custom transforms run in restricted context

---

## Implementation Phases

### Phase A: Core Infrastructure (MVP)
- [ ] `DynamicProviderConfig` model
- [ ] `ProviderStore` (JSON file storage)
- [ ] `DynamicAnimeCatalog` / `DynamicMangaCatalog`
- [ ] CLI: `provider list`, `provider show`, `provider remove`

### Phase B: Analysis Engine
- [ ] `SiteProber` with basic detection
- [ ] `ApiDiscoveryEngine` for GraphQL/REST
- [ ] `ContentPatternMatcher` for common patterns
- [ ] CLI: `provider autoconfig` (basic)

### Phase C: Template Library
- [ ] GraphQL template (AllAnime-style)
- [ ] REST template (Consumet-style)
- [ ] HTML scraping template
- [ ] Template matching logic

### Phase D: Validation & Polish
- [ ] `ConfigValidator` with live testing
- [ ] Interactive CLI with progress
- [ ] Error recovery and suggestions
- [ ] Documentation and examples

### Phase E: Advanced Features
- [ ] Cloudflare handling
- [ ] Rate limit detection
- [ ] Provider updates
- [ ] Community sharing

---

## Example Generated Config

```json
{
  "name": "Example Anime",
  "slug": "example-anime",
  "type": "Anime",
  "version": "1.0.0",
  "generatedAt": "2024-12-13T20:00:00Z",
  "hosts": {
    "baseHost": "example-anime.com",
    "apiBase": "https://api.example-anime.com",
    "referer": "https://example-anime.com/",
    "userAgent": "Mozilla/5.0 ...",
    "customHeaders": {
      "X-Requested-With": "XMLHttpRequest"
    }
  },
  "search": {
    "method": "GraphQL",
    "endpoint": "/api",
    "queryTemplate": "query($search: String!) { shows(search: $search) { edges { _id name thumbnail } } }",
    "resultMapping": [
      { "sourcePath": "$.data.shows.edges[*]._id", "targetField": "Id" },
      { "sourcePath": "$.data.shows.edges[*].name", "targetField": "Title" },
      { "sourcePath": "$.data.shows.edges[*].thumbnail", "targetField": "CoverImage", "transform": "PrependHost" }
    ]
  },
  "content": {
    "episodes": {
      "method": "GraphQL",
      "endpoint": "/api",
      "queryTemplate": "query($showId: String!) { show(_id: $showId) { availableEpisodesDetail } }",
      "resultMapping": [
        { "sourcePath": "$.data.show.availableEpisodesDetail.sub[*]", "targetField": "Number" }
      ]
    }
  },
  "media": {
    "streams": {
      "method": "GraphQL",
      "endpoint": "/api",
      "queryTemplate": "query($showId: String!, $ep: String!) { episode(showId: $showId, episodeString: $ep) { sourceUrls } }",
      "resultMapping": [
        { "sourcePath": "$.data.episode.sourceUrls[*].sourceUrl", "targetField": "Url", "transform": "DecodeCustom" }
      ],
      "customDecoder": "AllAnimeSourceDecoder"
    }
  },
  "transforms": [
    {
      "name": "DecodeCustom",
      "type": "Regex",
      "pattern": "...",
      "replacement": "..."
    }
  ]
}
```

---

## Summary

The provider autoconfig system transforms Koware from a tool with hardcoded providers to a **universal anime/manga client** that can adapt to any site. Key innovations:

1. **Multi-phase intelligent analysis** - Systematic discovery of site structure
2. **Template-based generation** - Reusable patterns for common site architectures
3. **Live validation** - Ensures generated configs actually work
4. **Dynamic runtime** - Single execution engine for all provider types
5. **User empowerment** - Users can add any site without code changes

This architecture positions Koware as a truly extensible platform while maintaining the simplicity of the current CLI experience.
