@echo off
REM Author: Ilgaz Mehmetoğlu | Windows cmd shim to invoke the Koware CLI or published exe.
setlocal

set "SCRIPT_DIR=%~dp0"
set "PUBLISHED_EXE=%SCRIPT_DIR%Koware.Cli.exe"
set "PROJECT_DIR=%SCRIPT_DIR%Koware.Cli"

if exist "%PUBLISHED_EXE%" (
    "%PUBLISHED_EXE%" %*
) else if exist "%PROJECT_DIR%" (
    dotnet run --project "%PROJECT_DIR%" -- %*
) else (
    echo Could not find Koware binaries. Expected "%PUBLISHED_EXE%" or project at "%PROJECT_DIR%".
    exit /b 1
)

exit /b %errorlevel%
