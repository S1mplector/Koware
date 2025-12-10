# Koware.Installer.Win

WPF installer for Windows that packages and installs the Koware CLI, player, reader, and browser payloads.

## Build

```bash
dotnet build Koware.Installer.Win/Koware.Installer.Win.csproj
```

## Publish payloads (Release)

```bash
dotnet publish Koware.Installer.Win/Koware.Installer.Win.csproj -c Release -r win-x64 --self-contained true
```

The `PublishPayload` target builds/publishes the CLI, player, reader, and browser, zips them, and embeds them as resources.

## Notes
- Assumes sibling projects (`Koware.Cli`, `Koware.Player*`, `Koware.Reader*`, `Koware.Browser`) are buildable.
- Uses `RuntimeIdentifier=win-x64` by default.
