# Builds a test copy of SSH Manager into .\app-dev that runs next to the main one (.\app):
# own data folder (.\data-dev, a copy of .\data made on the first build), own pipes, no autostart, no auto backup.
#   .\publish-dev.ps1              build (keeps data-dev)
#   .\publish-dev.ps1 -ResetData   also replace data-dev with a fresh copy of data
param([switch]$ResetData)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$running = Get-Process SshManager -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$PSScriptRoot\app-dev\*" }
if ($running) {
    Write-Host 'The test copy (app-dev) is running - exit it from its tray menu first.' -ForegroundColor Yellow
    exit 1
}

dotnet publish src/SshManager -c Release -o app-dev --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish src/sshm -c Release -o app-dev --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Set-Content -Path app-dev\instance.txt -Value 'dev' -Encoding ascii

if ($ResetData -and (Test-Path data-dev)) { Remove-Item data-dev -Recurse -Force }
if (-not (Test-Path data-dev\vault.dat) -and (Test-Path data\vault.dat)) {
    New-Item -ItemType Directory -Force data-dev | Out-Null
    # everything except the old vault versions and backup snapshots
    Get-ChildItem data -Force | Where-Object { $_.Name -ne 'backups' } | Copy-Item -Destination data-dev -Recurse -Force
    # the test copy starts with the built-in terminal, which is what it is mostly for
    $settings = 'data-dev\settings.json'
    if (Test-Path $settings) {
        $json = Get-Content $settings -Raw | ConvertFrom-Json
        $json.Terminal = 'BuiltIn'
        $json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8
    }
    Write-Host 'data-dev: copied from data (same servers, keys and master password)' -ForegroundColor Cyan
}
Write-Host "`nDone: $PSScriptRoot\app-dev\SshManager.exe" -ForegroundColor Green
