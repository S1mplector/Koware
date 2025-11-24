param(
    [switch]$Publish
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $repoRoot "Koware.Cli"
$playerProject = Join-Path $repoRoot "Koware.Player.Win"
$installDir = Join-Path $env:LOCALAPPDATA "koware"

New-Item -ItemType Directory -Force -Path $installDir | Out-Null

if ($Publish) {
    Write-Host "Publishing Koware CLI to $installDir ..."
    dotnet publish $cliProject -c Release -o $installDir | Out-Host

    if (Test-Path $playerProject) {
        Write-Host "Publishing Koware Player (WebView2) to $installDir ..."
        dotnet publish $playerProject -c Release -o $installDir | Out-Host
    } else {
        Write-Warning "Player project not found at $playerProject; skipping player publish."
    }
} else {
    Write-Host "Skipping publish (use -Publish to prebuild). CLI will run via dotnet run if no exe is found."
}

# Always copy the shim so PATH can find it
Copy-Item -Path (Join-Path $repoRoot "koware.cmd") -Destination (Join-Path $installDir "koware.cmd") -Force

# Add installDir to PATH for current session
if (-not ($env:PATH.Split(';') -contains $installDir)) {
    $env:PATH = "$env:PATH;$installDir"
}

# Persist PATH for future sessions (idempotent)
$currentUserPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if (-not ($currentUserPath.Split(';') -contains $installDir)) {
    [Environment]::SetEnvironmentVariable("PATH", "$currentUserPath;$installDir", "User")
    Write-Host "Added $installDir to user PATH. Restart your shell to pick it up everywhere."
} else {
    Write-Host "$installDir already on PATH."
}

Write-Host "Done. You can now run 'koware <command>' from any new PowerShell window."
