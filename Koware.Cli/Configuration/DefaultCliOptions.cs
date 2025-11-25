// Author: Ilgaz Mehmetoğlu
// Configuration model for CLI defaults such as quality and preferred match index.
namespace Koware.Cli.Configuration;

public sealed class DefaultCliOptions
{
    public string? Quality { get; set; }

    public int? PreferredMatchIndex { get; set; }
}
