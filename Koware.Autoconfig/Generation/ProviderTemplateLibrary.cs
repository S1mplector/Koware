// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Generation.Templates;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Generation;

/// <summary>
/// Library of provider templates for matching and generating configurations.
/// </summary>
public sealed class ProviderTemplateLibrary : IProviderTemplateLibrary
{
    private readonly IReadOnlyList<IProviderTemplate> _templates;
    private readonly ILogger<ProviderTemplateLibrary> _logger;

    public ProviderTemplateLibrary(ILogger<ProviderTemplateLibrary> logger)
    {
        _logger = logger;
        
        // Register all templates in priority order
        _templates =
        [
            new GraphQLAnimeTemplate(),
            new GraphQLMangaTemplate(),
            new RestAnimeTemplate(),
            new RestMangaTemplate(),
            new GenericTemplate() // Fallback
        ];
    }

    public IProviderTemplate? FindBestMatch(SiteProfile profile, ContentSchema schema)
    {
        var scores = new List<(IProviderTemplate template, int score)>();

        foreach (var template in _templates)
        {
            var score = template.CalculateMatchScore(profile, schema);
            _logger.LogDebug("Template '{Template}' scored {Score} for {Site}", 
                template.Name, score, profile.BaseUrl.Host);
            
            if (score > 0)
            {
                scores.Add((template, score));
            }
        }

        if (scores.Count == 0)
        {
            _logger.LogWarning("No matching templates found for {Site}", profile.BaseUrl.Host);
            return null;
        }

        var best = scores.OrderByDescending(s => s.score).First();
        _logger.LogInformation("Best matching template for {Site}: {Template} (score: {Score})",
            profile.BaseUrl.Host, best.template.Name, best.score);

        return best.template;
    }

    public IReadOnlyList<IProviderTemplate> GetAll() => _templates;

    public IProviderTemplate? GetById(string id)
    {
        return _templates.FirstOrDefault(t => 
            t.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
    }
}
