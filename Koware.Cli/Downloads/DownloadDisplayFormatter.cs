using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Koware.Cli.Downloads;

internal static class DownloadDisplayFormatter
{
    private const double IntegerEpsilon = 0.0001d;

    internal static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        var order = 0;

        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }

        return len.ToString("0.##", CultureInfo.InvariantCulture) + " " + sizes[order];
    }

    internal static string FormatNumber(double number)
    {
        if (double.IsNaN(number) || double.IsInfinity(number))
        {
            return "0";
        }

        var rounded = Math.Round(number);
        if (Math.Abs(number - rounded) < IntegerEpsilon)
        {
            return rounded.ToString("0", CultureInfo.InvariantCulture);
        }

        return number.ToString("0.###", CultureInfo.InvariantCulture);
    }

    internal static string FormatNumberRanges(IEnumerable<double> numbers)
    {
        var sorted = numbers
            .Where(n => !double.IsNaN(n) && !double.IsInfinity(n))
            .Select(n => Math.Round(n, 3, MidpointRounding.AwayFromZero))
            .Distinct()
            .OrderBy(n => n)
            .ToList();

        if (sorted.Count == 0)
        {
            return "none";
        }

        var ranges = new List<string>();
        var index = 0;

        while (index < sorted.Count)
        {
            var current = sorted[index];
            if (!IsWholeNumber(current))
            {
                ranges.Add(FormatNumber(current));
                index++;
                continue;
            }

            var start = current;
            var end = current;

            while (index + 1 < sorted.Count
                && IsWholeNumber(sorted[index + 1])
                && Math.Abs(sorted[index + 1] - end - 1d) < IntegerEpsilon)
            {
                end = sorted[index + 1];
                index++;
            }

            ranges.Add(Math.Abs(start - end) < IntegerEpsilon
                ? FormatNumber(start)
                : $"{FormatNumber(start)}-{FormatNumber(end)}");
            index++;
        }

        return string.Join(", ", ranges);
    }

    private static bool IsWholeNumber(double value)
    {
        return Math.Abs(value - Math.Round(value)) < IntegerEpsilon;
    }
}
