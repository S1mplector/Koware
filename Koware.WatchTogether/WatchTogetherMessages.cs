// Author: Ilgaz Mehmetoğlu
using System.Text.Json;

namespace Koware.WatchTogether;

public static class WatchTogetherRoles
{
    public const string Host = "host";
    public const string Guest = "guest";
    public const string System = "system";
}

public static class WatchTogetherMessageTypes
{
    public const string Welcome = "welcome";
    public const string Hello = "hello";
    public const string Content = "content";
    public const string State = "state";
    public const string Participant = "participant";
    public const string Error = "error";
}

public sealed record WatchTogetherSessionOptions(
    Uri RelayUri,
    string RoomCode,
    string ClientId,
    string DisplayName,
    string Role)
{
    public bool IsHost => Role.Equals(WatchTogetherRoles.Host, StringComparison.OrdinalIgnoreCase);
}

public sealed record WatchTogetherSubtitle(
    string Label,
    string Url,
    string? Language = null);

public sealed record WatchTogetherContent
{
    public string? Query { get; init; }

    public int? EpisodeNumber { get; init; }

    public string? Quality { get; init; }

    public string? Title { get; init; }

    public string StreamUrl { get; init; } = string.Empty;

    public string? Referrer { get; init; }

    public string? UserAgent { get; init; }

    public IReadOnlyList<WatchTogetherSubtitle> Subtitles { get; init; } = Array.Empty<WatchTogetherSubtitle>();
}

public sealed record WatchTogetherPlaybackState
{
    public bool IsPlaying { get; init; }

    public long PositionMs { get; init; }

    public double Rate { get; init; } = 1.0;

    public long SentAtUnixMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed record WatchTogetherMessage
{
    public string Type { get; init; } = string.Empty;

    public string? RoomCode { get; init; }

    public string? ClientId { get; init; }

    public string? Name { get; init; }

    public string? Role { get; init; }

    public WatchTogetherContent? Content { get; init; }

    public WatchTogetherPlaybackState? State { get; init; }

    public string? Text { get; init; }

    public string? Error { get; init; }

    public long SentAtUnixMs { get; init; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public static class WatchTogetherJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, Options);
}
