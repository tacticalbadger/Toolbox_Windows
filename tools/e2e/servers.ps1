<#
.SYNOPSIS
    Starts or stops the local test servers the fixture points at.

.DESCRIPTION
      SSH + SFTP echo server  127.0.0.1:2222  (demo / secret; any public key)
      FTP server              127.0.0.1:2121  (demo / secret)
      FTPS (explicit TLS)     127.0.0.1:2122  (demo / secret, self-signed)
      RDP                     127.0.0.1:3390  (demo / secret, self-signed) plain
                                              graphics, honours resize requests
      RDP, graphics pipeline  127.0.0.1:3391  the same, offering H.264

        tools\e2e\servers.ps1 start [-Dir <fixture>] [-ExitAfter <secs>] [-RdpArgs '<options>']
        tools\e2e\servers.ps1 stop
        tools\e2e\servers.ps1 status

    -ExitAfter makes shells log out on their own (automatic pane close).

    The RDP servers start only when tools\e2e\make-rdp-server.ps1 has built
    them. Both paste whatever the app announces on its clipboard and log its
    size, never its contents (rdp_server.log, rdp_gfx_server.log). -RdpArgs
    replaces the options of the one on 3390, for example
    '--copy-text hello' to have it copy that text on its side instead.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('start', 'stop', 'status')]
    [string]$Action,
    [string]$Dir,
    [int]$ExitAfter = 0,
    [string]$RdpArgs = '--paste'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$Core = Join-Path $Root 'core'
if (-not $Dir) { $Dir = Join-Path $Root 'build\e2e' }
$Bin = Join-Path $Core 'target\debug\examples'
$RdpBin = Join-Path $Root 'build\e2e-rdp\target\release'
$HasRdp = Test-Path (Join-Path $RdpBin 'rdpsrv.exe')
$Ports = [ordered]@{ ssh = 2222; ftp = 2121; ftps = 2122 }
if ($HasRdp) { $Ports['rdp'] = 3390; $Ports['rdp-gfx'] = 3391 }

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
    Get-Process -Name 'echo_server', 'ftp_server', 'rdpsrv' -ErrorAction SilentlyContinue | Where-Object {
        try {
            $_.Path -and ($_.Path.StartsWith($Bin, [System.StringComparison]::OrdinalIgnoreCase) -or
                          $_.Path.StartsWith($RdpBin, [System.StringComparison]::OrdinalIgnoreCase))
        }
        catch { $false }
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
    if (-not $HasRdp) { $parts += 'rdp: not built (tools\e2e\make-rdp-server.ps1)' }
    Write-Host ($parts -join '   ')
}

function Start-Server([string]$path, [string[]]$arguments, [string]$log) {
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
        Start-Server (Join-Path $Bin 'echo_server.exe') $echoArgs 'echo_server' | Out-Null
        Start-Server (Join-Path $Bin 'ftp_server.exe') @('--port', '2121', '--root', $ftpRoot) 'ftp_server' | Out-Null
        Start-Server (Join-Path $Bin 'ftp_server.exe') @('--port', '2122', '--root', $ftpRoot, '--tls') 'ftps_server' | Out-Null
        if ($HasRdp) {
            $rdp = Join-Path $RdpBin 'rdpsrv.exe'
            $plain = @('--port', '3390') + @($RdpArgs -split '\s+' | Where-Object { $_ })
            Start-Server $rdp $plain 'rdp_server' | Out-Null
            Start-Server $rdp @('--port', '3391', '--graphics-pipeline', '--paste') 'rdp_gfx_server' | Out-Null
        }

        $wanted = @($Ports.Values)
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
