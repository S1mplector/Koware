[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

# Determine repo root (this script lives in the Scripts folder)
$repoRoot = Split-Path -Path $PSScriptRoot -Parent
$testProject = Join-Path $repoRoot "Koware.Tests\Koware.Tests.csproj"

if (-not (Test-Path $testProject)) {
    Write-Host "Test project not found: $testProject" -ForegroundColor Red
    exit 1
}

Write-Host "Running Koware tests (configuration: $Configuration)..." -ForegroundColor Cyan

Push-Location $repoRoot
try {
    dotnet test $testProject -c $Configuration --nologo
    $exitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($exitCode -eq 0) {
    Write-Host "All tests passed." -ForegroundColor Green
    exit 0
}
else {
    Write-Host "One or more tests failed. dotnet test exit code: $exitCode" -ForegroundColor Red
    exit $exitCode
}
