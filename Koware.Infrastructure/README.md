# Koware.Infrastructure

Infrastructure layer with scraping/catalog implementations (e.g., AllAnime, AllManga, GogoAnime), configuration bindings, and DI helpers.

## Build

```bash
dotnet build Koware.Infrastructure/Koware.Infrastructure.csproj
```

## Notes
- Depends on `Koware.Application` abstractions and `Koware.Domain` models.
- HTTP clients are configured via `AllAnime`, `AllManga`, and `GogoAnime` sections in `appsettings.user.json`.
- DI extension: `AddInfrastructure` registers catalog services for consumers.
