# Author: Ilgaz Mehmetoğlu
# Summary: PowerShell helper to run the Koware CLI with convenience parameters.
param(
    [string]$Command = "search",
    [Parameter(Mandatory = $true)][string]$Query,
    [int]$Episode,
    [string]$Quality
)

$projectPath = Join-Path $PSScriptRoot "Koware.Cli"
$argsList = @("--", $Command, $Query)

if ($Episode) {
    $argsList += "--episode"
    $argsList += $Episode
}

if ($Quality) {
    $argsList += "--quality"
    $argsList += $Quality
}

dotnet run --project $projectPath -- $argsList
