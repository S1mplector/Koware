using System;

namespace Koware.Cli.Console;

internal static class TextColorer
{
    public static ConsoleColor ForMatchIndex(int zeroBasedIndex, int total)
    {
        if (total <= 0)
        {
            return ConsoleColor.Gray;
        }

        if (total == 1 || zeroBasedIndex <= 0)
        {
            return ConsoleColor.Yellow;
        }

        var t = (double)zeroBasedIndex / Math.Max(1, total - 1);

        if (t < 0.33)
        {
            return ConsoleColor.Yellow;
        }

        if (t < 0.66)
        {
            return ConsoleColor.DarkYellow;
        }

        return ConsoleColor.DarkGray;
    }
}
