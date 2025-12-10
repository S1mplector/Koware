# Koware.Reader.Win

Windows-specific wrapper/build for the Koware reader. Packaged with the Windows installer to deliver a ready-to-run reader experience.

## Build

```bash
dotnet build Koware.Reader.Win/Koware.Reader.Win.csproj
```

## Notes
- Targets `win-x64` in release packaging.
- Included in installer payload via `Koware.Installer.Win`.
