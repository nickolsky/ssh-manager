# Builds SSH Manager into .\app (framework-dependent, needs .NET 10 Desktop Runtime).
# Data stays in .\data next to this script, so rebuilding never touches your vault.
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$running = Get-Process SshManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot\app\*" }
if ($running) {
    Write-Host 'SSH Manager from .\app is running - exit it from the tray menu first.' -ForegroundColor Yellow
    exit 1
}

dotnet publish src/SshManager -c Release -o app --nologo
dotnet publish src/sshm -c Release -o app --nologo
Write-Host "`nDone: $PSScriptRoot\app\SshManager.exe" -ForegroundColor Green
