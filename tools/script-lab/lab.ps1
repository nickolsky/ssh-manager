<#
.SYNOPSIS
  Test servers for SSH Manager install scripts: Ubuntu 24.04, Debian 12 and CentOS Stream 9
  (with -Extra also Ubuntu 22.04, Debian 13 and Rocky Linux 9),
  each with systemd, sshd and its own Docker.

.EXAMPLE
  .\lab.ps1 up                      # build and start the servers
  .\lab.ps1 test                    # all VPN / web / File Browser checks on every running server
  .\lab.ps1 test vless,hysteria2    # only these scripts
  .\lab.ps1 test -Heavy             # also Nextcloud and Seafile (several GB of images, 10+ minutes)
  .\lab.ps1 up -Extra               # add Ubuntu 22.04, Debian 13 and Rocky Linux 9
  .\lab.ps1 reset                   # throw the servers away and start clean ones
  .\lab.ps1 down                    # stop and remove everything, volumes included
#>
param(
    [Parameter(Position = 0)][ValidateSet('up', 'down', 'reset', 'status', 'test')][string]$Command = 'status',
    [Parameter(Position = 1)][string]$Only = '',
    [switch]$Heavy,
    [switch]$Extra
)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$repo = Resolve-Path "$PSScriptRoot\..\.."
$profileArgs = if ($Extra) { @('--profile', 'extra') } else { @() }
$servers = [ordered]@{ ubuntu = 2201; debian = 2202; centos = 2205; ubuntu22 = 2203; debian13 = 2204; rocky = 2206 }

function Running {
    $names = docker ps --format '{{.Names}}'
    $servers.Keys | Where-Object { $names -contains "sshm-lab-$_" }
}

function Show-Servers {
    Write-Host ''
    Write-Host 'Add them in SSH Manager (password login):' -ForegroundColor Cyan
    foreach ($name in Running) {
        Write-Host ("  lab-{0,-9} 127.0.0.1 port {1}   root / lab-root   (sudo user: lab / lab-user)" -f $name, $servers[$name])
    }
    Write-Host 'Ports on this PC: ubuntu 18001-18004, debian 18011-18014, centos 18021-18024 -> 8080, 8081, 8000, 80 inside (see compose.yml)'
}

switch ($Command) {
    'up' {
        docker compose @profileArgs up -d --build
        Show-Servers
    }
    'down' { docker compose --profile extra down -v }
    'reset' {
        docker compose --profile extra down -v
        docker compose @profileArgs up -d --build
        Show-Servers
    }
    'status' {
        docker compose --profile extra ps
        Show-Servers
    }
    'test' {
        $env:SSHM_LAB = (Running | ForEach-Object { "$_=127.0.0.1:$($servers[$_]):root:lab-root" }) -join ';'
        if (-not $env:SSHM_LAB) { throw 'No lab server is running: .\lab.ps1 up' }
        $env:SSHM_LAB_ONLY = $Only
        $env:SSHM_LAB_HEAVY = if ($Heavy) { '1' } else { '' }
        try {
            dotnet test "$repo\tests\SshManager.Tests" --filter 'FullyQualifiedName~LabTests' --logger 'console;verbosity=normal'
        }
        finally {
            Remove-Item Env:SSHM_LAB, Env:SSHM_LAB_ONLY, Env:SSHM_LAB_HEAVY -ErrorAction SilentlyContinue
        }
        Write-Host "Script output: $env:TEMP\sshm-lab\" -ForegroundColor Cyan
    }
}
