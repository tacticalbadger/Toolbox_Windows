<#
.SYNOPSIS
    Relaunches the built app against the fixture.

.DESCRIPTION
        tools\e2e\launch.ps1 [-Fixture <dir>] [-Wait <secs>] [-Exe <path>] [app arguments...]
        tools\e2e\launch.ps1 --connect echo --connect echo-1p
        tools\e2e\launch.ps1 -Exe build\release\Toolbox-0.1.0-win-x64\Toolbox.exe --connect echo

    The run uses the fixture's store, vault, vault key, settings (owner check
    off), known_hosts, log, WebView2 profile and updates folder; the real app
    data is not touched. A previous instance started from the same build is
    closed first. -Exe runs another build than build\app, such as a packaged
    one.

    App arguments: --connect <id>, --files <id>, --local-terminal (all
    repeatable), --local-terminal-profile <name>, --expand-all, --demo-move,
    --demo-merge, --demo-select-tab <n>, --demo-close, --demo-split,
    --demo-find <text>, --edit-connection <id|new>,
    --edit-credential <id|new>, --edit-provider <id|new>,
    --set-folder-credential <path>, --demo-credential-search <text>,
    --demo-provider-account <provider-id>,
    --demo-provider-rename <provider-id> <name>,
    --demo-folder-rename <path> <new path>, --unlock-vault,
    --assume-keyboard, --rdp-expose-control, and the update rehearsal's
    --updater-feed <folder|github>, --updater-key <public key>,
    --updater-pretend-build <n>, --demo-update, --demo-update-install.
#>
[CmdletBinding()]
param(
    [string]$Fixture,
    [int]$Wait = 3,
    [string]$Exe,
    # The only positional parameter: without that, "--connect echo" typed at a
    # PowerShell prompt lands in -Fixture and -Wait.
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$AppArgs
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Fixture) { $Fixture = Join-Path $Root 'build\e2e' }
if (-not (Test-Path (Join-Path $Fixture 'store.json'))) { throw "no fixture at $Fixture; run tools\e2e\make-fixture.ps1 first" }
$Fixture = (Resolve-Path $Fixture).Path

if ($Exe) {
    if (-not (Test-Path $Exe)) { throw "no build at $Exe" }
    $Exe = (Resolve-Path $Exe).Path
}
else {
    # The machine's architecture, as in build.ps1 (an emulated shell reports its own).
    $machine = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine')
    if (-not $machine) { $machine = $env:PROCESSOR_ARCHITECTURE }
    $arch = if ($machine -eq 'ARM64') { 'arm64' } else { 'x64' }
    $Exe = Join-Path $Root "build\app\win-$arch\Toolbox.exe"
    if (-not (Test-Path $Exe)) { throw "no build at $Exe; run tools\build.ps1 first" }
}
$OutDir = Split-Path -Parent $Exe

$running = @(Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path -ieq $Exe) } catch { $false }
    })
if ($running.Count -gt 0) {
    $running | Stop-Process -Force
    $running | ForEach-Object { try { $_.WaitForExit(5000) | Out-Null } catch { } }
}

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.WorkingDirectory = $OutDir
# Started through the shell so the app does not inherit this script's output
# handles: a caller piping the script's output would otherwise wait for the
# app to exit. The shell cannot pass an environment, so the overrides are set
# here for the moment of the launch and put back afterwards.
$psi.UseShellExecute = $true
if ($AppArgs) {
    $psi.Arguments = ($AppArgs | ForEach-Object { if ($_ -match '[\s"]') { '"' + ($_ -replace '"', '\"') + '"' } else { $_ } }) -join ' '
}
$overrides = @{
    TOOLBOX_STORE_PATH     = Join-Path $Fixture 'store.json'
    TOOLBOX_KNOWN_HOSTS    = Join-Path $Fixture 'known_hosts'
    TOOLBOX_VAULT_PATH     = Join-Path $Fixture 'vault.json'
    TOOLBOX_VAULT_KEY_PATH = Join-Path $Fixture 'vault.key'
    TOOLBOX_SETTINGS_PATH  = Join-Path $Fixture 'settings.json'
    TOOLBOX_LOG_PATH       = Join-Path $Fixture 'toolbox.log'
    TOOLBOX_WEBVIEW_DATA   = Join-Path $Fixture 'webview'
    TOOLBOX_UPDATES_DIR    = Join-Path $Fixture 'updates'
}
$previous = @{}
foreach ($name in $overrides.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
    [Environment]::SetEnvironmentVariable($name, $overrides[$name], 'Process')
}
try { $process = [System.Diagnostics.Process]::Start($psi) }
finally {
    foreach ($name in $overrides.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
Write-Host "launched pid $($process.Id) with: $($psi.Arguments)"
if ($Wait -gt 0) { Start-Sleep -Seconds $Wait }
if ($process.HasExited) { Write-Host "the app has exited (code $($process.ExitCode)); see $(Join-Path $Fixture 'toolbox.log')" }
