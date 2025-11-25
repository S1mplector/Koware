// Author: Ilgaz Mehmetoğlu
// Tracks enabled/disabled providers.
using System.Collections.Generic;

namespace Koware.Infrastructure.Configuration;

public sealed class ProviderToggleOptions
{
    public HashSet<string> DisabledProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string providerName) =>
        !DisabledProviders.Contains(providerName);
}
