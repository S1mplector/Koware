// Author: Ilgaz Mehmetoğlu
namespace Koware.Autoconfig.Models;

/// <summary>
/// Result of validating a provider configuration.
/// </summary>
public sealed record ValidationResult
{
    /// <summary>Whether the configuration is valid and working.</summary>
    public bool IsValid { get; init; }
    
    /// <summary>Individual validation checks performed.</summary>
    public IReadOnlyList<ValidationCheck> Checks { get; init; } = [];
    
    /// <summary>Suggested fixes if validation failed.</summary>
    public DynamicProviderConfig? SuggestedFixes { get; init; }
    
    /// <summary>Overall error message if validation failed.</summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>Time taken to validate.</summary>
    public TimeSpan Duration { get; init; }
    
    /// <summary>Create a successful validation result.</summary>
    public static ValidationResult Success(IReadOnlyList<ValidationCheck> checks, TimeSpan duration) =>
        new()
        {
            IsValid = true,
            Checks = checks,
            Duration = duration
        };
    
    /// <summary>Create a failed validation result.</summary>
    public static ValidationResult Failure(string error, IReadOnlyList<ValidationCheck> checks, TimeSpan duration) =>
        new()
        {
            IsValid = false,
            ErrorMessage = error,
            Checks = checks,
            Duration = duration
        };
}

/// <summary>
/// Individual validation check result.
/// </summary>
public sealed record ValidationCheck
{
    /// <summary>Name of the check.</summary>
    public required string Name { get; init; }
    
    /// <summary>Description of what was tested.</summary>
    public string? Description { get; init; }
    
    /// <summary>Whether the check passed.</summary>
    public bool Passed { get; init; }
    
    /// <summary>Error message if check failed.</summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>Sample data returned (for successful checks).</summary>
    public string? SampleData { get; init; }
    
    /// <summary>Time taken for this check.</summary>
    public TimeSpan Duration { get; init; }
    
    /// <summary>Create a passed check.</summary>
    public static ValidationCheck Pass(string name, string? description = null, string? sampleData = null, TimeSpan duration = default) =>
        new()
        {
            Name = name,
            Description = description,
            Passed = true,
            SampleData = sampleData,
            Duration = duration
        };
    
    /// <summary>Create a failed check.</summary>
    public static ValidationCheck Fail(string name, string error, string? description = null, TimeSpan duration = default) =>
        new()
        {
            Name = name,
            Description = description,
            Passed = false,
            ErrorMessage = error,
            Duration = duration
        };
}

/// <summary>
/// Result of the autoconfig analysis process.
/// </summary>
public sealed record AutoconfigResult
{
    /// <summary>Whether the analysis was successful.</summary>
    public bool IsSuccess { get; init; }
    
    /// <summary>The generated provider configuration.</summary>
    public DynamicProviderConfig? Config { get; init; }
    
    /// <summary>Site profile gathered during analysis.</summary>
    public SiteProfile? SiteProfile { get; init; }
    
    /// <summary>Content schema discovered.</summary>
    public ContentSchema? ContentSchema { get; init; }
    
    /// <summary>Validation result if validation was performed.</summary>
    public ValidationResult? ValidationResult { get; init; }
    
    /// <summary>Error message if analysis failed.</summary>
    public string? ErrorMessage { get; init; }
    
    /// <summary>Warnings encountered during analysis.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
    
    /// <summary>Analysis phases completed.</summary>
    public IReadOnlyList<AnalysisPhase> Phases { get; init; } = [];
    
    /// <summary>Total time taken for analysis.</summary>
    public TimeSpan Duration { get; init; }
    
    /// <summary>Create a successful result.</summary>
    public static AutoconfigResult Success(
        DynamicProviderConfig config,
        SiteProfile profile,
        ContentSchema schema,
        ValidationResult? validation,
        IReadOnlyList<AnalysisPhase> phases,
        TimeSpan duration) =>
        new()
        {
            IsSuccess = true,
            Config = config,
            SiteProfile = profile,
            ContentSchema = schema,
            ValidationResult = validation,
            Phases = phases,
            Duration = duration
        };
    
    /// <summary>Create a failed result.</summary>
    public static AutoconfigResult Failure(
        string error,
        SiteProfile? profile,
        IReadOnlyList<AnalysisPhase> phases,
        TimeSpan duration) =>
        new()
        {
            IsSuccess = false,
            ErrorMessage = error,
            SiteProfile = profile,
            Phases = phases,
            Duration = duration
        };
}

/// <summary>
/// Information about a completed analysis phase.
/// </summary>
public sealed record AnalysisPhase
{
    /// <summary>Phase name.</summary>
    public required string Name { get; init; }
    
    /// <summary>Whether the phase succeeded.</summary>
    public bool Succeeded { get; init; }
    
    /// <summary>Status message.</summary>
    public string? Message { get; init; }
    
    /// <summary>Time taken.</summary>
    public TimeSpan Duration { get; init; }
    
    /// <summary>Sub-steps within this phase.</summary>
    public IReadOnlyList<string> Steps { get; init; } = [];
}
