<#
.SYNOPSIS
    Builds Toolbox for Windows.

.DESCRIPTION
    tools\build.ps1 [targets...] [-Configuration Release|Debug] [-Arch arm64|x64]

    Targets (default: core assets app):
      core     Rust core (release) -> app\runtimes\win-<arch>\native\toolbox_core.dll
               and the UniFFI C# bindings -> app\Generated\toolbox_core.cs
      assets   xterm.js (pinned versions, SHA-512 checked) -> app\Toolbox\Assets\Terminal\xterm
      app      the WPF app -> build\app\win-<arch>\Toolbox.exe
      test     Rust tests (in-process SSH, SFTP and FTP servers) and the .NET tests
      run      start the built app
      package  self-contained build for handing out, under build/release, for
               one architecture: an installer (-setup.exe, made with Inno
               Setup), a disc image (.iso) holding the installer and the
               app's folder, the same folder as a zip for copies that
               update themselves, and SHA256SUMS (needs "core" for that
               architecture, and "assets", first)
      feed     update.json and its signature under build/release: what
               installed copies read to find this version (needs both
               packages, and the update key: tools\update-key.ps1)
      clean    remove build output, generated bindings and fetched assets

    Run "core" again after any change to the Rust API: the bindings and the
    DLL are generated, not in git. "app" refuses to overwrite a build that is
    running; pass -Restart to close that instance and start the new build.

    The version comes from the VERSION file; the build number is the commit
    count. tools\publish.ps1 puts the packages on the GitHub Releases page.

.EXAMPLE
    tools\build.ps1                 # everything, for this machine's architecture
    tools\build.ps1 app -Restart    # rebuild the app and relaunch it
    tools\build.ps1 core app -Arch x64
    tools\build.ps1 core assets package            # a package for this machine's architecture
    tools\build.ps1 core package -Arch x64         # and one for the other
    tools\build.ps1 feed                           # then the signed update feed for both
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, ValueFromRemainingArguments = $true)]
    [string[]]$Targets,
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [ValidateSet('', 'arm64', 'x64')]
    [string]$Arch = '',
    [switch]$Restart
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent $PSScriptRoot
$Core = Join-Path $Root 'core'
$App = Join-Path $Root 'app'
$Build = Join-Path $Root 'build'

if (-not $Targets -or $Targets.Count -eq 0) { $Targets = @('core', 'assets', 'app') }
$known = @('core', 'assets', 'app', 'test', 'run', 'package', 'feed', 'clean')
foreach ($t in $Targets) {
    if ($known -notcontains $t) { throw "unknown target '$t' (expected: $($known -join ', '))" }
}

# The machine's architecture, not this process's: an x64 PowerShell running
# under emulation on ARM64 (Git Bash starts one) reports AMD64 for itself,
# while cargo still builds for ARM64 when no target is named.
$machine = [Environment]::GetEnvironmentVariable('PROCESSOR_ARCHITECTURE', 'Machine')
if (-not $machine) { $machine = $env:PROCESSOR_ARCHITECTURE }
$hostArch = if ($machine -eq 'ARM64') { 'arm64' } else { 'x64' }
if (-not $Arch) { $Arch = $hostArch }
$Rid = "win-$Arch"
$Triple = if ($Arch -eq 'arm64') { 'aarch64-pc-windows-msvc' } else { 'x86_64-pc-windows-msvc' }
$OutDir = Join-Path $Build "app\$Rid"
$Exe = Join-Path $OutDir 'Toolbox.exe'
$Version = (Get-Content (Join-Path $Root 'VERSION') -Raw).Trim()

# New-DiscImage and Compare-DiscImage, for the package.
. (Join-Path $PSScriptRoot 'disc-image.ps1')

# A shell opened before the toolchain was installed does not have these on
# PATH yet. clang and cmake are required by the crypto crates on ARM64.
$extra = @(
    (Join-Path $env:USERPROFILE '.cargo\bin'),
    'C:\Program Files\LLVM\bin',
    'C:\Program Files\CMake\bin',
    'C:\Program Files\dotnet'
) | Where-Object { (Test-Path $_) -and (($env:PATH -split ';') -notcontains $_) }
if ($extra) { $env:PATH = ($extra -join ';') + ';' + $env:PATH }

function Step([string]$text) { Write-Host "==> $text" -ForegroundColor Cyan }

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

function Assert-Tool([string]$name, [string]$hint) {
    if (-not (Get-Command $name -ErrorAction SilentlyContinue)) { throw "$name not found. $hint" }
}

# Instances of the app started from this build's output folder.
function Get-RunningBuild {
    Get-Process -Name 'Toolbox' -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path -ieq $Exe) } catch { $false }
    }
}

# The 8.3 spelling of a folder, which is how aws-lc-sys names its sources to
# the C compiler. Where short names are switched off the path comes back
# unchanged.
function Get-ShortPath([string]$path) {
    try { return (New-Object -ComObject Scripting.FileSystemObject).GetFolder($path).ShortPath }
    catch { return $path }
}

# Compilers bake source paths into the DLL: panic locations in Rust, __FILE__
# in the crypto library's C code. They all start with the profile folder,
# which is the account name, and the DLL is handed to other people. So the
# prefixes are rewritten: cargo's folder becomes "cargo", this checkout
# "toolbox". Returns the environment variables that make cargo do it.
function Get-PathScrubEnvironment {
    $cargoHome = if ($env:CARGO_HOME) { $env:CARGO_HOME } else { Join-Path $env:USERPROFILE '.cargo' }
    $cargoHome = $cargoHome.TrimEnd('\')
    $separator = [string][char]0x1f

    $rustFlags = @()
    if ($env:CARGO_ENCODED_RUSTFLAGS) { $rustFlags += @($env:CARGO_ENCODED_RUSTFLAGS -split $separator) }
    elseif ($env:RUSTFLAGS) { $rustFlags += @($env:RUSTFLAGS -split '\s+' | Where-Object { $_ }) }
    $rustFlags += "--remap-path-prefix=$cargoHome=cargo"
    $rustFlags += "--remap-path-prefix=$Root=toolbox"

    # aws-lc-sys compiles with clang-cl for ARM64 and with cl for x64, and
    # takes flags of its own, so no other crate's C build sees these. A C
    # flag cannot carry a space; the short spelling never has one.
    $spellings = @(@($cargoHome, (Get-ShortPath $cargoHome)) | Select-Object -Unique | Where-Object { $_ -notmatch '\s' })
    $cFlags = @()
    foreach ($spelling in $spellings) {
        if ($Arch -eq 'arm64') { $cFlags += "/clang:-ffile-prefix-map=$spelling=cargo" }
        else { $cFlags += "/d1trimfile:$spelling\" }
    }
    $cName = 'AWS_LC_SYS_CFLAGS_' + $Triple.Replace('-', '_')
    $existing = [Environment]::GetEnvironmentVariable($cName, 'Process')
    if ($existing) { $cFlags = @($existing) + $cFlags }

    return @{
        CARGO_ENCODED_RUSTFLAGS = ($rustFlags -join $separator)
        $cName                  = ($cFlags -join ' ')
    }
}

# Which processor a DLL or EXE is for, from its header.
function Get-PeArchitecture([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $machine = [BitConverter]::ToUInt16($bytes, [BitConverter]::ToInt32($bytes, 0x3c) + 4)
    if ($machine -eq 0xAA64) { return 'arm64' }
    if ($machine -eq 0x8664) { return 'x64' }
    return ('0x{0:X4}' -f $machine)
}

# The files among these that still name this machine's profile folder, in
# either spelling, as text or as UTF-16.
function Find-PrivatePaths([string[]]$files) {
    $leaves = @((Split-Path -Leaf $env:USERPROFILE), (Split-Path -Leaf (Get-ShortPath $env:USERPROFILE))) | Select-Object -Unique
    $needles = @()
    foreach ($leaf in $leaves) {
        foreach ($slash in '\', '/') {
            $needle = "Users$slash$leaf"
            $needles += $needle
            $needles += ($needle.ToCharArray() -join [string][char]0) + [string][char]0
        }
    }
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)
    $found = @()
    foreach ($file in $files) {
        $text = $latin1.GetString([System.IO.File]::ReadAllBytes($file))
        foreach ($needle in $needles) {
            if ($text.IndexOf($needle, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
                $found += (Split-Path -Leaf $file)
                break
            }
        }
    }
    return $found
}

function Build-Core {
    Assert-Tool 'cargo' 'Install Rust: winget install Rustlang.Rustup'
    Assert-Tool 'uniffi-bindgen-cs' 'cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0'
    Step "core: cargo build --release ($Triple)"
    $scrub = Get-PathScrubEnvironment
    $before = @{}
    foreach ($name in $scrub.Keys) {
        $before[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $scrub[$name], 'Process')
    }
    Push-Location $Core
    try {
        if ($Arch -eq $hostArch) {
            Invoke-Native 'cargo build' { cargo build --release --lib -p toolbox-core }
            $dll = Join-Path $Core 'target\release\toolbox_core.dll'
        }
        else {
            Invoke-Native 'rustup target add' { rustup target add $Triple }
            # The crypto library behind russh wants NASM for x64 Windows and
            # carries prebuilt objects for machines without it.
            if ($Arch -eq 'x64' -and -not (Get-Command nasm -ErrorAction SilentlyContinue)) {
                $env:AWS_LC_SYS_PREBUILT_NASM = '1'
            }
            Invoke-Native 'cargo build' { cargo build --release --lib -p toolbox-core --target $Triple }
            $dll = Join-Path $Core "target\$Triple\release\toolbox_core.dll"
        }
    }
    finally {
        Pop-Location
        foreach ($name in $scrub.Keys) { [Environment]::SetEnvironmentVariable($name, $before[$name], 'Process') }
    }
    # A DLL for the wrong processor would only fail when the app loads it.
    $built = Get-PeArchitecture $dll
    if ($built -ne $Arch) { throw "cargo built a DLL for $built where $Arch was asked for ($dll)" }
    if (@(Find-PrivatePaths @($dll)).Count -gt 0) {
        Write-Warning 'toolbox_core.dll still names this machine''s profile folder; "package" will refuse it. See Get-PathScrubEnvironment.'
    }

    $native = Join-Path $App "runtimes\$Rid\native"
    New-Item -ItemType Directory -Force $native | Out-Null
    Copy-Item $dll (Join-Path $native 'toolbox_core.dll') -Force

    # Bindings come from the metadata embedded in the DLL (UniFFI library
    # mode). The generator asks cargo about the crate, so it runs from there.
    Step 'core: C# bindings'
    $generated = Join-Path $App 'Generated'
    New-Item -ItemType Directory -Force $generated | Out-Null
    Push-Location (Join-Path $Core 'toolbox-core')
    try {
        Invoke-Native 'uniffi-bindgen-cs' { uniffi-bindgen-cs --library $dll --config uniffi.toml --no-format --out-dir $generated }
    }
    finally { Pop-Location }
    Write-Host "ok: $native\toolbox_core.dll"
    Write-Host "ok: $generated\toolbox_core.cs"
}

# xterm.js and its addons, straight from the npm registry. Versions and
# SHA-512 digests are pinned here; a tarball that does not match is refused.
$XtermPackages = @(
    @{ Name = 'xterm'; Version = '6.0.0'; Url = 'https://registry.npmjs.org/@xterm/xterm/-/xterm-6.0.0.tgz'
       Sha512 = 'TQwDdQGtwwDt+2cgKDLn0IRaSxYu1tSUjgKarSDkUM0ZNiSRXFpjxEsvc/Zgc5kq5omJ+V0a8/kIM2WD3sMOYg=='
       Files = @('package\lib\xterm.js', 'package\css\xterm.css', 'package\LICENSE') },
    @{ Name = 'addon-fit'; Version = '0.11.0'; Url = 'https://registry.npmjs.org/@xterm/addon-fit/-/addon-fit-0.11.0.tgz'
       Sha512 = 'jYcgT6xtVYhnhgxh3QgYDnnNMYTcf8ElbxxFzX0IZo+vabQqSPAjC3c1wJrKB5E19VwQei89QCiZZP86DCPF7g=='
       Files = @('package\lib\addon-fit.js') },
    @{ Name = 'addon-web-links'; Version = '0.12.0'; Url = 'https://registry.npmjs.org/@xterm/addon-web-links/-/addon-web-links-0.12.0.tgz'
       Sha512 = '4Smom3RPyVp7ZMYOYDoC/9eGJJJqYhnPLGGqJ6wOBfB8VxPViJNSKdgRYb8NpaM6YSelEKbA2SStD7lGyqaobw=='
       Files = @('package\lib\addon-web-links.js') },
    @{ Name = 'addon-webgl'; Version = '0.19.0'; Url = 'https://registry.npmjs.org/@xterm/addon-webgl/-/addon-webgl-0.19.0.tgz'
       Sha512 = 'b3fMOsyLVuCeNJWxolACEUED0vm7qC0cy4wRvf3oURSzDTYVQiGPhTnhWZwIHdvC48Y+oLhvYXnY4XDXPoJo6A=='
       Files = @('package\lib\addon-webgl.js') },
    @{ Name = 'addon-search'; Version = '0.16.0'; Url = 'https://registry.npmjs.org/@xterm/addon-search/-/addon-search-0.16.0.tgz'
       Sha512 = '9OeuBFu0/uZJPu+9AHKY6g/w0Czyb/Ut0A5t79I4ULoU4IfU5BEpPFVGQxP4zTTMdfZEYkVIRYbHBX1xWwjeSA=='
       Files = @('package\lib\addon-search.js') }
)

function Get-Sha512Base64([string]$path) {
    $sha = [System.Security.Cryptography.SHA512]::Create()
    $stream = [System.IO.File]::OpenRead($path)
    try { [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $stream.Dispose(); $sha.Dispose() }
}

# Whether the fetched terminal assets are the ones listed above. A build
# with an older set would start, and its terminal page would stop at the
# first script it cannot find.
function Test-AssetsCurrent {
    $stamp = Join-Path $App 'Toolbox\Assets\Terminal\xterm\versions.txt'
    $wanted = ($XtermPackages | ForEach-Object { "$($_.Name)@$($_.Version) sha512-$($_.Sha512)" }) -join "`n"
    return ((Test-Path $stamp) -and (((Get-Content $stamp -Raw).Trim() -replace "`r", '') -eq $wanted))
}

function Assert-Assets {
    if (-not (Test-AssetsCurrent)) { throw 'the terminal assets are missing or out of date; run: tools\build.ps1 assets' }
}

function Build-Assets {
    $dest = Join-Path $App 'Toolbox\Assets\Terminal\xterm'
    $stamp = Join-Path $dest 'versions.txt'
    $wanted = ($XtermPackages | ForEach-Object { "$($_.Name)@$($_.Version) sha512-$($_.Sha512)" }) -join "`n"
    if (Test-AssetsCurrent) {
        Write-Host "ok: xterm.js assets are current ($dest)"
        return
    }
    Step 'assets: xterm.js'
    $cache = Join-Path $Build 'cache\npm'
    New-Item -ItemType Directory -Force $cache | Out-Null
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force $dest | Out-Null
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    foreach ($p in $XtermPackages) {
        $tgz = Join-Path $cache "$($p.Name)-$($p.Version).tgz"
        if (-not ((Test-Path $tgz) -and ((Get-Sha512Base64 $tgz) -eq $p.Sha512))) {
            Write-Host "downloading $($p.Url)"
            Invoke-WebRequest -Uri $p.Url -OutFile $tgz -UseBasicParsing
        }
        $actual = Get-Sha512Base64 $tgz
        if ($actual -ne $p.Sha512) {
            Remove-Item $tgz -Force
            throw "$($p.Name) $($p.Version): SHA-512 mismatch (got $actual). Refusing to use it."
        }
        $unpacked = Join-Path $cache "$($p.Name)-$($p.Version)"
        if (Test-Path $unpacked) { Remove-Item -Recurse -Force $unpacked }
        New-Item -ItemType Directory -Force $unpacked | Out-Null
        Invoke-Native 'tar' { tar.exe -xzf $tgz -C $unpacked }
        foreach ($f in $p.Files) {
            $name = Split-Path -Leaf $f
            if ($name -eq 'LICENSE') { $name = 'LICENSE-xterm.txt' }
            Copy-Item (Join-Path $unpacked $f) (Join-Path $dest $name) -Force
        }
    }
    Set-Content -Path $stamp -Value $wanted -Encoding ascii
    Write-Host "ok: $dest"
}

function Build-App {
    Assert-Tool 'dotnet' 'Install the .NET SDK: winget install Microsoft.DotNet.SDK.10'
    if (-not (Test-Path (Join-Path $App 'Generated\toolbox_core.cs'))) { throw 'no generated bindings; run: tools\build.ps1 core' }
    if (-not (Test-Path (Join-Path $App "runtimes\$Rid\native\toolbox_core.dll"))) { throw "no core DLL for $Rid; run: tools\build.ps1 core -Arch $Arch" }
    Assert-Assets

    $running = @(Get-RunningBuild)
    if ($running.Count -gt 0) {
        if (-not $Restart) {
            throw "Toolbox is running from $OutDir (pid $($running.Id -join ', ')), so its files are locked. Close it, or pass -Restart to close and relaunch it."
        }
        Step "app: closing the running build (pid $($running.Id -join ', '))"
        $running | Stop-Process -Force
        $running | ForEach-Object { try { $_.WaitForExit(5000) | Out-Null } catch { } }
    }

    $number = Get-BuildNumber
    Step "app: dotnet build ($Configuration, $Rid, $Version build $number)"
    $project = Join-Path $App 'Toolbox\Toolbox.csproj'
    Invoke-Native 'dotnet build' { dotnet build $project -c $Configuration -r $Rid -o $OutDir "-p:ToolboxBuild=$number" --nologo -v minimal }
    Write-Host "ok: $Exe"
    if ($Restart) { Start-App }
}

# The build number is the commit count, so two builds of one version can be told apart.
function Get-BuildNumber {
    $count = 0
    try {
        $text = & git -C $Root rev-list --count HEAD 2>$null
        if ($LASTEXITCODE -eq 0) { $count = [int]$text }
    }
    catch { }
    return $count
}

# The Inno Setup compiler, which makes the installer. Installed for the
# account, with no administrator, by:
#   winget install --id JRSoftware.InnoSetup.7 -e --custom /CURRENTUSER
function Find-InnoSetup {
    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    foreach ($folder in $env:LOCALAPPDATA, $env:ProgramFiles, ${env:ProgramFiles(x86)}) {
        if (-not $folder) { continue }
        foreach ($sub in 'Programs\Inno Setup 7', 'Inno Setup 7') {
            $candidate = Join-Path $folder "$sub\ISCC.exe"
            if (Test-Path $candidate) { return $candidate }
        }
    }
    throw 'the Inno Setup compiler (ISCC.exe) was not found. Install it: winget install --id JRSoftware.InnoSetup.7 -e --custom /CURRENTUSER'
}

# What the disc says of itself, in a file at its top, for whoever opens it
# without the release page in front of them.
function Get-DiscReadMe([string]$setupName) {
    return @(
        "Toolbox $Version for Windows ($Arch)",
        '',
        'To install it, run',
        "    $setupName",
        'from this disc. It installs Toolbox for you alone, asks for no',
        'administrator, and adds it to the Start menu.',
        '',
        'Or, without installing anything: copy the Toolbox folder from this disc',
        'to your PC, for example into %LOCALAPPDATA%\Programs, and run Toolbox.exe',
        'from the copy.',
        '',
        'Either way, eject this disc afterwards. It is not needed again.',
        '',
        'Toolbox is not code-signed. If Windows shows "Windows protected your PC",',
        'choose More info, then Run anyway.',
        '',
        'Your connections, credentials and settings are kept on your PC, under',
        '%APPDATA%\Toolbox, and not in the folder the app runs from.',
        '',
        'Toolbox updates itself from here on: it looks for a new version once a',
        'day, shows what changed, and replaces its own files when you agree. For',
        'that it must be somewhere it can change, which is where the installer',
        'puts it, and what a place of your own is.',
        '',
        'https://github.com/tacticalbadger/Toolbox_Windows'
    )
}

# A folder that runs on a PC with nothing installed (the .NET runtime is
# inside), handed out three ways: an installer, a disc image (Windows opens
# an .iso as a disc when it is double-clicked) holding that installer and the
# folder, and the folder as a zip, which is what a copy that updates itself
# downloads. Nothing in it is code-signed yet, so Windows asks before running
# it the first time.
function New-Package {
    Assert-Tool 'dotnet' 'Install the .NET SDK: winget install Microsoft.DotNet.SDK.10'
    if (-not (Test-Path (Join-Path $App 'Generated\toolbox_core.cs'))) { throw 'no generated bindings; run: tools\build.ps1 core' }
    if (-not (Test-Path (Join-Path $App "runtimes\$Rid\native\toolbox_core.dll"))) { throw "no core DLL for $Rid; run: tools\build.ps1 core -Arch $Arch" }
    Assert-Assets
    $iscc = Find-InnoSetup

    $number = Get-BuildNumber
    $name = "Toolbox-$Version-win-$Arch"
    $release = Join-Path $Build 'release'
    $stage = Join-Path $release $name
    $zip = Join-Path $release "$name.zip"
    $setup = Join-Path $release "$name-setup.exe"
    $iso = Join-Path $release "$name.iso"
    $top = Join-Path $release "$name.top"
    New-Item -ItemType Directory -Force $release | Out-Null
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    if (Test-Path $top) { Remove-Item -Recurse -Force $top }
    # Gone before anything is built: an installer, an image, a zip or a feed
    # left from an earlier build must not sit beside a folder it was not made
    # from.
    Remove-DiscImage -Path $iso
    foreach ($stale in $zip, $setup, (Join-Path $release 'update.json'), (Join-Path $release 'update.json.sig')) {
        if (Test-Path $stale) { Remove-Item -Force $stale }
    }

    Step "package: dotnet publish (self-contained, $Rid, $Version build $number)"
    $project = Join-Path $App 'Toolbox\Toolbox.csproj'
    # Always compiled afresh: an assembly left by the "app" target at the same
    # commit would be reused as it is, debug information and all, whatever
    # this step asks for.
    $intermediate = Join-Path $App 'Toolbox\obj\Release'
    if (Test-Path $intermediate) {
        Get-ChildItem $intermediate -Directory -Recurse -Filter $Rid | ForEach-Object { Remove-Item -Recurse -Force $_.FullName }
    }
    Invoke-Native 'dotnet publish' {
        dotnet publish $project -c Release -r $Rid --self-contained true "-p:ToolboxBuild=$number" `
            -p:ToolboxPackaged=true -p:DebugType=none -p:DebugSymbols=false -o $stage --nologo -v minimal
    }
    foreach ($needed in 'Toolbox.exe', 'toolbox_core.dll', 'WebView2Loader.dll', 'Assets\Terminal\terminal.html', 'Assets\Terminal\xterm\xterm.js', 'Assets\Terminal\xterm\addon-search.js') {
        if (-not (Test-Path (Join-Path $stage $needed))) { throw "the package is missing $needed" }
    }
    # What is built here must not tell the recipient where it was built.
    $telling = @(Find-PrivatePaths @('Toolbox.exe', 'Toolbox.dll', 'toolbox_core.dll' | ForEach-Object { Join-Path $stage $_ }))
    if ($telling.Count -gt 0) {
        throw "$($telling -join ', ') contains a path under this machine's profile folder. Run: tools\build.ps1 core -Arch $Arch (it rewrites those paths), then package again."
    }

    # The list of what this version puts into its folder. An update removes
    # the files the old list names and the new version no longer has.
    $files = @(Get-ChildItem $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length + 1).Replace('\', '/') }) + 'files.txt' | Sort-Object -Unique
    [System.IO.File]::WriteAllText((Join-Path $stage 'files.txt'), (($files -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

    Step "package: $name.zip"
    Invoke-Native 'tar' { tar.exe -a -c -f $zip -C $release $name }

    # The installer: the same folder, put where its user may write, with a
    # Start menu entry and an uninstaller (tools\installer\Toolbox.iss).
    Step "package: $name-setup.exe"
    $script = Join-Path $PSScriptRoot 'installer\Toolbox.iss'
    Invoke-Native 'ISCC' {
        & $iscc --quiet "--define=Version=$Version" "--define=Build=$number" "--define=Arch=$Arch" `
            "--define=Stage=$stage" "--define=OutputDir=$release" "--define=OutputName=$name-setup" $script
    }
    if (-not (Test-Path $setup)) { throw "ISCC made no $name-setup.exe" }
    $made = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
    if ($made.FilePrivatePart -ne $number) { throw "$name-setup.exe says it is build $($made.FilePrivatePart), not $number" }
    # Setup's tables are left uncompressed (see the script), so this reads them.
    if (@(Find-PrivatePaths @($setup)).Count -gt 0) {
        Remove-Item -Force $setup
        throw "$name-setup.exe contains a path under this machine's profile folder."
    }

    # The disc: the installer and a few words at its top, and the app in a
    # folder named Toolbox for whoever would rather copy it.
    Step "package: $name.iso"
    $label = "Toolbox $Version"
    $holds = @{ Toolbox = $stage }
    New-Item -ItemType Directory -Force $top | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $top 'README.txt'), ((Get-DiscReadMe "$name-setup.exe") -join "`r`n") + "`r`n", [System.Text.Encoding]::ASCII)
    Copy-Item -LiteralPath $setup -Destination (Join-Path $top "$name-setup.exe")
    try {
        New-DiscImage -Path $iso -Label $label -Top $top -Folders $holds
        # Read back as Windows reads it, before anyone else does: every file
        # that went in must come out the same, and nothing else.
        $different = @(Compare-DiscImage -Path $iso -Label $label -Top $top -Folders $holds)
    }
    finally {
        Remove-Item -Recurse -Force $top
    }
    if ($different.Count -gt 0) {
        Remove-Item -Force $iso
        throw "the disc image does not hold what was put in it: $(($different | Select-Object -First 5) -join '; ')"
    }
    # The whole image this time, so every file in the package is looked at,
    # and whatever the disc says of itself.
    if (@(Find-PrivatePaths @($iso)).Count -gt 0) {
        Remove-Item -Force $iso
        throw "$name.iso contains a path under this machine's profile folder."
    }

    # Checksums for everything of this version that is there to be handed out.
    $lines = Get-ChildItem $release -File | Where-Object {
        $_.Name -like "Toolbox-$Version-win-*" -and ($_.Extension -in '.zip', '.iso' -or $_.Name -like '*-setup.exe')
    } | Sort-Object Name | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant(), $_.Name
    }
    [System.IO.File]::WriteAllText((Join-Path $release 'SHA256SUMS'), (($lines -join "`n") + "`n"), [System.Text.Encoding]::ASCII)
    foreach ($file in $setup, $iso, $zip) { Write-Host ("ok: {0} ({1:N0} MB)" -f $file, ((Get-Item $file).Length / 1MB)) }
    Write-Host "ok: $(Join-Path $release 'SHA256SUMS')"
}

# What installed copies read to learn about this version: update.json, with
# each package's length and SHA-256 and the release notes, and its signature
# made with the update key. Both architectures have to be packaged, from the
# current commit. Without these two files on a release, installed copies see
# no update; with a wrong signature they refuse it.
function New-Feed {
    $release = Join-Path $Build 'release'
    $number = Get-BuildNumber
    $packages = [ordered]@{}
    foreach ($rid in 'win-arm64', 'win-x64') {
        $name = "Toolbox-$Version-$rid"
        $zip = Join-Path $release "$name.zip"
        $assembly = Join-Path $release "$name\Toolbox.dll"
        if (-not (Test-Path $zip) -or -not (Test-Path $assembly)) {
            throw "no package for $rid; run: tools\build.ps1 core package -Arch $($rid -replace 'win-', '')"
        }
        $built = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($assembly).FilePrivatePart
        if ($built -ne $number) { throw "$name.zip is build $built, but the current commit is number $number. Package again from this commit." }
        $packages[$rid] = [ordered]@{
            file   = "$name.zip"
            size   = (Get-Item $zip).Length
            sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    $notes = ''
    $notesPath = Join-Path $Root "docs\release-notes\$Version.md"
    if (Test-Path $notesPath) { $notes = [System.IO.File]::ReadAllText($notesPath, [System.Text.Encoding]::UTF8).Trim() -replace "`r`n", "`n" }
    $feed = [ordered]@{ format = 1; version = $Version; build = $number; notes = $notes; packages = $packages }
    $path = Join-Path $release 'update.json'
    [System.IO.File]::WriteAllText($path, (($feed | ConvertTo-Json -Depth 5) -replace "`r`n", "`n") + "`n", (New-Object System.Text.UTF8Encoding($false)))

    Step 'feed: signing update.json'
    $keys = Join-Path $PSScriptRoot 'update-key.ps1'
    & $keys check
    & $keys sign -File $path
    & $keys verify -File $path
    Write-Host "ok: $path (version $Version, build $number)"
}

function Start-App {
    if (-not (Test-Path $Exe)) { throw "no build at $Exe; run: tools\build.ps1 app" }
    Step "run: $Exe"
    Start-Process -FilePath $Exe -WorkingDirectory $OutDir | Out-Null
}

function Test-All {
    Assert-Tool 'cargo' 'Install Rust: winget install Rustlang.Rustup'
    Step 'test: cargo test --features test-support'
    Push-Location $Core
    try { Invoke-Native 'cargo test' { cargo test --features test-support } }
    finally { Pop-Location }
    $tests = Join-Path $App 'Toolbox.Tests\Toolbox.Tests.csproj'
    if (Test-Path $tests) {
        Step 'test: dotnet test'
        Invoke-Native 'dotnet test' { dotnet test $tests --nologo -v minimal }
    }
}

function Clear-All {
    Step 'clean'
    $paths = @(
        $Build,
        (Join-Path $Core 'target'),
        (Join-Path $App 'Generated'),
        (Join-Path $App 'runtimes'),
        (Join-Path $App 'Toolbox\bin'),
        (Join-Path $App 'Toolbox\obj'),
        (Join-Path $App 'Toolbox.Tests\bin'),
        (Join-Path $App 'Toolbox.Tests\obj'),
        (Join-Path $App 'Toolbox\Assets\Terminal\xterm')
    )
    foreach ($p in $paths) {
        if (Test-Path $p) { Remove-Item -Recurse -Force $p; Write-Host "removed $p" }
    }
}

foreach ($t in $Targets) {
    switch ($t) {
        'core' { Build-Core }
        'assets' { Build-Assets }
        'app' { Build-App }
        'test' { Test-All }
        'run' { Start-App }
        'package' { New-Package }
        'feed' { New-Feed }
        'clean' { Clear-All }
    }
}
