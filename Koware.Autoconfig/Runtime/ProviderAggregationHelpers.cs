// Author: Ilgaz Mehmetoğlu
using System.Text;

namespace Koware.Autoconfig.Runtime;

internal static class ProviderAggregationHelpers
{
    public static string PrefixId(string providerSlug, string id)
    {
        if (string.IsNullOrWhiteSpace(providerSlug) || string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        return id.StartsWith(providerSlug + ":", StringComparison.OrdinalIgnoreCase)
            ? id
            : $"{providerSlug}:{id}";
    }

    public static string RemovePrefix(string providerSlug, string id)
    {
        if (string.IsNullOrWhiteSpace(providerSlug) || string.IsNullOrWhiteSpace(id))
        {
            return id;
        }

        var prefix = providerSlug + ":";
        return id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? id[prefix.Length..]
            : id;
    }

    public static bool TryExtractProviderSlug(string id, out string providerSlug)
    {
        providerSlug = string.Empty;

        var colonIndex = id.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var possibleSlug = id[..colonIndex];
        if (possibleSlug.All(char.IsDigit))
        {
            return false;
        }

        providerSlug = NormalizeSlug(possibleSlug);
        return true;
    }

    public static string NormalizeSlug(string value) =>
        value.Trim().ToLowerInvariant().Replace(' ', '-');

    public static string NormalizeTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : ' ');
        }

        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static int ScoreTitle(string query, string? title)
    {
        var normalizedQuery = NormalizeTitle(query);
        var normalizedTitle = NormalizeTitle(title);

        if (normalizedQuery.Length == 0 || normalizedTitle.Length == 0)
        {
            return 0;
        }

        if (normalizedTitle == normalizedQuery)
        {
            return 10_000;
        }

        var score = 0;
        if (normalizedTitle.StartsWith(normalizedQuery, StringComparison.Ordinal))
        {
            score += 5_000;
        }

        if (normalizedTitle.Contains(normalizedQuery, StringComparison.Ordinal))
        {
            score += 2_500;
        }

        var queryTokens = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var titleTokens = normalizedTitle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var token in queryTokens)
        {
            if (titleTokens.Contains(token))
            {
                score += 300;
            }
            else if (titleTokens.Any(t => t.StartsWith(token, StringComparison.Ordinal)))
            {
                score += 150;
            }
        }

        return score;
    }
}
