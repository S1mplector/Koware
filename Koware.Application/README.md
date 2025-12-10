# Koware.Application

Core application layer containing abstractions, orchestration logic, and service interfaces shared across the CLI, reader, player, and installer. Keeps the domain types independent from infrastructure and UI concerns.

## Build

```bash
dotnet build Koware.Application/Koware.Application.csproj
```

## Notes
- References domain models from `Koware.Domain`.
- Consumed by CLI and desktop apps to keep behaviors consistent.
