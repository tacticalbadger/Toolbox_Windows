<#
.SYNOPSIS
    After a release: a copy of an earlier release takes the published
    update from GitHub, as a person's copy will.

.DESCRIPTION
        tools\e2e\check-published-update.ps1 -From <version> [-Arch arm64|x64] [-Keep]

    The old copy is the earlier release's own zip, unpacked: the file from
    build\release when it is still there, otherwise downloaded from that
    release's page. Nothing in it is changed. It is started against the
    fixture and told to read the real feed (--updater-feed github; a run
    against a fixture looks nowhere unless a feed is named) and to take what
    it finds without the dialog (--demo-update --demo-update-install). It
    then has to do what an installed copy does: believe the feed because the
    update key signed it, fetch the zip from GitHub, check it against the
    feed, replace its own files and come back as the published version,
    which is compared file for file with build\release\Toolbox-<version>-
    win-<arch> (the folder this release was made from). Started again, it
    must be offered nothing.

    Needs the fixture (tools\e2e\make-fixture.ps1) and the current
    version's package folder. Talks to GitHub and downloads the package
    (some 75 MB). No mouse, no keys, no clipboard. The exit code is the
    number of failed checks. -Keep leaves build\e2e-published.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$From,
    [ValidateSet('', 'arm64', 'x64')]
    [string]$Arch = '',
    [switch]$Keep
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
if ($From -eq $Version) { throw "-From is the version an update is taken from; $Version is the one being checked" }
$Release = Join-Path $Root 'build\release'
$Stage = Join-Path $Release "Toolbox-$Version-win-$Arch"
$OldName = "Toolbox-$From-win-$Arch"
$Fixture = Join-Path $Root 'build\e2e'
$Log = Join-Path $Fixture 'toolbox.log'
$Work = Join-Path $Root 'build\e2e-published'
$Target = Join-Path $Work $OldName
$Exe = Join-Path $Target 'Toolbox.exe'
$Launch = Join-Path $PSScriptRoot 'launch.ps1'

if (-not (Test-Path (Join-Path $Stage 'Toolbox.exe'))) { throw "no package folder for $Version ($Arch) in build\release: it is what the updated copy is compared with" }
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

function Wait-Log([string]$pattern, [int]$seconds) {
    return Wait-Until { (Test-Path $Log) -and (Select-String -Path $Log -Pattern $pattern -Quiet) } $seconds
}

function Get-Copies {
    Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase) -or $_.Path.StartsWith((Join-Path $Fixture 'updates'), [StringComparison]::OrdinalIgnoreCase)) } catch { $false }
    }
}

function Stop-Copies {
    $copies = @(Get-Copies)
    foreach ($copy in $copies) { try { $copy.CloseMainWindow() | Out-Null } catch { } }
    if ($copies.Count -gt 0) { Wait-Until { @(Get-Copies).Count -eq 0 } 10 | Out-Null }
    @(Get-Copies) | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Get-Hashes([string]$folder) {
    $hashes = @{}
    foreach ($file in [System.IO.Directory]::GetFiles($folder, '*', 'AllDirectories')) {
        $hashes[$file.Substring($folder.Length + 1)] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    return $hashes
}

function Get-CopyVersion {
    $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $Target 'Toolbox.dll'))
    return "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)"
}

Stop-Copies
if (Test-Path $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force $Work | Out-Null

Step "the released $From, as it was handed out"
$zip = Join-Path $Release "$OldName.zip"
if (-not (Test-Path $zip)) {
    $remote = (& git -C $Root remote get-url origin | Select-Object -First 1).Trim()
    if ($remote -notmatch 'github\.com[:/]+([^/]+)/([^/]+?)(\.git)?$') { throw "origin is not a GitHub repository: $remote" }
    $zip = Join-Path $Work "$OldName.zip"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Write-Host "downloading $OldName.zip from the v$From release"
    Invoke-WebRequest -Uri "https://github.com/$($Matches[1])/$($Matches[2])/releases/download/v$From/$OldName.zip" -OutFile $zip -UseBasicParsing -TimeoutSec 900
}
& tar.exe -x -f $zip -C $Work
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Exe)) { throw "could not unpack $zip" }
Check "the copy is $From" ((Get-CopyVersion) -eq $From) (Get-CopyVersion)

try {
    Step "1. $From looks at the published feed and takes what it finds"
    if (Test-Path $Log) { Remove-Item -LiteralPath $Log -Force }
    & $Launch -Exe $Exe -Wait 0 --updater-feed github --demo-update --demo-update-install
    Check "it starts as $From" (Wait-Log "Toolbox $([regex]::Escape($From)) \(build \d+\) starting" 60)
    Check "it finds $Version in the feed on GitHub" (Wait-Log "updates: found $([regex]::Escape($Version))" 90)
    Check 'it fetches the package, checks it, and installs it' (Wait-Log "updates: installed $([regex]::Escape($Version))" 900)
    Check "the app comes back as $Version" (Wait-Log "Toolbox $([regex]::Escape($Version)) \(build \d+\) starting" 60)
    Start-Sleep -Seconds 2
    $wanted = Get-Hashes $Stage
    $found = Get-Hashes $Target
    $different = @()
    foreach ($name in $wanted.Keys) {
        if (-not $found.ContainsKey($name)) { $different += "missing: $name" }
        elseif ($found[$name] -ne $wanted[$name]) { $different += "not as packaged: $name" }
    }
    foreach ($name in $found.Keys) { if (-not $wanted.ContainsKey($name)) { $different += "left over: $name" } }
    Check "the copy on disk is $Version, file for file what was packaged" ($different.Count -eq 0) (($different | Select-Object -First 5) -join '; ')
    Check 'nothing went wrong on the way' (-not (Select-String -Path $Log -Pattern 'unhandled exception|could not be installed' -Quiet))
    Stop-Copies

    Step "2. the updated copy looks again"
    & $Launch -Exe $Exe -Wait 0 --updater-feed github --demo-update
    Check 'it is offered nothing' (Wait-Log 'updates: nothing newer in the feed' 90)
}
finally {
    Stop-Copies
}

Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host "passed: $($script:Checks) checks ($From -> $Version, $Arch)" -ForegroundColor Green
    if (-not $Keep -and (Test-Path $Work)) { Remove-Item -LiteralPath $Work -Recurse -Force }
}
else {
    Write-Host "FAILED: $($script:Failed) of $($script:Checks) checks; the copy is in $Work, the log is $Log" -ForegroundColor Red
}
exit $script:Failed
