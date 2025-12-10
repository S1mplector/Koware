# Koware.Cli

Console-first entrypoint for Koware. Hosts commands for searching, streaming, reading, provider management, and configuration.

## Build & run

```bash
# Build
dotnet build Koware.Cli/Koware.Cli.csproj

# Run help
dotnet run --project Koware.Cli/Koware.Cli.csproj -- help
```

## Configuration
- Copies `appsettings.json` to the output; user overrides live in `~/.config/koware/appsettings.user.json` (macOS/Linux) or `%APPDATA%\koware\appsettings.user.json` (Windows).
- Providers can be auto-configured via `koware provider autoconfig`.
