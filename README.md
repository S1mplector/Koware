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

Configuration defaults live in `Koware.Cli/appsettings.json` (`AllAnime` provider settings and `Player` command/args`). Files are copied to the output directory so the host reads them at runtime. VLC is the default player (`vlc --play-and-exit --quiet`); mpv is used as a fallback. Set `Player:Command`/`Args` to override.

## Persistence
Watch history now lives in `%APPDATA%\koware\history.db` (SQLite). If you had an older `history.json`, it is imported on first run and renamed to `history.bak`.

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
- Watch history is stored in SQLite and is written even if the player exits with an error, so `koware last --play` can retry.

## Notes / next steps
- AllAnime endpoints are consumed with the same decode flow ani-cli uses; if the provider changes, update `AllAnimeCatalog`.
- Add retries/error handling around network calls and enrich stream selection (e.g., prefer dub if configured).
- Add persistence for cache/history and automated tests for search/episode parsing.
