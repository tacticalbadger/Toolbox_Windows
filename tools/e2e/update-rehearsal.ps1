<#
.SYNOPSIS
    A whole over-the-air update, with nothing real in it.

.DESCRIPTION
        tools\e2e\update-rehearsal.ps1 [-Keep]

    Takes the build in build\app, makes two copies of it that stand for an
    installed version and a newer one ("9.9.2", build 9999999), a feed for
    the newer one signed with a key made for this run, and has the installed
    copy update itself from that feed. It passes only if

      1. the installed copy becomes the new one on disk and starts again:
         the new version's files are there, a file only the old version's
         list names is gone, a file neither version knows stays;
      2. the updated copy is then offered nothing;
      3. a package altered after signing is refused and nothing is installed;
      4. a feed altered after signing is refused and nothing is downloaded.

    Nothing real is touched: not the update key (the run makes one named
    "Toolbox Rehearsal Key" and deletes it), not GitHub (the feed is a
    folder), not the real app data (the copies run against the fixture, and
    keep their downloads under build\e2e\updates). The app's part is the
    testing aids --updater-feed, --updater-key, --updater-pretend-build,
    --demo-update and --demo-update-install, which only a run against a
    store named in the environment honours.

    Needs: tools\build.ps1 app, tools\e2e\make-fixture.ps1. A run that passes
    removes build\e2e-update (-Keep keeps it); a failed run's stays for a look.
#>
[CmdletBinding()]
param([switch]$Keep)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$machine = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine')
if (-not $machine) { $machine = $env:PROCESSOR_ARCHITECTURE }
$Rid = if ($machine -eq 'ARM64') { 'win-arm64' } else { 'win-x64' }
$Built = Join-Path $Root "build\app\$Rid"
$Fixture = Join-Path $Root 'build\e2e'
$Work = Join-Path $Root 'build\e2e-update'
$Log = Join-Path $Fixture 'toolbox.log'
$KeyName = 'Toolbox Rehearsal Key'
$Keys = Join-Path $Root 'tools\update-key.ps1'
$Launch = Join-Path $PSScriptRoot 'launch.ps1'
$Utf8 = New-Object System.Text.UTF8Encoding($false)
$Version = '9.9.2'
$BuildNumber = 9999999

if (-not (Test-Path (Join-Path $Built 'Toolbox.exe'))) { throw "no build at $Built; run tools\build.ps1 first" }
if (-not (Test-Path (Join-Path $Fixture 'store.json'))) { throw 'no fixture; run tools\e2e\make-fixture.ps1 first' }

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

# Instances started from this run's folders, the unpacked update included.
function Get-Copies {
    Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase) -or $_.Path.StartsWith((Join-Path $Fixture 'updates'), [StringComparison]::OrdinalIgnoreCase)) } catch { $false }
    }
}

function Stop-Copies {
    $copies = @(Get-Copies)
    foreach ($copy in $copies) { try { $copy.CloseMainWindow() | Out-Null } catch { } }
    if ($copies.Count -gt 0) { Start-Sleep -Milliseconds 1500 }
    @(Get-Copies) | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Wait-Log([string]$pattern, [int]$seconds) {
    for ($i = 0; $i -lt $seconds * 4; $i++) {
        if ((Test-Path $Log) -and (Select-String -Path $Log -Pattern $pattern -Quiet)) { return $true }
        Start-Sleep -Milliseconds 250
    }
    return $false
}

function Copy-Tree([string]$from, [string]$to) {
    New-Item -ItemType Directory -Force $to | Out-Null
    Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
}

function Write-Manifest([string]$folder, [string[]]$extra) {
    $files = @(Get-ChildItem $folder -Recurse -File | ForEach-Object { $_.FullName.Substring($folder.Length + 1).Replace('\', '/') }) + 'files.txt' + $extra | Sort-Object -Unique
    [System.IO.File]::WriteAllText((Join-Path $folder 'files.txt'), (($files -join "`n") + "`n"), $Utf8)
}

# A copy that stands for the installed version: the build, a file its list
# names that the new version does not have, and a file no version knows.
function New-Installed([string]$name) {
    $folder = Join-Path $Work $name
    Copy-Tree $Built $folder
    [System.IO.File]::WriteAllText((Join-Path $folder 'old-only.txt'), "only the old version has this`n", $Utf8)
    Write-Manifest $folder @()
    [System.IO.File]::WriteAllText((Join-Path $folder 'users-own.txt'), "no version put this here`n", $Utf8)
    return $folder
}

function New-Feed([string]$name, [string]$zip) {
    $folder = Join-Path $Work $name
    New-Item -ItemType Directory -Force $folder | Out-Null
    Copy-Item $zip $folder
    $file = Split-Path -Leaf $zip
    $feed = [ordered]@{
        format = 1; version = $Version; build = $BuildNumber; notes = "- A rehearsal: nothing real is in this version."
        packages = [ordered]@{ $Rid = [ordered]@{ file = $file; size = (Get-Item $zip).Length; sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() } }
    }
    $path = Join-Path $folder 'update.json'
    [System.IO.File]::WriteAllText($path, (($feed | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n", $Utf8)
    & $Keys sign -Name $KeyName -File $path | Out-Null
    return $folder
}

function Start-Copy([string]$folder, [string]$feed, [string[]]$more) {
    Remove-Item -LiteralPath $Log -Force -ErrorAction SilentlyContinue
    $arguments = @('--updater-feed', $feed, '--updater-key', $script:PublicKey, '--demo-update') + $more
    & $Launch -Exe (Join-Path $folder 'Toolbox.exe') -Wait 0 @arguments | Out-Null
}

$failures = @()
function Check([string]$what, [bool]$passed) {
    if ($passed) { Write-Host "  ok    $what" -ForegroundColor Green }
    else { Write-Host "  FAIL  $what" -ForegroundColor Red; $script:failures += $what }
}

Stop-Copies
if (Test-Path $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force $Work | Out-Null
$updates = Join-Path $Fixture 'updates'
if (Test-Path $updates) { Remove-Item -LiteralPath $updates -Recurse -Force }

try {
    Step 'A key for this run, the two versions, and a feed'
    & $Keys delete -Name $KeyName | Out-Null
    $script:PublicKey = (& $Keys create -Name $KeyName 6>$null | Select-Object -Last 1).Trim()

    $packageName = "Toolbox-$Version-$Rid"
    $stage = Join-Path $Work "stage\$packageName"
    Copy-Tree $Built $stage
    [System.IO.File]::WriteAllText((Join-Path $stage 'new-only.txt'), "only the new version has this`n", $Utf8)
    Write-Manifest $stage @()
    $zip = Join-Path $Work "$packageName.zip"
    & tar.exe -a -c -f $zip -C (Join-Path $Work 'stage') $packageName
    if ($LASTEXITCODE -ne 0) { throw 'tar failed' }
    $good = New-Feed 'feed' $zip

    Step '1. The installed copy updates itself'
    $installed = New-Installed 'installed'
    Start-Copy $installed $good @('--demo-update-install')
    $applied = Wait-Log 'updates: installed ' 90
    Check 'the feed was accepted and the version found' (Select-String -Path $Log -Pattern "updates: found $Version" -Quiet)
    Check 'the package was downloaded and matched the feed' (Select-String -Path $Log -Pattern 'is verified and will be installed now' -Quiet)
    Check 'the new copy installed itself' $applied
    Start-Sleep -Seconds 3
    Check 'the new version''s file is in the installed folder' (Test-Path (Join-Path $installed 'new-only.txt'))
    Check 'a file only the old version listed is gone' (-not (Test-Path (Join-Path $installed 'old-only.txt')))
    Check 'a file no version knows is still there' (Test-Path (Join-Path $installed 'users-own.txt'))
    $again = @(Get-Copies | Where-Object { $_.Path -ieq (Join-Path $installed 'Toolbox.exe') })
    Check 'the app is running again from its usual folder' ($again.Count -eq 1)
    Check 'the log has no error' (-not (Select-String -Path $Log -Pattern ' ERROR ' -Quiet))
    Stop-Copies

    Step '2. The updated copy is offered nothing'
    Start-Copy $installed $good @('--updater-pretend-build', "$BuildNumber")
    Check 'nothing newer in the feed' (Wait-Log 'updates: nothing newer in the feed' 30)
    Stop-Copies

    Step '3. A package altered after signing'
    $badPackage = Join-Path $Work 'feed-altered-package'
    Copy-Tree $good $badPackage
    $bytes = [System.IO.File]::ReadAllBytes((Join-Path $badPackage "$packageName.zip"))
    $middle = [int]($bytes.Length / 2)
    $bytes[$middle] = $bytes[$middle] -bxor 0x01
    [System.IO.File]::WriteAllBytes((Join-Path $badPackage "$packageName.zip"), $bytes)
    $second = New-Installed 'installed-2'
    Start-Copy $second $badPackage @('--demo-update-install')
    Check 'the download is refused' (Wait-Log 'is not the file the release described' 60)
    Start-Sleep -Seconds 2
    Check 'nothing was installed' ((Test-Path (Join-Path $second 'old-only.txt')) -and -not (Test-Path (Join-Path $second 'new-only.txt')))
    Check 'no copy was started to install anything' (-not (Select-String -Path $Log -Pattern 'started to install itself' -Quiet))
    Stop-Copies

    Step '4. A feed altered after signing'
    $badFeed = Join-Path $Work 'feed-altered-feed'
    Copy-Tree $good $badFeed
    $text = [System.IO.File]::ReadAllText((Join-Path $badFeed 'update.json'), $Utf8)
    [System.IO.File]::WriteAllText((Join-Path $badFeed 'update.json'), $text.Replace('A rehearsal', 'A rehearsaI'), $Utf8)
    if (Test-Path $updates) { Remove-Item -LiteralPath $updates -Recurse -Force }
    Start-Copy $second $badFeed @('--demo-update-install')
    Check 'the feed is refused for its signature' (Wait-Log 'not signed with Toolbox''s update key' 30)
    Start-Sleep -Seconds 1
    Check 'nothing was downloaded' (-not (Test-Path $updates) -or @(Get-ChildItem $updates -Recurse -File).Count -eq 0)
    Check 'nothing was installed' (-not (Test-Path (Join-Path $second 'new-only.txt')))
    Stop-Copies
}
finally {
    Stop-Copies
    & $Keys delete -Name $KeyName | Out-Null
}

if ($failures.Count -gt 0) {
    Write-Host "the rehearsal FAILED ($($failures.Count)): $($failures -join '; ')" -ForegroundColor Red
    Write-Host "left for a look: $Work, $Log"
    exit 1
}
if (-not $Keep) {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path $updates) { Remove-Item -LiteralPath $updates -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host 'the rehearsal passed' -ForegroundColor Green
