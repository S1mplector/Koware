// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Summary information about a provider for listing purposes.
/// </summary>
public sealed record ProviderInfo
{
    /// <summary>Provider slug identifier.</summary>
    public required string Slug { get; init; }
    
    /// <summary>Display name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Provider type.</summary>
    public ProviderType Type { get; init; }
    
    /// <summary>Base host URL.</summary>
    public required string BaseHost { get; init; }
    
    /// <summary>Whether this is a built-in provider.</summary>
    public bool IsBuiltIn { get; init; }
    
    /// <summary>Whether this is the active/default provider for its type.</summary>
    public bool IsActive { get; init; }
    
    /// <summary>When the provider was last validated.</summary>
    public DateTimeOffset? LastValidatedAt { get; init; }
    
    /// <summary>Configuration version.</summary>
    public string Version { get; init; } = "1.0.0";
    
    /// <summary>Create from a full config.</summary>
    public static ProviderInfo FromConfig(DynamicProviderConfig config, bool isActive = false) =>
        new()
        {
            Slug = config.Slug,
            Name = config.Name,
            Type = config.Type,
            BaseHost = config.Hosts.BaseHost,
            IsBuiltIn = config.IsBuiltIn,
            IsActive = isActive,
            LastValidatedAt = config.LastValidatedAt,
            Version = config.Version
        };
}
