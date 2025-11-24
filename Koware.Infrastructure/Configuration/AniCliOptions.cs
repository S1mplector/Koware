namespace Koware.Infrastructure.Configuration;

public sealed class AniCliOptions
{
    public string BaseUrl { get; set; } = "https://ani-cli.example";

    public string UserAgent { get; set; } = "Koware/0.1 (stub)";

    public int SampleEpisodeCount { get; set; } = 3;
}
