// Author: Ilgaz Mehmetoğlu
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Koware.Autoconfig.Models;
using Microsoft.Extensions.Logging;

namespace Koware.Autoconfig.Runtime;

/// <summary>
/// Default implementation of the transform engine.
/// </summary>
public sealed class TransformEngine : ITransformEngine
{
    private readonly ILogger<TransformEngine> _logger;
    private readonly Dictionary<string, Func<string, string>> _customDecoders = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Regex PathRegex = new(@"(\w+)|\[(\d+)\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly ConcurrentDictionary<string, PathSegment[]> PathCache = new(StringComparer.Ordinal);

    public TransformEngine(ILogger<TransformEngine> logger)
    {
        _logger = logger;
    }

    public IReadOnlyList<Dictionary<string, object?>> ExtractAll(
        string json,
        IReadOnlyList<FieldMapping> mappings,
        string? arrayPath = null)
    {
        var results = new List<Dictionary<string, object?>>();
        
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            
            // Navigate to array if path specified
            var arrayElement = arrayPath != null 
                ? NavigateToPath(root, arrayPath) 
                : root;
            
            if (arrayElement.ValueKind != JsonValueKind.Array)
            {
                // Single object, wrap in result
                var item = ExtractSingle(arrayElement, mappings);
                if (item.Count > 0)
                    results.Add(item);
                return results;
            }
            
            foreach (var element in arrayElement.EnumerateArray())
            {
                var item = ExtractSingle(element, mappings);
                if (item.Count > 0)
                    results.Add(item);
            }
        }
        catch (JsonException)
        {
            // Silently ignore non-JSON responses (e.g., HTML error pages)
            _logger.LogDebug("Response is not valid JSON, skipping extraction");
        }
        
        return results;
    }

    private Dictionary<string, object?> ExtractSingle(JsonElement element, IReadOnlyList<FieldMapping> mappings)
    {
        var result = new Dictionary<string, object?>();
        
        foreach (var mapping in mappings)
        {
            try
            {
                var value = ExtractValue(element, mapping.SourcePath);
                var transformed = ApplyTransform(value, mapping.Transform, mapping.TransformParams);
                result[mapping.TargetField] = transformed;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract field {Field} from path {Path}", 
                    mapping.TargetField, mapping.SourcePath);
            }
        }
        
        return result;
    }

    private string? ExtractValue(JsonElement element, string path)
    {
        // Simple JSONPath-like navigation
        // Supports: $.field, $.nested.field, $[0], $.array[0].field
        var current = element;
        var segments = GetPathSegments(path);
        
        foreach (var segment in segments)
        {
            if (segment.IsIndex)
            {
                if (current.ValueKind == JsonValueKind.Array && segment.Index < current.GetArrayLength())
                {
                    current = current[segment.Index];
                }
                else
                {
                    return null;
                }
            }
            else if (!string.IsNullOrEmpty(segment.PropertyName))
            {
                if (current.ValueKind == JsonValueKind.Object && 
                    current.TryGetProperty(segment.PropertyName, out var prop))
                {
                    current = prop;
                }
                else
                {
                    return null;
                }
            }
        }
        
        return current.ValueKind switch
        {
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => null,
            _ => current.GetRawText()
        };
    }

    private JsonElement NavigateToPath(JsonElement root, string path)
    {
        var segments = GetPathSegments(path);
        var current = root;
        
        foreach (var segment in segments)
        {
            if (segment.IsIndex)
            {
                if (current.ValueKind == JsonValueKind.Array)
                    current = current[segment.Index];
            }
            else if (!string.IsNullOrEmpty(segment.PropertyName))
            {
                if (current.TryGetProperty(segment.PropertyName, out var prop))
                    current = prop;
            }
        }
        
        return current;
    }

    private static PathSegment[] GetPathSegments(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return Array.Empty<PathSegment>();
        }

        return PathCache.GetOrAdd(path, static p =>
        {
            var segments = new List<PathSegment>();
            var cleanPath = p.TrimStart('$', '.');

            var matches = PathRegex.Matches(cleanPath);

            foreach (Match match in matches)
            {
                if (match.Groups[1].Success)
                {
                    segments.Add(new PathSegment { PropertyName = match.Groups[1].Value });
                }
                else if (match.Groups[2].Success)
                {
                    segments.Add(new PathSegment { IsIndex = true, Index = int.Parse(match.Groups[2].Value) });
                }
            }

            return segments.ToArray();
        });
    }

    public string? ApplyTransform(string? value, TransformType type, string? parameters = null)
    {
        if (value == null)
            return null;
            
        return type switch
        {
            TransformType.None => value,
            TransformType.DecodeBase64 => DecodeBase64(value),
            TransformType.DecodeHex => DecodeHex(value),
            TransformType.UrlDecode => HttpUtility.UrlDecode(value),
            TransformType.PrependHost => PrependHost(value, parameters),
            TransformType.RegexExtract => RegexExtract(value, parameters),
            TransformType.ParseJson => value, // Already handled in extraction
            TransformType.Custom => ApplyCustomDecoder(value, parameters),
            _ => value
        };
    }

    public string? ApplyCustomTransform(string? value, TransformRule rule)
    {
        if (value == null)
            return null;
            
        return rule.Type switch
        {
            TransformType.RegexExtract when rule.Pattern != null => 
                RegexExtract(value, rule.Pattern, rule.Replacement),
            TransformType.Custom when rule.DecoderClass != null => 
                ApplyCustomDecoder(value, rule.DecoderClass),
            _ => ApplyTransform(value, rule.Type, rule.Pattern)
        };
    }

    public void RegisterDecoder(string name, Func<string, string> decoder)
    {
        _customDecoders[name] = decoder;
        _logger.LogDebug("Registered custom decoder '{Name}'", name);
    }

    private static string DecodeBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return value;
        }
    }

    private static string DecodeHex(string value)
    {
        try
        {
            var bytes = new byte[value.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
            {
                bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return value;
        }
    }

    private static string PrependHost(string value, string? host)
    {
        if (string.IsNullOrEmpty(host) || Uri.IsWellFormedUriString(value, UriKind.Absolute))
            return value;
            
        return host.TrimEnd('/') + "/" + value.TrimStart('/');
    }

    private static string? RegexExtract(string value, string? pattern, string? replacement = null)
    {
        if (string.IsNullOrEmpty(pattern))
            return value;
            
        var match = Regex.Match(value, pattern);
        if (!match.Success)
            return value;
            
        if (!string.IsNullOrEmpty(replacement))
            return Regex.Replace(value, pattern, replacement);
            
        return match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
    }

    private string ApplyCustomDecoder(string value, string? decoderName)
    {
        if (string.IsNullOrEmpty(decoderName))
            return value;
            
        if (_customDecoders.TryGetValue(decoderName, out var decoder))
        {
            try
            {
                return decoder(value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Custom decoder '{Name}' failed", decoderName);
            }
        }
        
        return value;
    }

    private struct PathSegment
    {
        public string? PropertyName;
        public bool IsIndex;
        public int Index;
    }
}
