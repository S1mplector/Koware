// Author: Ilgaz Mehmetoğlu
using Koware.Autoconfig.Models;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Engine for extracting and transforming data from API responses.
/// </summary>
public interface ITransformEngine
{
    /// <summary>
    /// Extract values from a JSON response using field mappings.
    /// </summary>
    IReadOnlyList<Dictionary<string, object?>> ExtractAll(
        string json, 
        IReadOnlyList<FieldMapping> mappings,
        string? arrayPath = null);
    
    /// <summary>
    /// Apply a single transform to a value.
    /// </summary>
    string? ApplyTransform(string? value, TransformType type, string? parameters = null);
    
    /// <summary>
    /// Apply a custom transform rule.
    /// </summary>
    string? ApplyCustomTransform(string? value, TransformRule rule);
    
    /// <summary>
    /// Register a custom decoder function.
    /// </summary>
    void RegisterDecoder(string name, Func<string, string> decoder);
}
