<#
.SYNOPSIS
    The packaged disc image, opened as Windows opens it, and the app started
    from it.

.DESCRIPTION
        tools\e2e\check-disc.ps1 [-Arch arm64|x64]

    Mounts build\release\Toolbox-<version>-win-<arch>.iso as a drive (what a
    double-click on the file does) and checks what a person would find: the
    disc's name, the three things at its top (README.txt, the installer, the
    Toolbox folder), that the README reads as plain text, that the installer
    on the disc is the one beside the image, that the program is for the
    right processor, and that Toolbox starts from the disc against the
    fixture and closes again. The image is unmounted at the end, whatever
    happened.

    "tools\build.ps1 package" has already compared every file on the disc
    with what went in; this is the look from the outside. While it runs, a
    drive named "Toolbox <version>" shows in File Explorer.

    Needs: tools\build.ps1 core package (for the architecture) and the
    fixture (tools\e2e\make-fixture.ps1). No mouse, no keys, no clipboard.
    The exit code is the number of failed checks.
#>
[CmdletBinding()]
param(
    [ValidateSet('', 'arm64', 'x64')]
    [string]$Arch = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Arch) {
    $machine = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine')
    if (-not $machine) { $machine = $env:PROCESSOR_ARCHITECTURE }
    $Arch = if ($machine -eq 'ARM64') { 'arm64' } else { 'x64' }
}
$Version = (Get-Content (Join-Path $Root 'VERSION') -Raw).Trim()
$Name = "Toolbox-$Version-win-$Arch"
$Release = Join-Path $Root 'build\release'
$Iso = Join-Path $Release "$Name.iso"
$Setup = Join-Path $Release "$Name-setup.exe"
$Fixture = Join-Path $Root 'build\e2e'
$Log = Join-Path $Fixture 'toolbox.log'
$Launch = Join-Path $PSScriptRoot 'launch.ps1'

if (-not (Test-Path $Iso)) { throw "no disc image for $Arch in build\release; run: tools\build.ps1 core package -Arch $Arch" }
if (-not (Test-Path (Join-Path $Fixture 'store.json'))) { throw 'no fixture; run tools\e2e\make-fixture.ps1 first' }

$script:Checks = 0
$script:Failed = 0
function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Check([string]$what, [bool]$ok, [string]$detail = '') {
    $script:Checks++
    if ($ok) { Write-Host "ok    $what" }
    else {
        $script:Failed++
        Write-Host ("FAIL  $what" + $(if ($detail) { ": $detail" } else { '' })) -ForegroundColor Red
    }
}

# Timed by a stopwatch: this machine's clock cannot be trusted for a wait.
function Wait-Until([scriptblock]$condition, [int]$seconds) {
    $waited = [System.Diagnostics.Stopwatch]::StartNew()
    while ($waited.Elapsed.TotalSeconds -lt $seconds) {
        if (& $condition) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return [bool](& $condition)
}

# Which processor a DLL or EXE is for, from its header.
function Get-PeArchitecture([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $machineType = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4)
    if ($machineType -eq 0xAA64) { return 'arm64' }
    if ($machineType -eq 0x8664) { return 'x64' }
    return ('0x{0:X4}' -f $machineType)
}

$drive = $null
function Get-FromDisc {
    if (-not $drive) { return @() }
    Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and $_.Path.StartsWith($drive, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    }
}

Step "mounting $Name.iso"
Mount-DiskImage -ImagePath $Iso -StorageType ISO | Out-Null
try {
    Wait-Until { $shown = Get-DiskImage -ImagePath $Iso | Get-Volume; $shown -and $shown.DriveLetter } 15 | Out-Null
    $volume = Get-DiskImage -ImagePath $Iso | Get-Volume
    Check 'Windows gives the disc a drive letter' ($null -ne $volume -and [bool]$volume.DriveLetter)
    if ($null -ne $volume -and $volume.DriveLetter) {
        $drive = "$($volume.DriveLetter):\"
        Check "the disc is called 'Toolbox $Version'" ($volume.FileSystemLabel -ceq "Toolbox $Version") "$($volume.FileSystemLabel)"

        $top = @(Get-ChildItem -LiteralPath $drive -Force | Sort-Object Name | ForEach-Object { $_.Name })
        $wanted = @('README.txt', 'Toolbox', "$Name-setup.exe") | Sort-Object
        Check 'at its top: the README, the installer and the Toolbox folder' (($top -join '|') -eq ($wanted -join '|')) ($top -join ', ')

        $readme = Join-Path $drive 'README.txt'
        $bytes = [System.IO.File]::ReadAllBytes($readme)
        $text = [System.Text.Encoding]::ASCII.GetString($bytes)
        Check 'the README begins with what this is' ($text.StartsWith("Toolbox $Version for Windows ($Arch)`r`n")) ($text.Split("`n")[0])
        Check 'the README is plain ASCII' (@($bytes | Where-Object { $_ -gt 126 -or ($_ -lt 32 -and $_ -ne 13 -and $_ -ne 10) }).Count -eq 0)
        Check 'its lines end the Windows way' (($text -replace "`r`n", '').IndexOfAny([char[]]"`r`n") -lt 0)
        Check 'it names the installer that is on the disc' ($text.Contains("$Name-setup.exe"))

        $onDisc = Join-Path $drive "$Name-setup.exe"
        Check 'the installer on the disc is the one beside the image' ((Test-Path $Setup) -and (Get-FileHash -LiteralPath $onDisc -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $Setup -Algorithm SHA256).Hash)

        $exe = Join-Path $drive 'Toolbox\Toolbox.exe'
        Check 'the Toolbox folder holds the program' (Test-Path -LiteralPath $exe)
        Check "the program is for $Arch" ((Get-PeArchitecture $exe) -eq $Arch) (Get-PeArchitecture $exe)
        Check "the core is for $Arch" ((Get-PeArchitecture (Join-Path $drive 'Toolbox\toolbox_core.dll')) -eq $Arch)
        Check 'the folder says what it holds (files.txt)' (Test-Path -LiteralPath (Join-Path $drive 'Toolbox\files.txt'))

        Step 'starting Toolbox from the disc, against the fixture'
        if (Test-Path $Log) { Remove-Item -LiteralPath $Log -Force }
        & $Launch -Exe $exe -Wait 0
        Check 'the app starts and says which version it is' (Wait-Until { (Test-Path $Log) -and (Select-String -Path $Log -Pattern "Toolbox $([regex]::Escape($Version)) \(build \d+\) starting" -Quiet) } 60)
        Check 'it has a window' (Wait-Until { @(Get-FromDisc | Where-Object { $_.MainWindowHandle -ne 0 }).Count -gt 0 } 30)
        Start-Sleep -Seconds 2
        foreach ($copy in @(Get-FromDisc)) { try { $copy.CloseMainWindow() | Out-Null } catch { } }
        Check 'it closes when its window is closed' (Wait-Until { @(Get-FromDisc).Count -eq 0 } 20)
        Check 'and says so' (Wait-Until { (Test-Path $Log) -and (Select-String -Path $Log -Pattern 'Toolbox exiting' -Quiet) } 5)
    }
}
finally {
    @(Get-FromDisc) | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Dismount-DiskImage -ImagePath $Iso | Out-Null
}

Write-Host ''
if ($script:Failed -eq 0) { Write-Host "passed: $($script:Checks) checks ($Name.iso)" -ForegroundColor Green }
else { Write-Host "FAILED: $($script:Failed) of $($script:Checks) checks" -ForegroundColor Red }
exit $script:Failed
