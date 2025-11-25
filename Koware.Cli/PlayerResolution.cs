// Author: Ilgaz Mehmetoglu
// Represents a resolved player executable and candidate names for logging/fallback.
internal sealed record PlayerResolution(string? Path, string Name, IReadOnlyList<string> Candidates);
