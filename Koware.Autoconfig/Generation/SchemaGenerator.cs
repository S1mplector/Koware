// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Generation;

/// <summary>
/// Generates provider configurations from analyzed site data.
/// </summary>
public sealed class SchemaGenerator : ISchemaGenerator
{
    private readonly IProviderTemplateLibrary _templateLibrary;
    private readonly ILogger<SchemaGenerator> _logger;

    public SchemaGenerator(
        IProviderTemplateLibrary templateLibrary,
        ILogger<SchemaGenerator> logger)
    {
        _templateLibrary = templateLibrary;
        _logger = logger;
    }

    public DynamicProviderConfig Generate(
        SiteProfile profile,
        ContentSchema schema,
        string? providerName = null)
    {
        // Determine provider name
        var name = providerName ?? GenerateProviderName(profile);
        
        _logger.LogInformation("Generating provider config for '{Name}' from {Url}", name, profile.BaseUrl);

        // Find best matching template
        var template = _templateLibrary.FindBestMatch(profile, schema);
        
        if (template == null)
        {
            _logger.LogWarning("No matching template found, using generic template");
            template = _templateLibrary.GetById("generic");
        }

        if (template == null)
        {
            throw new InvalidOperationException("No templates available to generate configuration");
        }

        _logger.LogInformation("Using template '{Template}' for {Name}", template.Name, name);

        // Apply template to generate config
        var config = template.Apply(profile, schema, name);

        // Add any additional metadata
        config = config with
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            Notes = BuildNotes(profile, schema, template)
        };

        return config;
    }

    private static string GenerateProviderName(SiteProfile profile)
    {
        // Try site title first
        if (!string.IsNullOrWhiteSpace(profile.SiteTitle))
        {
            var titlePart = profile.SiteTitle.Split(['-', '|', '–', ':', '•'])[0].Trim();
            if (!string.IsNullOrWhiteSpace(titlePart) && titlePart.Length >= 2)
            {
                return titlePart;
            }
        }
        
        // Fall back to hostname
        var host = profile.BaseUrl.Host;
        
        // Remove common prefixes
        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        
        // Remove TLD
        var dotIndex = host.LastIndexOf('.');
        if (dotIndex > 0)
            host = host[..dotIndex];

        // Capitalize first letter
        if (host.Length > 0)
            host = char.ToUpperInvariant(host[0]) + host[1..];

        return host;
    }

    private static string? BuildNotes(SiteProfile profile, ContentSchema schema, IProviderTemplate template)
    {
        var notes = new List<string>();

        notes.Add($"Generated using '{template.Name}' template");
        notes.Add($"Site type: {profile.Type}");
        notes.Add($"Content category: {profile.Category}");

        if (profile.HasCloudflareProtection)
            notes.Add("WARNING: Cloudflare protection detected - may require additional configuration");

        if (profile.RequiresJavaScript)
            notes.Add("Site requires JavaScript - ensure requests include proper headers");

        if (!string.IsNullOrEmpty(profile.JsFramework))
            notes.Add($"JS Framework: {profile.JsFramework}");

        if (schema.Endpoints.Count == 0)
            notes.Add("WARNING: No API endpoints discovered - configuration may be incomplete");

        return string.Join(Environment.NewLine, notes);
    }
}
