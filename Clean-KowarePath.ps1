# backs up the current user PATH and removes older Koware installs from PATH
$pathBackup = [Environment]::GetEnvironmentVariable('PATH','User')
Set-Content -Path "$PSScriptRoot\path-backup.txt" -Value $pathBackup

$remove = @(
  'C:\Users\Ilgaz\AppData\Local\koware',
  'C:\Users\Ilgaz\Desktop\Koware',
  'C:\Koware-Test',
  'C:\Koware-Test2'
)

$paths = $pathBackup -split ';' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
$cleaned = $paths | Where-Object { $remove -notcontains $_.TrimEnd('\') }
$newPath = ($cleaned | Select-Object -Unique) -join ';'

[Environment]::SetEnvironmentVariable('PATH', $newPath, 'User')
Write-Host "Cleaned PATH set. New PATH:"
Write-Host $newPath
Write-Host "Backup saved to $PSScriptRoot\path-backup.txt"
