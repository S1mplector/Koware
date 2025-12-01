param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputDir = "./publish/installer",
    [switch]$SelfContained,
    [switch]$CopyZipToRepoRoot,
    [switch]$CopyZipToDesktop
)

if (-not $PSBoundParameters.ContainsKey('SelfContained')) {
    $SelfContained = $true
}

$ErrorActionPreference = 'Stop'

function Write-Info($msg) { Write-Host "[INFO] $msg" -ForegroundColor Cyan }
function Write-Warn($msg) { Write-Host "[WARN] $msg" -ForegroundColor Yellow }
function Write-Err($msg)  { Write-Host "[ERR ] $msg" -ForegroundColor Red }

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$installerProj = Join-Path $scriptRoot '..\Koware.Installer.Win\Koware.Installer.Win.csproj'
try { $installerProj = (Resolve-Path -LiteralPath $installerProj).Path } catch {}
if (-not (Test-Path $installerProj)) {
    Write-Err "Installer project not found at $installerProj"
    exit 1
}

$resolvedOut = Resolve-Path -LiteralPath (Join-Path $scriptRoot $OutputDir) -ErrorAction SilentlyContinue
if (-not $resolvedOut) {
    $resolvedOut = Join-Path $scriptRoot $OutputDir
}
if ($resolvedOut -is [System.Management.Automation.PathInfo]) { $resolvedOut = $resolvedOut.Path }

if (Test-Path $resolvedOut) {
    Write-Info "Cleaning $resolvedOut"
    Remove-Item -Recurse -Force -LiteralPath $resolvedOut
}
New-Item -ItemType Directory -Force -Path $resolvedOut | Out-Null

# Publish the installer (this also builds/zips the payloads via MSBuild target)
Write-Info "Publishing installer -> $resolvedOut"
$publishArgs = @(
    "publish", $installerProj,
    "-c", $Configuration,
    "-r", $Runtime,
    "-o", $resolvedOut,
    "/p:PublishSingleFile=true",
    "/p:IncludeNativeLibrariesForSelfExtract=true",
    "/p:EnableCompressionInSingleFile=true"
)
if ($SelfContained) { $publishArgs += @("--self-contained", "true") }
$logLine = "dotnet " + (($publishArgs | ForEach-Object { if ($_ -match '\\s') { '"' + $_ + '"' } else { $_ } }) -join ' ')
Write-Host $logLine -ForegroundColor DarkGray
& dotnet @publishArgs

# Locate payload zips created by the installer project target
$payloadDir = Join-Path $scriptRoot '..\Koware.Installer.Win\payload'
try { $payloadDir = (Resolve-Path -LiteralPath $payloadDir).Path } catch {}
$cliZip = Join-Path $payloadDir 'koware-cli.zip'
$playerZip = Join-Path $payloadDir 'koware-player.zip'
$readerZip = Join-Path $payloadDir 'koware-reader.zip'

if (Test-Path $cliZip) {
    Copy-Item -LiteralPath $cliZip -Destination $resolvedOut -Force
    Write-Info "Copied payload: $(Split-Path $cliZip -Leaf)"
} else {
    Write-Warn "CLI payload zip not found at $cliZip (build target may not have run)."
}

if (Test-Path $playerZip) {
    Copy-Item -LiteralPath $playerZip -Destination $resolvedOut -Force
    Write-Info "Copied payload: $(Split-Path $playerZip -Leaf)"
} else {
    Write-Warn "Player payload zip not found at $playerZip (build target may not have run or player disabled)."
}

if (Test-Path $readerZip) {
    Copy-Item -LiteralPath $readerZip -Destination $resolvedOut -Force
    Write-Info "Copied payload: $(Split-Path $readerZip -Leaf)"
} else {
    Write-Warn "Reader payload zip not found at $readerZip (build target may not have run or reader disabled)."
}

Write-Host ""; Write-Info "Publish complete. Output: $resolvedOut"
Get-ChildItem -LiteralPath $resolvedOut | Select-Object Name,Length,LastWriteTime | Format-Table -AutoSize

# Create a single zip bundle for easy distribution
$zipName = "koware-installer-win-x64.zip"
$zipPath = Join-Path $resolvedOut $zipName
if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Write-Info "Creating bundle: $zipPath"
Compress-Archive -Path (Join-Path $resolvedOut '*') -DestinationPath $zipPath -Force
Write-Info "Bundle ready: $zipPath"

$installerExeName = "Koware.Installer.Win.exe"
$installerExePath = Join-Path $resolvedOut $installerExeName
if (-not (Test-Path $installerExePath)) {
    Write-Err "Installer executable not found at $installerExePath"
    exit 1
}

if ($CopyZipToRepoRoot) {
    $repoRoot = Split-Path -Parent $scriptRoot
    $target = Join-Path $repoRoot "koware-installer-win-x64.exe"
    Copy-Item -LiteralPath $installerExePath -Destination $target -Force
    Write-Info "Copied installer to repo root: $target"
}

if ($CopyZipToDesktop) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    if (-not [string]::IsNullOrWhiteSpace($desktop)) {
        $target = Join-Path $desktop "koware-installer-win-x64.exe"
        Copy-Item -LiteralPath $installerExePath -Destination $target -Force
        Write-Info "Copied installer to Desktop: $target"
    } else {
        Write-Warn "Could not resolve Desktop folder; skipping Desktop copy."
    }
}
