# Koware.Player.Win

Windows-specific build of the Koware media player. Used by the installer to provide a bundled player executable for Windows users.

## Build

```bash
dotnet build Koware.Player.Win/Koware.Player.Win.csproj
```

## Notes
- Runtime identifier is typically `win-x64`.
- Included in the Windows installer payload via `Koware.Installer.Win`.
