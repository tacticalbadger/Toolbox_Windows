<#
.SYNOPSIS
    Publishes the packages built by "tools\build.ps1 package" as a GitHub
    Release on this repository.

.DESCRIPTION
        tools\publish.ps1 [-Replace] [-Prerelease] [-DryRun]

    Tag v<version> (from the VERSION file), with every
    build\release\Toolbox-<version>-win-<arch>.zip and SHA256SUMS attached,
    so the app can be downloaded from the repo's Releases page.

    Before anything is sent: the checksums are verified, the packages must
    have been built from the current commit (their build number is the commit
    count), and that commit must be on origin/main. The notes are the
    standard install text below plus docs\release-notes\<version>.md ("In
    this version") when that file exists. Keep names, hostnames and account
    details out of release notes.

    -Replace     re-upload the files and notes of a release that already exists
                 (not possible where releases are immutable)
    -Prerelease  mark the release as a pre-release
    -DryRun      run every check and show what would be published; send nothing

    The release is created as a draft, the files are attached, and only then
    is it published. With "immutable releases" switched on for a repository,
    GitHub accepts no file once a release is out, and a tag that has had
    such a release can never be used for another one: a release published
    without its files costs the version number. A draft left behind by a
    run that did not finish is found and carried on with.

    No GitHub CLI is needed: the release is created through the GitHub API
    with the credential Git already uses for this repository (Git Credential
    Manager). The token is used for these calls only and is never printed or
    stored.

    The packages are not code-signed. Until a certificate is in place the
    notes tell people how to get past the Windows warning.
#>
[CmdletBinding()]
param(
    [switch]$Replace,
    [switch]$Prerelease,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent $PSScriptRoot
$Version = (Get-Content (Join-Path $Root 'VERSION') -Raw).Trim()
$Tag = "v$Version"
$Out = Join-Path $Root 'build\release'
$SumsPath = Join-Path $Out 'SHA256SUMS'

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Git {
    $output = & git -C $Root @args 2>$null
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed (exit code $LASTEXITCODE)" }
    return $output
}

# ----- The files -----

$packages = @(Get-ChildItem $Out -Filter "Toolbox-$Version-win-*.zip" -ErrorAction SilentlyContinue | Sort-Object Name)
if ($packages.Count -eq 0 -or -not (Test-Path $SumsPath)) {
    throw "no packages for $Version in build\release; run: tools\build.ps1 core assets package (and again with -Arch for the other architecture)"
}

Step 'Checking the files'
$expected = @{}
foreach ($line in Get-Content $SumsPath) {
    if ($line -match '^([0-9a-f]{64})\s+(\S+)$') { $expected[$Matches[2]] = $Matches[1] }
}
$head = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()
$commitCount = [int](Invoke-Git rev-list --count HEAD | Select-Object -First 1)
foreach ($package in $packages) {
    $actual = (Get-FileHash $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if (-not $expected.ContainsKey($package.Name) -or $expected[$package.Name] -ne $actual) {
        throw "$($package.Name) does not match SHA256SUMS; run the package step again"
    }
    # The staged folder next to the zip says which commit count it was built from.
    $assembly = Join-Path $Out ($package.BaseName + '\Toolbox.dll')
    if (-not (Test-Path $assembly)) { throw "the staged folder for $($package.Name) is gone; run the package step again" }
    $fileVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
    if ($fileVersion.FilePrivatePart -ne $commitCount) {
        throw "$($package.Name) is build $($fileVersion.FilePrivatePart), but the current commit is number $commitCount. Package again from this commit."
    }
    Write-Host ("{0}  {1:N0} MB  build {2}" -f $package.Name, ($package.Length / 1MB), $fileVersion.FilePrivatePart)
}

# The tag points at the commit the release was built from: it has to be on GitHub.
Invoke-Git fetch -q origin | Out-Null
& git -C $Root merge-base --is-ancestor $head origin/main 2>$null
if ($LASTEXITCODE -ne 0) { throw 'the current commit is not on origin/main; push first' }
if (@(Invoke-Git status --porcelain).Count -gt 0) {
    Write-Host 'note: the working tree has uncommitted changes; the tag will point at the last commit'
}

$remote = (Invoke-Git remote get-url origin | Select-Object -First 1).Trim()
if ($remote -notmatch 'github\.com[:/]+([^/]+)/([^/]+?)(\.git)?$') { throw "origin is not a GitHub repository: $remote" }
$Owner = $Matches[1]
$Repo = $Matches[2]

# ----- The notes -----

$architectures = ($packages | ForEach-Object { if ($_.BaseName -match 'win-(\w+)$') { $Matches[1] } }) -join ' and '
$notes = @"
Toolbox is a Windows client for SSH, RDP and file transfer, with split panes, workspace tabs, a credential vault and 1Password and Active Directory integration.

## Install

1. Download the zip for your PC below: **win-x64** for most PCs, **win-arm64** for Windows on ARM (Settings > System > About shows the system type).
2. Right-click the zip, choose **Properties**, tick **Unblock** and press OK. Then extract it anywhere, for example ``%LOCALAPPDATA%\Programs``.
3. Run **Toolbox.exe** from the extracted folder.

This build is **not code-signed yet**. If Windows shows "Windows protected your PC", choose **More info**, then **Run anyway**. Nothing needs installing: the .NET runtime is inside the folder. Requires Windows 10 version 2004 or newer, or Windows 11; on Windows 10 the Microsoft Edge WebView2 Runtime must be present (it is part of Windows 11).

## First run

- Your connections and credentials are stored only on your PC, under ``%APPDATA%\Toolbox``. Nothing is shared between users.
- The first time you save a credential, Windows asks you to confirm it is you (Windows Hello, or your account password) to unlock the vault. You can turn that off under **Vault > Confirm It's You Before Unlocking**.
- 1Password integration needs the 1Password CLI: ``winget install AgileBits.1Password.CLI``, then enable **Integrate with 1Password CLI** in 1Password > Settings > Developer.
"@
$versionNotes = Join-Path $Root "docs\release-notes\$Version.md"
if (Test-Path $versionNotes) {
    # Read as UTF-8 by name: Windows PowerShell takes a file without a
    # byte-order mark for ANSI, which garbles anything beyond ASCII.
    $notes += "`n`n## In this version`n`n" + [System.IO.File]::ReadAllText($versionNotes, [System.Text.Encoding]::UTF8).Trim()
}
$notes += "`n`n``SHA256SUMS`` lists checksums for the downloads."
$title = "Toolbox $Version"

if ($DryRun) {
    Step "Dry run: would publish $Tag to $Owner/$Repo at $($head.Substring(0, 7))"
    Write-Host "title: $title"
    Write-Host "files: $(($packages | ForEach-Object { $_.Name }) -join ', '), SHA256SUMS"
    Write-Host '--- notes ---'
    Write-Host $notes
    return
}

# ----- GitHub -----

# The token Git already holds for this repository, asked for the way Git asks.
function Get-GitHubToken {
    # The question goes through a file and cmd's "<": a redirected input
    # stream from Windows PowerShell starts with a byte-order mark, and Git
    # then does not recognise the first line. The file holds no secret.
    $question = [System.IO.Path]::GetTempFileName()
    try {
        [System.IO.File]::WriteAllText($question, "protocol=https`nhost=github.com`npath=$Owner/$Repo.git`n`n", [System.Text.Encoding]::ASCII)
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = $env:ComSpec
        $psi.Arguments = "/d /c `"git credential fill < `"$question`"`""
        $psi.WorkingDirectory = $Root
        $psi.UseShellExecute = $false
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        $process = [System.Diagnostics.Process]::Start($psi)
        $text = $process.StandardOutput.ReadToEnd()
        $process.WaitForExit()
    }
    finally {
        Remove-Item -LiteralPath $question -Force -ErrorAction SilentlyContinue
    }
    foreach ($line in ($text -split "`n")) {
        if ($line.StartsWith('password=')) { return $line.Substring(9).Trim() }
    }
    throw 'Git has no credential for github.com; push once so Git Credential Manager signs in, then run this again'
}

[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$headers = @{
    Authorization          = 'Bearer ' + (Get-GitHubToken)
    Accept                 = 'application/vnd.github+json'
    'X-GitHub-Api-Version' = '2022-11-28'
    'User-Agent'           = 'toolbox-publish'
}
$api = "https://api.github.com/repos/$Owner/$Repo"

function Invoke-GitHub([string]$method, [string]$uri, $body = $null) {
    $arguments = @{ Method = $method; Uri = $uri; Headers = $headers; TimeoutSec = 120 }
    if ($null -ne $body) {
        $arguments.Body = [System.Text.Encoding]::UTF8.GetBytes(($body | ConvertTo-Json -Depth 5))
        $arguments.ContentType = 'application/json; charset=utf-8'
    }
    Invoke-RestMethod @arguments
}

function Get-StatusCode($errorRecord) {
    $response = $errorRecord.Exception.Response
    if ($null -ne $response) { return [int]$response.StatusCode }
    return 0
}

function Send-Asset($release, [System.IO.FileInfo]$file, [string]$contentType) {
    foreach ($asset in @($release.assets | Where-Object { $_.name -eq $file.Name })) {
        Invoke-GitHub 'Delete' "$api/releases/assets/$($asset.id)" | Out-Null
    }
    $upload = ($release.upload_url -replace '\{.*$', '') + '?name=' + [Uri]::EscapeDataString($file.Name)
    Write-Host ("uploading {0} ({1:N0} MB)" -f $file.Name, ($file.Length / 1MB))
    Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $contentType -InFile $file.FullName -TimeoutSec 3600 | Out-Null
}

$existing = $null
try { $existing = Invoke-GitHub 'Get' "$api/releases/tags/$Tag" }
catch { if ((Get-StatusCode $_) -ne 404) { throw "GitHub refused the request ($(Get-StatusCode $_)): $($_.Exception.Message)" } }

function Send-Files($release) {
    foreach ($package in $packages) { Send-Asset $release $package 'application/zip' }
    Send-Asset $release (Get-Item $SumsPath) 'text/plain'
}

if ($null -ne $existing) {
    if (-not $Replace) {
        throw "release $Tag already exists. Change VERSION and package again for a new one, or run with -Replace to re-upload this one."
    }
    if ($existing.PSObject.Properties['immutable'] -and $existing.immutable) {
        throw "release $Tag is immutable: GitHub accepts no changes to its files, and its tag cannot be used for another release. Change VERSION and package again."
    }
    Step "Replacing the files of $Tag"
    $release = Invoke-GitHub 'Patch' "$api/releases/$($existing.id)" @{ name = $title; body = $notes; prerelease = [bool]$Prerelease }
    $release = Invoke-GitHub 'Get' "$api/releases/$($existing.id)"
    Send-Files $release
}
else {
    # A draft is not found by its tag, only in the list.
    $draft = @(Invoke-GitHub 'Get' "$api/releases?per_page=100") | Where-Object { $_.draft -and $_.tag_name -eq $Tag } | Select-Object -First 1
    if ($null -ne $draft) {
        Step "Carrying on with the draft of $Tag"
        $release = Invoke-GitHub 'Patch' "$api/releases/$($draft.id)" @{ name = $title; body = $notes; prerelease = [bool]$Prerelease }
        $release = Invoke-GitHub 'Get' "$api/releases/$($draft.id)"
    }
    else {
        Step "Creating release $Tag (as a draft until its files are attached)"
        # An annotated tag made here and pushed, so the local clone has it too.
        & git -C $Root rev-parse -q --verify "refs/tags/$Tag" 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { Invoke-Git tag -a $Tag -m $title $head | Out-Null }
        Invoke-Git push -q origin "refs/tags/$Tag" | Out-Null
        $release = Invoke-GitHub 'Post' "$api/releases" @{
            tag_name = $Tag; name = $title; body = $notes; draft = $true; prerelease = [bool]$Prerelease
        }
    }
    Send-Files $release
    # Published last: nothing can be added to an immutable release.
    Step "Publishing $Tag"
    $release = Invoke-GitHub 'Patch' "$api/releases/$($release.id)" @{ draft = $false }
}

$published = Invoke-GitHub 'Get' "$api/releases/tags/$Tag"
$names = @($published.assets | ForEach-Object { $_.name })
$missing = @(@($packages | ForEach-Object { $_.Name }) + 'SHA256SUMS' | Where-Object { $names -notcontains $_ })
if ($missing.Count -gt 0) { throw "release $Tag is out, but GitHub does not list: $($missing -join ', ')" }
Write-Host "published: $($published.html_url) ($($names.Count) files)"