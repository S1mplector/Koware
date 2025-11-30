<p align="left">
  <img src="Assets/Logo/logo.png" alt="Koware logo" height="80" />
</p>

# Koware

Koware is a Windows-native, console-first link/stream aggregator application that helps you **search for anime, pick an episode, and open streams in a player** from your terminal.

It has a text-based user interface but behaves like a regular CLI. You run a command, Koware finds shows and episodes, and then opens the selected stream in a video player.

---

## Features

- **Search for anime** by title.
- **Browse episodes** for a selected show.
- **Open streams** in:
  - The bundled Koware player (recommended for best compatibility), or
  - External players such as **VLC** or **mpv** if you prefer.
- **Keep watch history** locally in a small SQLite database.

Koware does **NOT** host any media. It only helps you discover and open links that are already publicly available.

---

## Getting started

### Requirements

- Windows 10 x64 or later.
- PowerShell.
- To run from source: **.NET 8 SDK**.

### Install globally from source

1. Clone this repository.
2. From the repo root, run:

```powershell
.# From repo root
 .\install-koware.ps1 -Publish   # builds, publishes, and adds Koware to your PATH
```

This installs Koware under `%LOCALAPPDATA%\koware` and adds a global `koware` command.

After installation, open a new PowerShell window and run, for example:

```powershell
koware search "bleach"
koware watch "haikyuu" --episode 1
```

### Install via the installer 

The installer package bundles everything needed to run Koware without needing to build from source. You can download the latest installer from the [releases page](https://github.com/S1mplector/Koware/releases).

### Run without installing globally

From the repo root you can also run Koware directly via `dotnet`:

```powershell
dotnet run --project .\Koware.Cli -- search "<query>"
```

Or use the helper script:

```powershell
cd .\Koware
 .\koware.ps1 -Command search -Query "fullmetal alchemist"
 .\koware.ps1 -Command stream -Query "haikyuu" -Episode 1 -Quality 720p
 .\koware.ps1 -Command play -Query "demon slayer" -Episode 1 -Quality 1080p
```

---

## Commands

All examples assume you have the global `koware` command installed. If not, replace `koware` with `dotnet run --project .\Koware.Cli --`.

- **`search`** – find anime by title and select an entry.
  - Example:

    ```powershell
    koware search "one piece"
    ```

- **`stream`** – choose a show, episode, and quality, and open the stream in your configured player.
  - Example:

    ```powershell
    koware stream "haikyuu" --episode 1 --quality 720p
    ```

- **`play`** – convenience command that searches and immediately opens a specific episode at a given quality.
  - Example:

    ```powershell
    koware play "demon slayer" --episode 1 --quality 1080p
    ```

- **`history`** – inspect and manage your local watch history.
  - Run:

    ```powershell
    koware help history
    ```

When multiple search results are found, Koware will prompt you to choose one. You can also pass `--index <n>` or `--non-interactive` to skip prompts (useful for scripting).

Stream selection prefers HLS/DASH and HTTPS hosts; noisy HTTP logging is filtered by default so you can focus on the important bits.

For more information about available commands and options, run:

```powershell
koware help
``` 
---

## Configuration

Koware reads its configuration from `Koware.Cli/appsettings.json`. This file is copied to the output directory at build time and read at runtime.

Key settings:

- **Player**
  - By default, Koware uses the **bundled Koware player** for best compatibility. The player is included in the installer package. It's a lightweight, cross-platform media player built for Koware, and it's designed to work seamlessly with Koware's streaming workflow.
  - You can switch to **VLC** or **mpv** (or any other player) by changing:
    - `Player:Command` – the path or command name for your player.
    - `Player:Args` – arguments that Koware should pass to the player.

    Or, alternatively by using the config command. For more info, do:
    `koware help config`

---

## Watch history

Koware keeps a local watch history in a small SQLite database:

- Path: `%APPDATA%\\koware\\history.db`

To learn more and see available history options, run:

```powershell
koware help history
```

## Usage & Legality

- Koware is a locally hosted and operated tool that aggregates and presents links to content that is already publicly available on the internet. Koware strictly does NOT host or distribute media, and it does NOT include or support bypassing paywalls or DRM.

- Using Koware itself as a local link/stream aggregator is not, by itself, intended to be an infringing or illegal activity. However, whether your actual usage is lawful depends entirely on what you access and how you access it, as well as on the laws and website terms that apply to you. Koware does not turn unauthorized or infringing content into authorized content.

- You are solely responsible for ensuring that your use of Koware complies with local laws, website terms of service, and third-party rights (for example, rights held by copyright owners and streaming platforms). Nothing in this project, its documentation, or its source code constitutes legal advice.

- See `Usage-Notice.md` for more details.
