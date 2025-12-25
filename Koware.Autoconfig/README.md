# Koware.Autoconfig

Intelligent provider auto-configuration system for Koware.

## Purpose

This library provides the ability to analyze any anime/manga website and automatically generate a working provider configuration. It enables users to add support for new sites without writing code.

## Key Components

### Analysis
- **SiteProber** - Probes websites to detect structure, frameworks, and APIs
- **ApiDiscoveryEngine** - Discovers and tests API endpoints
- **ContentPatternMatcher** - Analyzes content structure patterns
- **IntelligentPatternEngine** - Advanced pattern recognition with heuristics
- **GraphQLIntrospector** - Deep GraphQL schema discovery and query generation

### Generation
- **SchemaGenerator** - Generates provider configurations from analyzed data
- **ProviderTemplateLibrary** - Pre-built templates for common site architectures

### Validation
- **ConfigValidator** - Tests generated configurations with live requests

### Runtime
- **TransformEngine** - Applies field mappings and data transformations
- **DynamicAnimeCatalog** / **DynamicMangaCatalog** - Runtime provider implementations

### Storage
- **ProviderStore** - Persists and manages provider configurations

## Usage

```csharp
var orchestrator = services.GetRequiredService<IAutoconfigOrchestrator>();
var result = await orchestrator.AnalyzeAndConfigureAsync("https://example-anime.com");

if (result.IsSuccess)
{
    Console.WriteLine($"Provider '{result.Config.Name}' created successfully!");
}
```

## Advanced Features

### Intelligent Pattern Recognition

The `IntelligentPatternEngine` uses heuristics to identify:
- Known site architectures (AllAnime, Gogoanime, Aniwatch, MangaDex, etc.)
- API patterns and endpoint purposes
- Encoding/decoding requirements
- Content types (anime vs manga)

```csharp
var patternEngine = services.GetRequiredService<IIntelligentPatternEngine>();
var analysis = await patternEngine.AnalyzeAsync(siteProfile);

// Get site fingerprint
Console.WriteLine($"Architecture: {analysis.Fingerprint.Architecture}");
Console.WriteLine($"Technologies: {string.Join(", ", analysis.Fingerprint.Technologies)}");

// View recommendations
foreach (var rec in analysis.Recommendations)
{
    Console.WriteLine($"- {rec}");
}
```

### GraphQL Introspection

The `GraphQLIntrospector` performs deep schema discovery:

```csharp
var introspector = services.GetRequiredService<IGraphQLIntrospector>();
var schema = await introspector.IntrospectAsync(endpoint, siteProfile);

if (schema != null)
{
    Console.WriteLine($"Found {schema.Queries.Count} queries");
    
    // Generate optimized queries
    var queries = introspector.GenerateQueries(schema, ProviderType.Anime);
    foreach (var query in queries)
    {
        Console.WriteLine($"{query.Name}: {query.Purpose} (confidence: {query.Confidence:P0})");
    }
}
```

## Supported Site Architectures

| Architecture | API Type | Auto-Detection |
|--------------|----------|----------------|
| AllAnime     | GraphQL  | ✓              |
| Gogoanime    | REST     | ✓              |
| Aniwatch     | REST     | ✓              |
| MangaDex     | REST     | ✓              |
| Generic GraphQL | GraphQL | ✓           |
| Generic REST | REST     | ✓              |

## Configuration Options

```csharp
var options = new AutoconfigOptions
{
    ProviderName = "MyCustomProvider",  // Custom name
    ForceType = ProviderType.Anime,     // Force content type
    TestQuery = "naruto",               // Custom test query
    SkipValidation = false,             // Run validation
    DryRun = false,                     // Save config
    Timeout = TimeSpan.FromSeconds(60)  // Analysis timeout
};
```
