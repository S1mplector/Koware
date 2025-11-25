# Installer Plan

## Artifacts
- **GUI installer (default):** Publish `Koware.Installer.Win` (WPF). Distribute the generated `Koware.Installer.Win.exe` as the primary setup experience.
- **Portable zip (optional):** `dotnet publish -c Release -o dist` for `Koware.Cli` and `Koware.Player.Win`, bundle `koware.cmd`/`koware.ps1`, and include a README with usage.

## Installer build steps
1) `dotnet publish .\Koware.Installer.Win -c Release -o .\artifacts\installer`
2) Sign the installer binary (if signing cert is available)
3) Smoke-test on a fresh Windows VM: install to `%LOCALAPPDATA%\koware`, ensure `koware` resolves on PATH and player launches.

## Portable build steps
1) `dotnet publish .\Koware.Cli -c Release -o .\artifacts\portable`
2) `dotnet publish .\Koware.Player.Win -c Release -o .\artifacts\portable` (optional flag if user wants player)
3) Copy shims (`koware.cmd`, `koware.ps1`) and docs into `artifacts\portable`
4) Zip `artifacts\portable` for download.

## Release pipeline (proposed)
- GitHub Actions workflow to build on Windows, produce both installer exe and portable zip, sign binaries, generate SHA256 checksums, and draft a GitHub Release.
- Attach both artifacts and checksums; note prerequisites (.NET runtime) if needed.

## Notes
- GUI installer should default to publishing fresh Release builds, include the player, add install dir to PATH, and allow overrides (install path, skip player, no PATH).
- Keep the PowerShell installer (`install-koware.ps1`) for automation/CI and advanced users.
