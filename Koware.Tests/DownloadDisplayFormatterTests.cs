using Koware.Cli.Downloads;
using Xunit;

namespace Koware.Tests;

public class DownloadDisplayFormatterTests
{
    [Fact]
    public void FormatNumberRanges_CompressesWholeNumbersAndKeepsDecimals()
    {
        var formatted = DownloadDisplayFormatter.FormatNumberRanges(new[] { 1d, 2d, 3d, 10.5d, 11d, 12d });

        Assert.Equal("1-3, 10.5, 11-12", formatted);
    }

    [Fact]
    public void FormatNumber_UsesInvariantDecimalFormat()
    {
        Assert.Equal("12.5", DownloadDisplayFormatter.FormatNumber(12.5));
    }
}
