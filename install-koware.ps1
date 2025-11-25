# Author: Ilgaz Mehmetoğlu
# PowerShell installer/publisher for Koware CLI and player, with safety checks and PATH setup.
[CmdletBinding()]
param(
    [switch]$Publish,
    [string]$InstallDir,
    [switch]$SkipPlayer,
    [switch]$NoPath,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

function Write-Info($message) { Write-Host "[INFO] $message" -ForegroundColor Cyan }
function Write-Warn($message) { Write-Warning $message }
function Write-Err($message) { Write-Host "[ERROR] $message" -ForegroundColor Red }

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$cliProject = Join-Path $repoRoot "Koware.Cli"
$playerProject = Join-Path $repoRoot "Koware.Player.Win"

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA "koware"
}

function Ensure-DotNet {
    try {
        $null = dotnet --info
    } catch {
        Write-Err "dotnet SDK/Runtime not found in PATH. Please install .NET 8+ and retry."
        exit 1
    }
}

function Ensure-Directory {
    param(
        [string]$Path,
        [switch]$Clean
    )

    if ($Clean -and (Test-Path $Path)) {
        Write-Info "Cleaning $Path ..."
        Remove-Item -Recurse -Force -Path $Path
    }

    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Publish-Project {
    param(
        [string]$ProjectPath,
        [string]$OutputPath,
        [string]$Name
    )

    Write-Info "Publishing $Name to $OutputPath ..."
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = "dotnet"
    $psi.Arguments = "publish `"$ProjectPath`" -c Release -o `"$OutputPath`""
    $psi.RedirectStandardError = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute = $false
    $process = [System.Diagnostics.Process]::Start($psi)
    $stdout = $process.StandardOutput.ReadToEnd()
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()

    if ($process.ExitCode -ne 0) {
        Write-Err "dotnet publish failed for ${Name}:`n$stderr"
        exit $process.ExitCode
    }

    if ($stdout) { Write-Host $stdout }
}

function Copy-LatestBuild {
    param(
        [string]$ProjectPath,
        [string]$OutputPath,
        [string]$Name
    )

    $bin = Join-Path $ProjectPath "bin"
    if (-not (Test-Path $bin)) {
        Write-Err "No bin folder found for $Name at $bin. Build or publish first."
        exit 1
    }

    $latest = Get-ChildItem -Path $bin -Recurse -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
    if (-not $latest) {
        Write-Err "No build artifacts found under $bin."
        exit 1
    }

    Write-Info "Copying latest build for $Name from $($latest.FullName) to $OutputPath"
    Copy-Item -Path (Join-Path $latest.FullName "*") -Destination $OutputPath -Recurse -Force
}

Ensure-Directory -Path $InstallDir -Clean:$Clean

if ($Publish) {
    Ensure-DotNet
    Publish-Project -ProjectPath $cliProject -OutputPath $InstallDir -Name "Koware CLI"

    if (-not $SkipPlayer -and (Test-Path $playerProject)) {
        Publish-Project -ProjectPath $playerProject -OutputPath $InstallDir -Name "Koware Player"
    } elseif ($SkipPlayer) {
        Write-Info "Skipping player publish."
    } else {
        Write-Warn "Player project not found at $playerProject; skipping player publish."
    }
} else {
    Write-Info "Publish flag not set. Copying latest build artifacts instead."
    Copy-LatestBuild -ProjectPath $cliProject -OutputPath $InstallDir -Name "Koware CLI"

    if (-not $SkipPlayer -and (Test-Path $playerProject)) {
        Copy-LatestBuild -ProjectPath $playerProject -OutputPath $InstallDir -Name "Koware Player"
    } elseif ($SkipPlayer) {
        Write-Info "Skipping player copy."
    } else {
        Write-Warn "Player project not found at $playerProject; skipping player copy."
    }
}

# Always copy the shim so PATH can find it
$shimSource = Join-Path $repoRoot "koware.cmd"
if (Test-Path $shimSource) {
    Copy-Item -Path $shimSource -Destination (Join-Path $InstallDir "koware.cmd") -Force
}

if (-not $NoPath) {
    if (-not ($env:PATH.Split(';') -contains $InstallDir)) {
        $env:PATH = "$env:PATH;$InstallDir"
    }

    $currentUserPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    if (-not ($currentUserPath.Split(';') -contains $InstallDir)) {
        [Environment]::SetEnvironmentVariable("PATH", "$currentUserPath;$InstallDir", "User")
        Write-Info "Added $InstallDir to user PATH. Restart your shell to pick it up everywhere."
    } else {
        Write-Info "$InstallDir already present in user PATH."
    }
} else {
    Write-Info "Skipping PATH update per -NoPath."
}

Write-Info "Done. Binaries are in $InstallDir. You can now run 'koware <command>' from a new shell."
