<#
.SYNOPSIS
    The packaged installer, run for real: installed, started, updated by the
    app itself, and removed again.

.DESCRIPTION
        tools\e2e\check-installer.ps1 [-Arch arm64|x64] [-Keep]

    Takes build\release\Toolbox-<version>-win-<arch>-setup.exe as "tools\build.ps1
    package" made it and

      1. has it refuse a folder that holds somebody else's files;
      2. installs it, silently, into build\e2e-install\Toolbox, and compares
         every file with the packaged folder;
      3. reads what it told Windows: the entry under Installed apps and the
         Start menu shortcut;
      4. starts the installed copy against the fixture and has it update
         itself from the feed in build\release (the real feed, signed with the
         real update key, read from the folder and not from GitHub), then
         checks that the copy is still the package file for file, that the
         uninstaller survived the update, and that the app put the version
         in Installed apps right;
      5. has the uninstaller refuse while the app runs;
      6. uninstalls, and checks that the app's files are gone, a file a later
         version would have added among them, that a file of the user's own
         stays, and that the entry and the shortcut are gone.

    The installer is the one that ships, so for the length of the run this
    account has Toolbox under Installed apps and in the Start menu. The check
    therefore refuses to start when Toolbox is already installed here: it
    would be installing over, and then removing, a real copy.

    Needs, for the architecture: tools\build.ps1 core package, then
    tools\build.ps1 feed (after both architectures), and the fixture
    (tools\e2e\make-fixture.ps1). No mouse, no keys, no clipboard. The exit
    code is the number of failed checks. -Keep leaves build\e2e-install.
#>
[CmdletBinding()]
param(
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
$Name = "Toolbox-$Version-win-$Arch"
$Release = Join-Path $Root 'build\release'
$Stage = Join-Path $Release $Name
$Setup = Join-Path $Release "$Name-setup.exe"
$Fixture = Join-Path $Root 'build\e2e'
$Log = Join-Path $Fixture 'toolbox.log'
$Work = Join-Path $Root 'build\e2e-install'
$Target = Join-Path $Work 'Toolbox'
$Exe = Join-Path $Target 'Toolbox.exe'
$Launch = Join-Path $PSScriptRoot 'launch.ps1'
# The installer's AppId (tools\installer\Toolbox.iss).
$KeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{89DB47A7-75BF-4E60-83DB-D1927B1EB6AD}_is1'
$Shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Toolbox.lnk'
$RealUpdates = Join-Path $env:LOCALAPPDATA 'Toolbox\updates'

if (-not (Test-Path $Setup) -or -not (Test-Path (Join-Path $Stage 'Toolbox.exe'))) { throw "no installer for $Arch in build\release; run: tools\build.ps1 core package -Arch $Arch" }
if (-not (Test-Path (Join-Path $Release 'update.json.sig'))) { throw 'no update feed in build\release; run: tools\build.ps1 feed' }
if (-not (Test-Path (Join-Path $Fixture 'store.json'))) { throw 'no fixture; run tools\e2e\make-fixture.ps1 first' }
if (Test-Path -LiteralPath $KeyPath) { throw 'Toolbox is installed for this account. This check installs a copy and removes it again, and would do that to the real one: not run.' }
if (Test-Path -LiteralPath $Shortcut) { throw "there is a Toolbox shortcut in the Start menu already ($Shortcut): not run." }
if ((Test-Path -LiteralPath $RealUpdates) -and @(Get-ChildItem -LiteralPath $RealUpdates -Force).Count -gt 0) {
    throw "a copy of Toolbox has an update waiting in $RealUpdates, which the uninstaller would remove: not run."
}

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

# Copies of the app started from this run's folders, the unpacked update included.
function Get-Copies {
    Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase) -or $_.Path.StartsWith((Join-Path $Fixture 'updates'), [StringComparison]::OrdinalIgnoreCase)) } catch { $false }
    }
}

# Closed the way a person closes it; ended only if that does not work.
function Stop-Copies {
    $copies = @(Get-Copies)
    foreach ($copy in $copies) { try { $copy.CloseMainWindow() | Out-Null } catch { } }
    if ($copies.Count -gt 0) { Wait-Until { @(Get-Copies).Count -eq 0 } 10 | Out-Null }
    @(Get-Copies) | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Invoke-Setup([string]$folder, [string]$logName) {
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', "/DIR=`"$folder`"", "/LOG=`"$(Join-Path $Work $logName)`"")
    return (Start-Process -FilePath $Setup -ArgumentList $arguments -Wait -PassThru).ExitCode
}

function Invoke-Uninstall([string]$logName) {
    $uninstaller = Join-Path $Target 'unins000.exe'
    $arguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=`"$(Join-Path $Work $logName)`"")
    return (Start-Process -FilePath $uninstaller -ArgumentList $arguments -Wait -PassThru).ExitCode
}

# The files of a folder, as relative path -> SHA-256.
function Get-Hashes([string]$folder) {
    $hashes = @{}
    foreach ($file in [System.IO.Directory]::GetFiles($folder, '*', 'AllDirectories')) {
        $hashes[$file.Substring($folder.Length + 1)] = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
    }
    return $hashes
}

# What differs between the installed folder and the package, the uninstaller's own files aside.
function Compare-WithPackage {
    $wanted = Get-Hashes $Stage
    $found = Get-Hashes $Target
    $different = @()
    foreach ($name in $wanted.Keys) {
        if (-not $found.ContainsKey($name)) { $different += "missing: $name" }
        elseif ($found[$name] -ne $wanted[$name]) { $different += "not as packaged: $name" }
    }
    foreach ($name in $found.Keys) {
        if (-not $wanted.ContainsKey($name) -and $name -notmatch '^unins\d{3}\.(exe|dat|msg)$') { $different += "not from the package: $name" }
    }
    return $different
}

Stop-Copies
if (Test-Path $Work) { Remove-Item -LiteralPath $Work -Recurse -Force }
New-Item -ItemType Directory -Force $Work | Out-Null

try {
    Step "1. a folder with somebody else's files in it is refused"
    $foreign = Join-Path $Work 'Somebody'
    New-Item -ItemType Directory -Force $foreign | Out-Null
    Set-Content -LiteralPath (Join-Path $foreign 'theirs.txt') -Value 'not the app''s' -Encoding ascii
    $code = Invoke-Setup $foreign 'refused.log'
    Check 'the installer stops (exit code 7)' ($code -eq 7) "exit code $code"
    Check 'nothing was put into that folder' (@(Get-ChildItem -LiteralPath $foreign -Force).Count -eq 1)
    Check 'nothing was told to Windows' (-not (Test-Path -LiteralPath $KeyPath))

    Step "2. install into $Target"
    $code = Invoke-Setup $Target 'install.log'
    Check 'the installer finishes (exit code 0)' ($code -eq 0) "exit code $code"
    $different = @(Compare-WithPackage)
    Check 'the installed folder is the package, file for file' ($different.Count -eq 0) (($different | Select-Object -First 5) -join '; ')
    Check 'the uninstaller is there' ((Test-Path (Join-Path $Target 'unins000.exe')) -and (Test-Path (Join-Path $Target 'unins000.dat')))

    Step '3. what Windows was told'
    $entry = Get-ItemProperty -LiteralPath $KeyPath -ErrorAction SilentlyContinue
    Check 'there is an entry under Installed apps' ($null -ne $entry)
    if ($null -ne $entry) {
        Check 'it is called Toolbox' ($entry.DisplayName -eq 'Toolbox') "$($entry.DisplayName)"
        Check "it says version $Version" ($entry.DisplayVersion -eq $Version) "$($entry.DisplayVersion)"
        Check 'it names the folder' ($entry.InstallLocation.TrimEnd('\') -ieq $Target) "$($entry.InstallLocation)"
        Check 'it names the uninstaller' ($entry.UninstallString -match [regex]::Escape((Join-Path $Target 'unins000.exe'))) "$($entry.UninstallString)"
    }
    $link = if (Test-Path -LiteralPath $Shortcut) { (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut) } else { $null }
    Check 'the Start menu has a shortcut to the installed app' ($null -ne $link -and $link.TargetPath -ieq $Exe) $(if ($null -ne $link) { $link.TargetPath } else { 'no shortcut' })

    Step '4. the installed copy updates itself, and says which version it is'
    # As if an older version had been installed: the app has to put it right.
    Set-ItemProperty -LiteralPath $KeyPath -Name DisplayVersion -Value '0.0.1'
    # A file the old version's list names and the new one's does not, and one nobody lists.
    Set-Content -LiteralPath (Join-Path $Target 'old-version-only.txt') -Value 'x' -Encoding ascii
    Add-Content -LiteralPath (Join-Path $Target 'files.txt') -Value 'old-version-only.txt' -Encoding ascii
    Set-Content -LiteralPath (Join-Path $Target 'mine.txt') -Value 'the user''s own' -Encoding ascii
    if (Test-Path $Log) { Remove-Item -LiteralPath $Log -Force }
    & $Launch -Exe $Exe -Wait 0 --updater-feed $Release --updater-pretend-build 1 --demo-update --demo-update-install
    Check 'the copy finds the version in the feed' (Wait-Log "updates: found $([regex]::Escape($Version))" 60)
    Check 'it installs it' (Wait-Log "updates: installed $([regex]::Escape($Version))" 240)
    # The line of the copy that came back, which is the second "starting" in the log.
    Check 'the app comes back' (Wait-Until { (Test-Path $Log) -and @(Select-String -Path $Log -Pattern "Toolbox $([regex]::Escape($Version)) \(build \d+\) starting").Count -ge 2 } 60)
    Start-Sleep -Seconds 2
    $different = @(Compare-WithPackage | Where-Object { $_ -ne 'not from the package: mine.txt' })
    Check 'the updated folder is the package, file for file' ($different.Count -eq 0) (($different | Select-Object -First 5) -join '; ')
    Check 'a file only the old version listed is gone' (-not (Test-Path (Join-Path $Target 'old-version-only.txt')))
    Check 'a file of the user''s own is still there' (Test-Path (Join-Path $Target 'mine.txt'))
    Check 'the uninstaller is still there' ((Test-Path (Join-Path $Target 'unins000.exe')) -and (Test-Path (Join-Path $Target 'unins000.dat')))
    Check "Installed apps says $Version again" (Wait-Until { (Get-ItemProperty -LiteralPath $KeyPath).DisplayVersion -eq $Version } 15) "$((Get-ItemProperty -LiteralPath $KeyPath).DisplayVersion)"
    Check 'the log says so' (Wait-Log 'installed copy: Windows'' list of installed apps now says' 5)

    Step '5. the uninstaller refuses while the app runs'
    Check 'the app is running' (@(Get-Copies).Count -gt 0)
    $code = Invoke-Uninstall 'uninstall-refused.log'
    Check 'the uninstaller stops' ($code -ne 0) "exit code $code"
    Check 'the app is still installed' ((Test-Path $Exe) -and (Test-Path -LiteralPath $KeyPath))

    Step '6. uninstall'
    Stop-Copies
    Check 'the app closed' (@(Get-Copies).Count -eq 0)
    # What a later version would have brought: on its list, and unknown to the installer.
    Set-Content -LiteralPath (Join-Path $Target 'later-version.dll') -Value 'x' -Encoding ascii
    Add-Content -LiteralPath (Join-Path $Target 'files.txt') -Value 'later-version.dll' -Encoding ascii
    $code = Invoke-Uninstall 'uninstall.log'
    Check 'the uninstaller finishes (exit code 0)' ($code -eq 0) "exit code $code"
    # The uninstaller's last steps run in a copy of itself that outlives it by a moment.
    Wait-Until { -not (Test-Path -LiteralPath $KeyPath) -and -not (Test-Path (Join-Path $Target 'unins000.exe')) } 60 | Out-Null
    $left = @(if (Test-Path $Target) { Get-ChildItem -LiteralPath $Target -Recurse -Force | ForEach-Object { $_.FullName.Substring($Target.Length + 1) } })
    Check 'only the user''s own file is left in the folder' ($left.Count -eq 1 -and $left[0] -eq 'mine.txt') (($left | Select-Object -First 8) -join ', ')
    Check 'the entry under Installed apps is gone' (-not (Test-Path -LiteralPath $KeyPath))
    Check 'the Start menu shortcut is gone' (-not (Test-Path -LiteralPath $Shortcut))
}
finally {
    Stop-Copies
    # Whatever happened above, nothing of the test copy stays with Windows.
    if ((Test-Path -LiteralPath $KeyPath) -and (Test-Path (Join-Path $Target 'unins000.exe'))) {
        Write-Host 'cleaning up: uninstalling the test copy'
        Invoke-Uninstall 'uninstall-cleanup.log' | Out-Null
        Wait-Until { -not (Test-Path -LiteralPath $KeyPath) } 60 | Out-Null
    }
    if (Test-Path -LiteralPath $KeyPath) {
        $about = (Get-ItemProperty -LiteralPath $KeyPath).InstallLocation
        if ($about -and $about.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $KeyPath -Recurse -Force }
    }
    if (Test-Path -LiteralPath $Shortcut) {
        $points = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut).TargetPath
        if ($points -and $points.StartsWith($Work, [StringComparison]::OrdinalIgnoreCase)) { Remove-Item -LiteralPath $Shortcut -Force }
    }
}

Write-Host ''
if ($script:Failed -eq 0) {
    Write-Host "passed: $($script:Checks) checks ($Name-setup.exe)" -ForegroundColor Green
    if (-not $Keep -and (Test-Path $Work)) { Remove-Item -LiteralPath $Work -Recurse -Force }
}
else {
    Write-Host "FAILED: $($script:Failed) of $($script:Checks) checks; the logs are in $Work" -ForegroundColor Red
}
exit $script:Failed
