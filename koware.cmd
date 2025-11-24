@echo off
setlocal

set "KOWARE_ROOT=%~dp0"
set "PROJECT=%KOWARE_ROOT%Koware.Cli"

if exist "%PROJECT%\\bin\\Release\\net10.0\\publish\\Koware.Cli.exe" (
    "%PROJECT%\\bin\\Release\\net10.0\\publish\\Koware.Cli.exe" %*
) else (
    dotnet run --project "%PROJECT%" -- %*
)

exit /b %errorlevel%
