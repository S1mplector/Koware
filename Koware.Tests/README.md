# Koware.Tests

Test suite for Koware projects. Contains unit/integration tests covering application logic, infrastructure behaviors, and CLI helpers where applicable.

## Run tests

```bash
dotnet test Koware.Tests/Koware.Tests.csproj
```

## Notes
- Ensure `appsettings.json` defaults are in place; some tests may use in-memory or test doubles for providers.
