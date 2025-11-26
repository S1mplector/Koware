<!-- Author: Ilgaz Mehmetoglu | Audience: Koware customers receiving the beta installer package. -->

# Welcome to Koware (Beta)

Koware is a CLI-based anime discovery and playback tool with a custom Windows player. This package contains the beta installer and all required files—no extra downloads needed. You can install, play, and update directly from this bundle.

## What’s inside the folder you received
- `Koware.Installer.Win.exe` — the installer you run.
- `Koware.Installer.Win.dll` — bundled payload (Koware CLI + custom player).
- `Koware.Installer.Win.runtimeconfig.json` and `Koware.Installer.Win.deps.json` — runtime files for the installer.
- `Microsoft.Windows.SDK.NET.dll`, `WinRT.Runtime.dll` — Windows desktop runtime support.

## What the installer does
- Unpacks the embedded Koware CLI and custom Koware player.
- Adds the install directory to your user PATH so you can run `koware` from any terminal.
- Cleans/overwrites the target directory to ensure a fresh install.
- Requires no internet access during install (everything is pre-bundled).

## How to install
1) Extract the zip you received into a folder (e.g., `C:\KowareInstaller`).
2) Open the folder and double-click `Koware.Installer.Win.exe`.
3) Choose or confirm the install directory, then click **Install**.
4) When finished, open a terminal and run `koware --help` to verify it’s on PATH.

## Using Koware
- Run `koware watch "<title>" --episode <n>` to search, plan, and play.
- Run `koware doctor` if you want to verify connectivity.
- The custom player is included; no external player setup is required.

## Version
- This build is `0.1.0-beta`. Expect changes; feedback is welcome.

## License and terms
- Copyright © Ilgaz Mehmetoglu.
- Koware is provided under the license included in this package. Use at your own risk; no warranties are provided. Ensure compliance with local laws and third-party terms.
