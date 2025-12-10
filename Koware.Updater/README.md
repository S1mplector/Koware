# Koware.Updater

Self-update component for Koware installers/clients. Handles fetching update manifests and applying new payloads.

## Build

```bash
dotnet build Koware.Updater/Koware.Updater.csproj
```

## Notes
- Used by the Windows installer/updater flow.
- Consumes network manifests; keep URLs/config in sync with installer settings.
