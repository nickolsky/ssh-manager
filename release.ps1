# Builds the release package artifacts\SshManager-<version>-win-x64.zip (+ .sha256) from Directory.Build.props <Version>.
#   .\release.ps1            build the package only
#   .\release.ps1 -Publish   also create the GitHub release v<version> with gh (needs a clean, pushed main)
# The auto-updater looks for exactly these asset names, so keep them.
param([switch]$Publish, [string]$NotesFile)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

[xml]$props = Get-Content Directory.Build.props
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props' }
$name = "SshManager-$version-win-x64"
$out = Join-Path artifacts $name
$pkg = Join-Path $out 'SshManager'
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet publish src/SshManager -c Release -r win-x64 --self-contained false -o $pkg --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
dotnet publish src/sshm -c Release -r win-x64 --self-contained false -o $pkg --nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Copy-Item LICENSE, THIRD-PARTY-NOTICES.md, README.md -Destination $pkg
Get-ChildItem $pkg -Recurse -Include *.pdb, instance.txt | Remove-Item -Force

$zip = Join-Path artifacts "$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $pkg -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
Write-Host "`n$zip`nsha256 $hash" -ForegroundColor Green

if ($Publish) {
    if (git status --porcelain) { throw 'Commit your changes first' }
    $notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
    gh release create "v$version" $zip "$zip.sha256" --title "SSH Manager $version" @notes
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
