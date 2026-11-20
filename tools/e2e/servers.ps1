<#
.SYNOPSIS
    Starts or stops the local test servers the fixture points at.

.DESCRIPTION
      SSH + SFTP echo server  127.0.0.1:2222  (demo / secret; any public key)
      FTP server              127.0.0.1:2121  (demo / secret)
      FTPS (explicit TLS)     127.0.0.1:2122  (demo / secret, self-signed)

        tools\e2e\servers.ps1 start [-Dir <fixture>] [-ExitAfter <secs>]
        tools\e2e\servers.ps1 stop
        tools\e2e\servers.ps1 status

    -ExitAfter makes shells log out on their own (automatic pane close).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('start', 'stop', 'status')]
    [string]$Action,
    [string]$Dir,
    [int]$ExitAfter = 0
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Core = Join-Path $Root 'core'
if (-not $Dir) { $Dir = Join-Path $Root 'build\e2e' }
$Bin = Join-Path $Core 'target\debug\examples'
$Ports = [ordered]@{ ssh = 2222; ftp = 2121; ftps = 2122 }

$extra = @(
    (Join-Path $env:USERPROFILE '.cargo\bin'),
    'C:\Program Files\LLVM\bin',
    'C:\Program Files\CMake\bin'
) | Where-Object { (Test-Path $_) -and (($env:PATH -split ';') -notcontains $_) }
if ($extra) { $env:PATH = ($extra -join ';') + ';' + $env:PATH }

function Test-Port([int]$port) {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect('127.0.0.1', $port, $null, $null)
        if (-not $pending.AsyncWaitHandle.WaitOne(500)) { return $false }
        $client.EndConnect($pending)
        return $true
    }
    catch { return $false }
    finally { $client.Close() }
}

function Get-TestServers {
    Get-Process -Name 'echo_server', 'ftp_server' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($Bin, [System.StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
}

function Stop-TestServers {
    $running = @(Get-TestServers)
    if ($running.Count -gt 0) {
        $running | Stop-Process -Force
        $running | ForEach-Object { try { $_.WaitForExit(3000) | Out-Null } catch { } }
    }
}

function Show-Status {
    $parts = foreach ($name in $Ports.Keys) {
        $state = if (Test-Port $Ports[$name]) { 'up' } else { 'DOWN' }
        "${name}: $state"
    }
    Write-Host ($parts -join '   ')
}

function Start-Server([string]$exe, [string[]]$arguments, [string]$log) {
    $path = Join-Path $Bin "$exe.exe"
    if (-not (Test-Path $path)) { return $false }
    $quoted = ($arguments | ForEach-Object { if ($_ -match '\s') { "`"$_`"" } else { $_ } }) -join ' '
    # Through the shell, with cmd doing the redirection: started any other
    # way the server inherits this script's output handles, and a caller
    # piping the script's output would wait until the server exits.
    $logFile = Join-Path $Dir "$log.log"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $env:ComSpec
    $psi.Arguments = "/d /c `"`"$path`" $quoted > `"$logFile`" 2>&1`""
    $psi.UseShellExecute = $true
    $psi.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    [System.Diagnostics.Process]::Start($psi) | Out-Null
    return $true
}

switch ($Action) {
    'start' {
        if (-not (Test-Path (Join-Path $Dir 'host_key'))) { throw "no fixture at $Dir; run tools\e2e\make-fixture.ps1 first" }
        Stop-TestServers
        Write-Host '==> building the test servers'
        Push-Location $Core
        try {
            cargo build -q --examples --features test-support
            if ($LASTEXITCODE -ne 0) { throw "cargo build failed (exit code $LASTEXITCODE)" }
        }
        finally { Pop-Location }

        $echoArgs = @('--key', (Join-Path $Dir 'host_key'), '--port', '2222')
        if ($ExitAfter -gt 0) { $echoArgs += @('--exit-after', "$ExitAfter") }
        $ftpRoot = Join-Path $Dir 'ftp-root'
        Start-Server 'echo_server' $echoArgs 'echo_server' | Out-Null
        Start-Server 'ftp_server' @('--port', '2121', '--root', $ftpRoot) 'ftp_server' | Out-Null
        Start-Server 'ftp_server' @('--port', '2122', '--root', $ftpRoot, '--tls') 'ftps_server' | Out-Null

        $wanted = @(2222, 2121, 2122)
        for ($i = 0; $i -lt 60; $i++) {
            if (@($wanted | Where-Object { -not (Test-Port $_) }).Count -eq 0) { break }
            Start-Sleep -Milliseconds 500
        }
        Show-Status
    }
    'stop' {
        Stop-TestServers
        Write-Host 'test servers stopped'
    }
    'status' {
        Show-Status
    }
}
