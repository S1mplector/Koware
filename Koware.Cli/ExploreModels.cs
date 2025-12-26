// Author: Ilgaz Mehmetoğlu
// Explore command supporting types.
using System;
using System.Collections.Generic;
using Koware.Application.Abstractions;
using Koware.Autoconfig.Models;
using Koware.Cli.Console;

namespace Koware.Cli;

internal enum ExploreView
{
    Search,
    Popular
}

internal sealed record ExploreProviderChoice(
    string Name,
    string Slug,
    ProviderType Type,
    bool IsBuiltIn,
    bool IsActive,
    string Host);

internal sealed class ExploreProviderContext
{
    public ExploreProviderChoice Info { get; init; } = default!;
    public IAnimeCatalog? AnimeCatalog { get; init; }
    public IMangaCatalog? MangaCatalog { get; init; }
    public string? Referrer { get; init; }
    public string? UserAgent { get; init; }
}

internal sealed class ListStatusLookup
{
    private readonly Dictionary<string, ItemStatus> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ItemStatus> _byTitle = new(StringComparer.OrdinalIgnoreCase);

    public ItemStatus Resolve(string id, string title)
    {
        if (!string.IsNullOrWhiteSpace(id) && _byId.TryGetValue(id, out var status))
        {
            return status;
        }

        if (!string.IsNullOrWhiteSpace(title) && _byTitle.TryGetValue(title, out status))
        {
            return status;
        }

        return ItemStatus.None;
    }

    public void Set(string id, string title, ItemStatus status)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            _byId[id] = status;
        }

        if (!string.IsNullOrWhiteSpace(title))
        {
            _byTitle[title] = status;
        }
    }
}

internal sealed record ExploreMenuItem(string Id, string Label, string Description);
internal sealed record ExploreActionItem(string Id, string Label, string Description);
