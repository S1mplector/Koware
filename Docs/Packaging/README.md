# Koware Packaging Guide

This file provides detailed instructions for packaging Koware for distribution on Windows and macOS.

## Table of Contents

- [Prerequisites](#prerequisites)
- [Project Structure](#project-structure)
- [Windows Packaging](#windows-packaging)
- [macOS Packaging](#macos-packaging)
- [Cross-Platform Components](#cross-platform-components)

---

## Prerequisites

### All Platforms

```bash
# Required
.NET 8 SDK or later

# Verify installation
dotnet --version
```

### Windows

- PowerShell 5.1+ (included with Windows 10+)
- Visual Studio 2022 (optional, for WPF projects)

### macOS

- Xcode Command Line Tools
- Homebrew (recommended)

```bash
# Install Xcode CLI tools
xcode-select --install

# Install create-dmg (for DMG creation)
brew install create-dmg
```

```bash
# Build and copy the macOS installer (for convenience, not meant for production)
cd ~/Desktop/Files/Projects/Koware && ./Scripts/build-installer-app.sh && cp ./publish/Koware-Installer-0.7.0-beta-osx-arm64.dmg ~/Desktop/
```
---

## Project Structure

```
Koware/
├── Koware.Cli/              # Main CLI application
├── Koware.Player/           # Cross-platform video player (Avalonia)
├── Koware.Reader/           # Cross-platform manga reader (Avalonia)
├── Koware.Player.Win/       # Windows-only video player (WPF)
├── Koware.Reader.Win/       # Windows-only manga reader (WPF)
├── Koware.Installer.Win/    # Windows installer GUI
├── Scripts/
│   ├── build-installer-app.sh   # macOS Installer.app builder
│   ├── build-pkg.sh             # macOS .pkg builder
│   ├── create-dmg.sh            # macOS DMG creator
│   ├── publish-macos.sh         # macOS publish script
│   ├── publish-installer.ps1    # Windows installer builder
│   └── install-koware.ps1       # Windows local installation
└── Assets/
    └── Logo/
        └── logo.png             # App icon source
```

---

## Windows Packaging

### Option 1: Quick Local Install (Development)

```powershell
# From repo root
.\Scripts\install-koware.ps1 -Publish

# This will:
# 1. Build Koware.Cli
# 2. Build Koware.Player.Win and Koware.Reader.Win
# 3. Install to %LOCALAPPDATA%\koware
# 4. Add to PATH
```

### Option 2: Build Windows Installer

```powershell
# Step 1: Build all components
dotnet publish Koware.Cli -c Release -r win-x64 --self-contained -o ./publish/cli
dotnet publish Koware.Player.Win -c Release -r win-x64 --self-contained -o ./publish/player
dotnet publish Koware.Reader.Win -c Release -r win-x64 --self-contained -o ./publish/reader
dotnet publish Koware.Installer.Win -c Release -r win-x64 --self-contained -o ./publish/installer

# Step 2: Or use the script
.\Scripts\publish-installer.ps1
```

### Option 3: Build Cross-Platform Player/Reader for Windows

```powershell
# Build cross-platform versions
dotnet publish Koware.Player -c Release -r win-x64 --self-contained -o ./publish/player-xplat
dotnet publish Koware.Reader -c Release -r win-x64 --self-contained -o ./publish/reader-xplat
```

### Windows Installer Contents

The installer package includes:

| Component | Location |
|-----------|----------|
| Koware CLI | `koware.exe` |
| Video Player | `Koware.Player.Win.exe` |
| Manga Reader | `Koware.Reader.Win.exe` |
| Config template | `appsettings.json` |

---

## macOS Packaging

### Option 1: Quick Build for Testing

```bash
# Build CLI only
dotnet publish Koware.Cli -c Release -r osx-arm64 --self-contained -o ./publish/macos

# Build with cross-platform player and reader
dotnet publish Koware.Cli -c Release -r osx-arm64 --self-contained -o ./publish/macos
dotnet publish Koware.Player -c Release -r osx-arm64 --self-contained -o ./publish/macos/player
dotnet publish Koware.Reader -c Release -r osx-arm64 --self-contained -o ./publish/macos/reader
```

### Option 2: Build Installer.app (Recommended)

```bash
# From repo root
chmod +x Scripts/build-installer-app.sh
./Scripts/build-installer-app.sh

# This creates:
# - dist/Koware-Installer.app (GUI installer)
# - dist/Koware-Installer.dmg (distributable disk image)
```

### Option 3: Build .pkg Installer

```bash
# From repo root
chmod +x Scripts/build-pkg.sh
./Scripts/build-pkg.sh

# This creates:
# - dist/Koware-<version>.pkg
```

### Option 4: Create DMG Only

```bash
# After building the app
chmod +x Scripts/create-dmg.sh
./Scripts/create-dmg.sh ./dist/Koware-Installer.app ./dist/Koware-Installer.dmg

# Or with custom volume name
./Scripts/create-dmg.sh ./dist/Koware-Installer.app ./dist/Koware.dmg "Koware Installer"
```

### macOS Package Contents

| Component | Location |
|-----------|----------|
| Koware CLI | `/usr/local/bin/koware` |
| Config directory | `~/.config/koware/` |
| Player | External (IINA/mpv/VLC) or Koware.Player |
| Reader | Browser-based or Koware.Reader |

### Intel vs Apple Silicon

```bash
# Apple Silicon (M1/M2/M3)
dotnet publish -r osx-arm64 --self-contained

# Intel Macs
dotnet publish -r osx-x64 --self-contained

# Universal binary (both architectures)
# Build both and use lipo to combine:
lipo -create ./arm64/koware ./x64/koware -output ./universal/koware
```

---

## Cross-Platform Components

### Building Koware.Player (Avalonia)

```bash
# macOS (Apple Silicon)
dotnet publish Koware.Player -c Release -r osx-arm64 --self-contained -o ./publish/player

# macOS (Intel)
dotnet publish Koware.Player -c Release -r osx-x64 --self-contained -o ./publish/player

# Windows
dotnet publish Koware.Player -c Release -r win-x64 --self-contained -o ./publish/player

# Linux
dotnet publish Koware.Player -c Release -r linux-x64 --self-contained -o ./publish/player
```

### Building Koware.Reader (Avalonia)

```bash
# macOS (Apple Silicon)
dotnet publish Koware.Reader -c Release -r osx-arm64 --self-contained -o ./publish/reader

# macOS (Intel)
dotnet publish Koware.Reader -c Release -r osx-x64 --self-contained -o ./publish/reader

# Windows
dotnet publish Koware.Reader -c Release -r win-x64 --self-contained -o ./publish/reader

# Linux
dotnet publish Koware.Reader -c Release -r linux-x64 --self-contained -o ./publish/reader
```

### Running with Cross-Platform Components

```bash
# Configure Koware to use the cross-platform player
koware config set Player:Command "./player/Koware.Player"

# Configure Koware to use the cross-platform reader
koware config set Reader:Command "./reader/Koware.Reader"
```

---

## Complete Build Scripts

### Windows: Full Release Build

```powershell
# Full Windows release build
$ErrorActionPreference = "Stop"

# Clean
Remove-Item -Recurse -Force ./publish -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path ./publish

# Build all components
dotnet publish Koware.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish/koware
dotnet publish Koware.Player -c Release -r win-x64 --self-contained -o ./publish/koware/player
dotnet publish Koware.Reader -c Release -r win-x64 --self-contained -o ./publish/koware/reader
dotnet publish Koware.Installer.Win -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o ./publish

Write-Host "Build complete! Installer at ./publish/Koware.Installer.Win.exe"
```

### macOS: Full Release Build

```bash
#!/bin/bash
set -e

# Clean
rm -rf ./publish
mkdir -p ./publish/koware

# Build CLI
dotnet publish Koware.Cli -c Release -r osx-arm64 --self-contained -p:PublishSingleFile=true -o ./publish/koware

# Build cross-platform player and reader
dotnet publish Koware.Player -c Release -r osx-arm64 --self-contained -o ./publish/koware/player
dotnet publish Koware.Reader -c Release -r osx-arm64 --self-contained -o ./publish/koware/reader

# Build installer
./Scripts/build-installer-app.sh

echo "Build complete! DMG at ./dist/Koware-Installer.dmg"
```

---

## Troubleshooting

### Windows

| Issue | Solution |
|-------|----------|
| "not recognized as cmdlet" | Restart PowerShell after PATH update |
| WPF build fails on non-Windows | Use cross-platform Avalonia version |
| Missing WebView2 | Install Edge WebView2 Runtime |

### macOS

| Issue | Solution |
|-------|----------|
| "Permission denied" | Run `chmod +x Scripts/*.sh` |
| Gatekeeper blocks app | Run `xattr -cr ./dist/Koware-Installer.app` |
| LibVLC not found | Install VLC or use `brew install libvlc` |
| create-dmg not found | Run `brew install create-dmg` |

---

## Version Bumping

Before release, update version in:

1. `Koware.Cli/Koware.Cli.csproj`
2. `Koware.Player/Koware.Player.csproj`
3. `Koware.Reader/Koware.Reader.csproj`
4. `Koware.Installer.Win/Koware.Installer.Win.csproj`

```bash
# Find all version references
grep -r "0.6.0-beta" --include="*.csproj"
```
