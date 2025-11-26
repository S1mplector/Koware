<!-- Author: Ilgaz Mehmetoğlu | Project overview and usage instructions for the Koware CLI/player. -->
# Koware

Layered C# Windows native anime scraper TUI application.

## Layout
- `Koware.Domain` – core models (`Anime`, `Episode`, `StreamLink`).
- `Koware.Application` – use-cases and orchestrator (`ScrapeOrchestrator`) plus abstractions (`IAnimeCatalog`).
- `Koware.Infrastructure` – AllAnime implementation (search/episodes/streams) and options binding.
- `Koware.Cli` – console front-end with `search`, `stream`, and `play` commands, config loading.
- `koware.ps1` – helper to run the CLI from PowerShell.

## Running
```powershell
cd .\Koware
.\koware.ps1 -Command search -Query "fullmetal alchemist"
.\koware.ps1 -Command stream -Query "haikyuu" -Episode 1 -Quality 720p
.\koware.ps1 -Command play -Query "demon slayer" -Episode 1 -Quality 1080p
```
You can also run directly with `dotnet run --project .\Koware.Cli -- search "<query>"`.

Configuration defaults live in `Koware.Cli/appsettings.json` Files are copied to the output directory so the host reads them at runtime. The bundled player is the default player, Koware supports VLC and MPV as well if you prefer those. Although for maxium compatibility the bundled player that has been built for Koware is recommended. Set `Player:Command`/`Args` to override.

## Persistence
Watch history lives in `%APPDATA%\koware\history.db` (SQLite).
Type 
```
powershell
koware help history
```

To learn more about the history functionality. 
## Global command
`koware.cmd` (root) is a tiny shim so you can invoke `koware` from anywhere. Run the helper script once to install it onto your PATH (optionally publishing a Release build for speed):
```powershell
# From repo root
.\install-koware.ps1 -Publish   # publish to %LOCALAPPDATA%\koware and add to PATH
# or skip -Publish to keep running via dotnet run when no exe is found
```
Then in a new PowerShell session, `koware search "bleach"` or `koware watch "haikyuu" --episode 1` will work from any directory.

## CLI selection and history
- When multiple search results are found, the CLI will prompt you to pick an index (or pass `--index <n>` / `--non-interactive` to skip prompts).
- Stream selection prefers HLS/DASH and HTTPS hosts; noisy HTTP logging is filtered by default.

## Usage & Legality

- Koware is a locally-hosted and operated tool that aggregates and presents links to content that is already publicly available on the internet. Koware strictly does NOT host or distribute media, and it does NOT include or support bypassing paywalls or DRM.

- Your use must comply with local laws, website terms of service, and third-party rights. Whether your usage is lawful depends on what you access and how you access it.
- See Usage-Notice.md for more details.
