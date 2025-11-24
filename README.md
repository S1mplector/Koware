# Koware (scaffold)

Layered C# anime scraper scaffold inspired by `ani-cli`. Everything lives in this solution so we can iterate from PowerShell or `dotnet` easily on Windows.

## Layout
- `Koware.Domain` – core models (`Anime`, `Episode`, `StreamLink`).
- `Koware.Application` – use-cases and orchestrator (`ScrapeOrchestrator`) plus abstractions (`IAnimeCatalog`).
- `Koware.Infrastructure` – stubbed `AniCliCatalog` implementation and options binding.
- `Koware.Cli` – console front-end with simple commands and config loading.
- `koware.ps1` – helper to run the CLI from PowerShell.

## Running
```powershell
cd .\Koware
.\koware.ps1 -Command search -Query "fullmetal alchemist"
# or stream/plan with episode + quality preference
.\koware.ps1 -Command stream -Query "haikyuu" -Episode 1 -Quality 720p
```
You can also run directly with `dotnet run --project .\Koware.Cli -- search "<query>"`.

Configuration defaults live in `Koware.Cli/appsettings.json` (base URL, user agent, sample episode count). Files are copied to the output directory so the host reads them at runtime.

## Next steps
- Swap the stubbed `AniCliCatalog` for a real scraper (HTML/JSON fetch via `HttpClient`, parsing, error handling).
- Add persistence for cache/history if needed.
- Expand CLI parsing or wrap with richer PowerShell functions as more commands land.
