# Koware.Browser

Avalonia-based desktop browser for the Koware ecosystem. Provides a GUI to browse catalogs and fetch streams using the shared application and infrastructure layers.

## Build & run

```bash
# Build
dotnet build Koware.Browser/Koware.Browser.csproj

# Run (dev)
dotnet run --project Koware.Browser/Koware.Browser.csproj
```

## Notes
- Uses the same provider/config composition as the CLI, including dynamic autoconfig providers.
- Shares models with `Koware.Domain` and orchestration from `Koware.Application`.
