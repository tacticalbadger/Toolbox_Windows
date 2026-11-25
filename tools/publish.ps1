<#
.SYNOPSIS
    Publishes the packages built by "tools\build.ps1 package" as a GitHub
    Release on this repository.

.DESCRIPTION
        tools\publish.ps1 [-Replace] [-Prerelease] [-DryRun]

    Tag v<version> (from the VERSION file). Attached, for each of win-arm64
    and win-x64 from build\release: the installer
    (Toolbox-<version>-win-<arch>-setup.exe), the disc image (.iso) and the
    zip that installed copies download to update themselves; then SHA256SUMS
    and the update feed (update.json, update.json.sig from "tools\build.ps1
    feed"). So the app can be installed from the repo's Releases page, and
    installed copies are offered the version. Publishing is the moment they
    start being offered it.

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
    throw "no packages for $Version in build\release; run: tools\build.ps1 core assets package (and again with -Arch for the other architecture), then tools\build.ps1 feed"
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

# What people install from: an installer and a disc image for each
# architecture, made by the same package step as the zip beside them (it
# removes all three before it builds, so none is left over from another
# build).
$installers = @()
$images = @()
foreach ($rid in 'win-arm64', 'win-x64') {
    $setup = Join-Path $Out "Toolbox-$Version-$rid-setup.exe"
    $image = Join-Path $Out "Toolbox-$Version-$rid.iso"
    foreach ($path in $setup, $image) {
        if (-not (Test-Path $path)) { throw "no $(Split-Path -Leaf $path) in build\release; run: tools\build.ps1 core package -Arch $($rid -replace 'win-', '')" }
        $file = Get-Item $path
        $actual = (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if (-not $expected.ContainsKey($file.Name) -or $expected[$file.Name] -ne $actual) {
            throw "$($file.Name) does not match SHA256SUMS; run the package step again"
        }
    }
    # The installer carries the build number in its own version.
    $setupVersion = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
    if ($setupVersion.FilePrivatePart -ne $commitCount) {
        throw "$(Split-Path -Leaf $setup) is build $($setupVersion.FilePrivatePart), but the current commit is number $commitCount. Package again from this commit."
    }
    $installers += Get-Item $setup
    $images += Get-Item $image
    Write-Host ("{0}  {1:N0} MB  build {2}" -f (Split-Path -Leaf $setup), ((Get-Item $setup).Length / 1MB), $setupVersion.FilePrivatePart)
    Write-Host ("{0}  {1:N0} MB" -f (Split-Path -Leaf $image), ((Get-Item $image).Length / 1MB))
}

# The update feed. Installed copies read update.json from the newest release:
# a release without it, or with one that is not about exactly these files,
# stops updates for everyone until the next release, and nobody is told.
$FeedPath = Join-Path $Out 'update.json'
$FeedSignaturePath = "$FeedPath.sig"
if (-not (Test-Path $FeedPath) -or -not (Test-Path $FeedSignaturePath)) {
    throw 'no update feed in build\release; run: tools\build.ps1 feed (after packaging both architectures)'
}
$feed = [System.IO.File]::ReadAllText($FeedPath, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
if ($feed.version -ne $Version -or [int]$feed.build -ne $commitCount) {
    throw "update.json is for $($feed.version) build $($feed.build), not $Version build $commitCount; run: tools\build.ps1 feed"
}
foreach ($rid in 'win-arm64', 'win-x64') {
    $listed = $feed.packages.$rid
    $package = $packages | Where-Object { $_.Name -eq "Toolbox-$Version-$rid.zip" } | Select-Object -First 1
    if ($null -eq $listed -or $null -eq $package) { throw "the release needs a package for $rid, and update.json has to list it" }
    if ($listed.file -ne $package.Name -or [long]$listed.size -ne $package.Length -or $listed.sha256 -ne $expected[$package.Name]) {
        throw "update.json does not describe $($package.Name) as it is now; run: tools\build.ps1 feed"
    }
}
# Signed with the key whose public half is in the app's source, which is the key these packages trust.
& (Join-Path $PSScriptRoot 'update-key.ps1') verify -File $FeedPath

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

$notes = @"
Toolbox is a Windows client for SSH, RDP and file transfer, with split panes, workspace tabs, a credential vault and 1Password and Active Directory integration.

## Install

Pick the files for your PC: **win-x64** for most PCs, **win-arm64** for Windows on ARM (Settings > System > About shows the system type).

- **The installer** (``Toolbox-$Version-win-<arch>-setup.exe``): download it and run it. It installs Toolbox for you alone, asks for no administrator, adds it to the Start menu, and can be removed again under Settings > Apps > Installed apps.
- **The disc image** (``Toolbox-$Version-win-<arch>.iso``): double-click it and Windows opens it as a disc. On it are the same installer and a **Toolbox** folder; run the installer, or copy the folder to a place of your own (for example ``%LOCALAPPDATA%\Programs``) and run **Toolbox.exe** from the copy. Eject the disc afterwards.
- The ``.zip`` files are what installed copies download to update themselves. They hold the same folder, for whoever wants it that way.

If you already have Toolbox 0.3.0 or newer, there is nothing to download: it offers this version by itself, or at once under **Help > Check for Updates**. It looks once a day, shows what changed, and installs when you agree. It can only do that in a folder it may write to, which is where the installer puts it.

This build is **not code-signed yet**. If Windows shows "Windows protected your PC", choose **More info**, then **Run anyway**. The .NET runtime is inside; nothing else needs installing. Requires Windows 10 version 2004 or newer, or Windows 11; on Windows 10 the Microsoft Edge WebView2 Runtime must be present (it is part of Windows 11).

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
    Write-Host "files: $((@($installers) + @($images) + @($packages) | ForEach-Object { $_.Name }) -join ', '), SHA256SUMS, update.json, update.json.sig"
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

# Attaches a file to a release that is still a draft. A file the draft
# already holds whole (GitHub gives its SHA-256) is left as it is, so a run
# that carries on with a draft does not send everything again; anything else
# of that name, a broken-off upload included, is removed first. An upload
# that breaks off is tried again: the first one of 0.3.1, some 800 MB in
# all, lost its connection part-way.
function Send-Asset($release, [System.IO.FileInfo]$file, [string]$contentType) {
    $upload = ($release.upload_url -replace '\{.*$', '') + '?name=' + [Uri]::EscapeDataString($file.Name)
    $digest = 'sha256:' + (Get-FileHash $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    for ($attempt = 1; ; $attempt++) {
        # Windows PowerShell hands a list from GitHub over as one object: taken apart here.
        $there = @(Invoke-GitHub 'Get' "$api/releases/$($release.id)/assets?per_page=100" | ForEach-Object { $_ } | Where-Object { $_.name -eq $file.Name })
        $whole = @($there | Where-Object { $_.state -eq 'uploaded' -and $_.PSObject.Properties['digest'] -and $_.digest -eq $digest })
        if ($whole.Count -gt 0) {
            Write-Host "already there, and the same file: $($file.Name)"
            return
        }
        foreach ($asset in $there) { Invoke-GitHub 'Delete' "$api/releases/assets/$($asset.id)" | Out-Null }
        Write-Host ("uploading {0} ({1:N0} MB)" -f $file.Name, ($file.Length / 1MB))
        try {
            Invoke-RestMethod -Method Post -Uri $upload -Headers $headers -ContentType $contentType -InFile $file.FullName -TimeoutSec 3600 | Out-Null
            return
        }
        catch {
            if ($attempt -ge 4) { throw }
            Write-Host "the upload broke off ($($_.Exception.Message)); trying again"
            Start-Sleep -Seconds 10
        }
    }
}

$existing = $null
try { $existing = Invoke-GitHub 'Get' "$api/releases/tags/$Tag" }
catch { if ((Get-StatusCode $_) -ne 404) { throw "GitHub refused the request ($(Get-StatusCode $_)): $($_.Exception.Message)" } }

function Send-Files($release) {
    foreach ($installer in $installers) { Send-Asset $release $installer 'application/octet-stream' }
    foreach ($image in $images) { Send-Asset $release $image 'application/x-iso9660-image' }
    foreach ($package in $packages) { Send-Asset $release $package 'application/zip' }
    Send-Asset $release (Get-Item $SumsPath) 'text/plain'
    # Last: from the moment the release is out, these two tell installed copies about it.
    Send-Asset $release (Get-Item $FeedPath) 'application/json'
    Send-Asset $release (Get-Item $FeedSignaturePath) 'text/plain'
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
    # A draft is not found by its tag, only in the list. Windows PowerShell
    # hands a list from GitHub over as one object, not release by release;
    # sent through a script block it comes apart into its releases.
    $releases = @(Invoke-GitHub 'Get' "$api/releases?per_page=100" | ForEach-Object { $_ })
    $draft = $releases | Where-Object { $_.draft -and $_.tag_name -eq $Tag } | Select-Object -First 1
    if ($null -ne $draft) {
        Step "Carrying on with the draft of $Tag"
        # The tag is named in every change to a draft. The one time a draft
        # was carried on with (0.3.1) it was changed without, and came out
        # published under a placeholder tag ("untagged-..."), where no
        # installed copy looks for the packages.
        $release = Invoke-GitHub 'Patch' "$api/releases/$($draft.id)" @{ tag_name = $Tag; name = $title; body = $notes; prerelease = [bool]$Prerelease }
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
    # Published last: nothing can be added to an immutable release, and its
    # tag cannot be changed. So the draft is looked at once more first: it
    # must still be a draft of this tag, with every file whole.
    $ready = Invoke-GitHub 'Get' "$api/releases/$($release.id)"
    if ($ready.tag_name -ne $Tag) {
        throw "the draft is for the tag '$($ready.tag_name)', not $Tag. Not published. Delete the draft on the Releases page and run this again."
    }
    $unfinished = @($ready.assets | Where-Object { $_.state -ne 'uploaded' } | ForEach-Object { $_.name })
    if ($unfinished.Count -gt 0) { throw "the draft holds files that did not arrive whole ($($unfinished -join ', ')). Not published; run this again." }
    Step "Publishing $Tag"
    $latest = if ($Prerelease) { 'false' } else { 'true' }
    $release = Invoke-GitHub 'Patch' "$api/releases/$($release.id)" @{ draft = $false; tag_name = $Tag; make_latest = $latest }
    if ($release.tag_name -ne $Tag) {
        throw "GitHub published the release under the tag '$($release.tag_name)', not $Tag ($($release.html_url)). Installed copies look for the packages under $Tag and will not find them: this needs putting right at once (see CLAUDE.md, the 0.3.1 release)."
    }
}

$published = Invoke-GitHub 'Get' "$api/releases/tags/$Tag"
$names = @($published.assets | ForEach-Object { $_.name })
$missing = @(@(@($installers) + @($images) + @($packages) | ForEach-Object { $_.Name }) + 'SHA256SUMS' + 'update.json' + 'update.json.sig' | Where-Object { $names -notcontains $_ })
if ($missing.Count -gt 0) { throw "release $Tag is out, but GitHub does not list: $($missing -join ', ')" }
Write-Host "published: $($published.html_url) ($($names.Count) files)"