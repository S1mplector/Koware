// Author: Ilgaz Mehmetoğlu
using System.Diagnostics;
using Koware.Autoconfig.Analysis;
using Koware.Autoconfig.Generation;
using Koware.Autoconfig.Models;
using Koware.Autoconfig.Storage;
using Koware.Autoconfig.Validation;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Orchestration;

/// <summary>
/// Orchestrates the complete autoconfig process from URL to working provider.
/// </summary>
public sealed class AutoconfigOrchestrator : IAutoconfigOrchestrator
{
    private readonly ISiteProber _siteProber;
    private readonly IApiDiscoveryEngine _apiDiscovery;
    private readonly IContentPatternMatcher _patternMatcher;
    private readonly ISchemaGenerator _schemaGenerator;
    private readonly IConfigValidator _validator;
    private readonly IProviderStore _providerStore;
    private readonly ILogger<AutoconfigOrchestrator> _logger;

    public AutoconfigOrchestrator(
        ISiteProber siteProber,
        IApiDiscoveryEngine apiDiscovery,
        IContentPatternMatcher patternMatcher,
        ISchemaGenerator schemaGenerator,
        IConfigValidator validator,
        IProviderStore providerStore,
        ILogger<AutoconfigOrchestrator> logger)
    {
        _siteProber = siteProber;
        _apiDiscovery = apiDiscovery;
        _patternMatcher = patternMatcher;
        _schemaGenerator = schemaGenerator;
        _validator = validator;
        _providerStore = providerStore;
        _logger = logger;
    }

    public async Task<AutoconfigResult> AnalyzeAndConfigureAsync(
        Uri url,
        AutoconfigOptions? options = null,
        IProgress<AutoconfigProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new AutoconfigOptions();
        var stopwatch = Stopwatch.StartNew();
        var phases = new List<AnalysisPhase>();
        var warnings = new List<string>();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.Timeout);
        var ct = timeoutCts.Token;

        _logger.LogInformation("Starting autoconfig for {Url}", url);

        // Phase 1: Site Probing
        SiteProfile? profile = null;
        var phase1Stopwatch = Stopwatch.StartNew();
        
        try
        {
            progress?.Report(new AutoconfigProgress
            {
                Phase = "Site Probing",
                Step = "Analyzing website structure",
                Percentage = 10
            });

            profile = await _siteProber.ProbeAsync(url, ct);
            phase1Stopwatch.Stop();

            var steps = new List<string>
            {
                $"Site type: {profile.Type}",
                $"Content category: {profile.Category}",
                profile.HasGraphQL ? "GraphQL API detected" : "REST/HTML site",
                profile.HasCloudflareProtection ? "Cloudflare protection detected" : "No Cloudflare"
            };

            if (profile.Errors.Count > 0)
            {
                warnings.AddRange(profile.Errors);
            }

            phases.Add(new AnalysisPhase
            {
                Name = "Site Probing",
                Succeeded = true,
                Message = $"Detected {profile.Category} site with {profile.DetectedApiEndpoints.Count} API endpoints",
                Duration = phase1Stopwatch.Elapsed,
                Steps = steps
            });

            progress?.Report(new AutoconfigProgress
            {
                Phase = "Site Probing",
                Step = "Completed",
                Percentage = 20,
                Succeeded = true,
                Message = $"Found {profile.DetectedApiEndpoints.Count} potential API endpoints"
            });
        }
        catch (Exception ex)
        {
            phase1Stopwatch.Stop();
            _logger.LogError(ex, "Site probing failed for {Url}", url);

            phases.Add(new AnalysisPhase
            {
                Name = "Site Probing",
                Succeeded = false,
                Message = ex.Message,
                Duration = phase1Stopwatch.Elapsed
            });

            stopwatch.Stop();
            return AutoconfigResult.Failure(
                $"Site probing failed: {ex.Message}",
                null,
                phases,
                stopwatch.Elapsed);
        }

        // Phase 2: API Discovery
        IReadOnlyList<ApiEndpoint> endpoints;
        var phase2Stopwatch = Stopwatch.StartNew();
        
        try
        {
            progress?.Report(new AutoconfigProgress
            {
                Phase = "Content Analysis",
                Step = "Discovering API endpoints",
                Percentage = 30
            });

            endpoints = await _apiDiscovery.DiscoverAsync(profile, ct);
            phase2Stopwatch.Stop();

            var steps = endpoints.Select(e => $"{e.Purpose}: {e.Type} at {e.Url.PathAndQuery}").ToList();

            phases.Add(new AnalysisPhase
            {
                Name = "API Discovery",
                Succeeded = endpoints.Count > 0,
                Message = $"Discovered {endpoints.Count} API endpoints",
                Duration = phase2Stopwatch.Elapsed,
                Steps = steps
            });

            progress?.Report(new AutoconfigProgress
            {
                Phase = "Content Analysis",
                Step = "API discovery completed",
                Percentage = 40,
                Succeeded = endpoints.Count > 0,
                Message = $"Found {endpoints.Count} endpoints"
            });

            if (endpoints.Count == 0)
            {
                warnings.Add("No API endpoints discovered - configuration may be incomplete");
            }
        }
        catch (Exception ex)
        {
            phase2Stopwatch.Stop();
            _logger.LogWarning(ex, "API discovery failed, continuing with limited info");
            
            endpoints = [];
            warnings.Add($"API discovery failed: {ex.Message}");

            phases.Add(new AnalysisPhase
            {
                Name = "API Discovery",
                Succeeded = false,
                Message = ex.Message,
                Duration = phase2Stopwatch.Elapsed
            });
        }

        // Phase 3: Pattern Matching
        ContentSchema schema;
        var phase3Stopwatch = Stopwatch.StartNew();
        
        try
        {
            progress?.Report(new AutoconfigProgress
            {
                Phase = "Content Analysis",
                Step = "Analyzing content patterns",
                Percentage = 50
            });

            schema = await _patternMatcher.AnalyzeAsync(profile, endpoints, ct);
            phase3Stopwatch.Stop();

            var steps = new List<string>();
            if (schema.SearchPattern != null)
                steps.Add($"Search: {schema.SearchPattern.Method}");
            if (schema.EpisodePattern != null)
                steps.Add("Episode pattern detected");
            if (schema.ChapterPattern != null)
                steps.Add("Chapter pattern detected");
            if (schema.MediaPattern != null)
                steps.Add($"Media pattern: {(schema.MediaPattern.RequiresDecoding ? "encoded URLs" : "direct URLs")}");

            phases.Add(new AnalysisPhase
            {
                Name = "Pattern Matching",
                Succeeded = true,
                Message = $"Analyzed content structure",
                Duration = phase3Stopwatch.Elapsed,
                Steps = steps
            });

            progress?.Report(new AutoconfigProgress
            {
                Phase = "Content Analysis",
                Step = "Pattern analysis completed",
                Percentage = 60,
                Succeeded = true
            });
        }
        catch (Exception ex)
        {
            phase3Stopwatch.Stop();
            _logger.LogError(ex, "Pattern matching failed");

            phases.Add(new AnalysisPhase
            {
                Name = "Pattern Matching",
                Succeeded = false,
                Message = ex.Message,
                Duration = phase3Stopwatch.Elapsed
            });

            stopwatch.Stop();
            return AutoconfigResult.Failure(
                $"Pattern matching failed: {ex.Message}",
                profile,
                phases,
                stopwatch.Elapsed);
        }

        // Phase 4: Configuration Generation
        DynamicProviderConfig config;
        var phase4Stopwatch = Stopwatch.StartNew();
        
        try
        {
            progress?.Report(new AutoconfigProgress
            {
                Phase = "Schema Generation",
                Step = "Generating provider configuration",
                Percentage = 70
            });

            config = _schemaGenerator.Generate(profile, schema, options.ProviderName);

            // Apply forced type if specified
            if (options.ForceType.HasValue)
            {
                config = config with { Type = options.ForceType.Value };
            }

            phase4Stopwatch.Stop();

            phases.Add(new AnalysisPhase
            {
                Name = "Schema Generation",
                Succeeded = true,
                Message = $"Generated '{config.Name}' provider config",
                Duration = phase4Stopwatch.Elapsed,
                Steps = [$"Provider: {config.Name}", $"Type: {config.Type}", $"Slug: {config.Slug}"]
            });

            progress?.Report(new AutoconfigProgress
            {
                Phase = "Schema Generation",
                Step = "Configuration generated",
                Percentage = 80,
                Succeeded = true,
                Message = $"Provider: {config.Name}"
            });
        }
        catch (Exception ex)
        {
            phase4Stopwatch.Stop();
            _logger.LogError(ex, "Configuration generation failed");

            phases.Add(new AnalysisPhase
            {
                Name = "Schema Generation",
                Succeeded = false,
                Message = ex.Message,
                Duration = phase4Stopwatch.Elapsed
            });

            stopwatch.Stop();
            return AutoconfigResult.Failure(
                $"Configuration generation failed: {ex.Message}",
                profile,
                phases,
                stopwatch.Elapsed);
        }

        // Phase 5: Validation (optional)
        ValidationResult? validationResult = null;
        
        if (!options.SkipValidation)
        {
            var phase5Stopwatch = Stopwatch.StartNew();
            
            try
            {
                progress?.Report(new AutoconfigProgress
                {
                    Phase = "Validation",
                    Step = "Testing configuration",
                    Percentage = 85
                });

                validationResult = await _validator.ValidateAsync(config, options.TestQuery, ct);
                phase5Stopwatch.Stop();

                var steps = validationResult.Checks
                    .Select(c => $"{c.Name}: {(c.Passed ? "✓" : "✗")} {c.ErrorMessage ?? ""}")
                    .ToList();

                phases.Add(new AnalysisPhase
                {
                    Name = "Validation",
                    Succeeded = validationResult.IsValid,
                    Message = validationResult.IsValid ? "All checks passed" : validationResult.ErrorMessage,
                    Duration = phase5Stopwatch.Elapsed,
                    Steps = steps
                });

                // Update config with validation timestamp
                if (validationResult.IsValid)
                {
                    config = config with { LastValidatedAt = DateTimeOffset.UtcNow };
                }

                progress?.Report(new AutoconfigProgress
                {
                    Phase = "Validation",
                    Step = validationResult.IsValid ? "Validation passed" : "Validation failed",
                    Percentage = 95,
                    Succeeded = validationResult.IsValid,
                    Message = validationResult.IsValid 
                        ? $"All {validationResult.Checks.Count} checks passed"
                        : validationResult.ErrorMessage
                });
            }
            catch (Exception ex)
            {
                phase5Stopwatch.Stop();
                _logger.LogWarning(ex, "Validation failed");
                warnings.Add($"Validation failed: {ex.Message}");

                phases.Add(new AnalysisPhase
                {
                    Name = "Validation",
                    Succeeded = false,
                    Message = ex.Message,
                    Duration = phase5Stopwatch.Elapsed
                });
            }
        }

        // Phase 6: Storage (unless dry run)
        if (!options.DryRun)
        {
            try
            {
                progress?.Report(new AutoconfigProgress
                {
                    Phase = "Storage",
                    Step = "Saving provider configuration",
                    Percentage = 98
                });

                await _providerStore.SaveAsync(config, ct);

                phases.Add(new AnalysisPhase
                {
                    Name = "Storage",
                    Succeeded = true,
                    Message = $"Saved provider '{config.Slug}'",
                    Duration = TimeSpan.Zero,
                    Steps = [$"Slug: {config.Slug}"]
                });

                _logger.LogInformation("Provider '{Name}' saved successfully", config.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save provider");
                warnings.Add($"Failed to save: {ex.Message}");

                phases.Add(new AnalysisPhase
                {
                    Name = "Storage",
                    Succeeded = false,
                    Message = ex.Message,
                    Duration = TimeSpan.Zero
                });
            }
        }

        stopwatch.Stop();

        progress?.Report(new AutoconfigProgress
        {
            Phase = "Complete",
            Step = "Autoconfig finished",
            Percentage = 100,
            Succeeded = true,
            Message = $"Provider '{config.Name}' created"
        });

        return new AutoconfigResult
        {
            IsSuccess = true,
            Config = config,
            SiteProfile = profile,
            ContentSchema = schema,
            ValidationResult = validationResult,
            Warnings = warnings,
            Phases = phases,
            Duration = stopwatch.Elapsed
        };
    }
}
