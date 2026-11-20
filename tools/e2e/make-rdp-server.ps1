<#
.SYNOPSIS
    Builds the RDP test server the end-to-end checks connect to.

.DESCRIPTION
    This repository has no RDP server: the Windows app uses Microsoft's own
    client control, and IronRDP left the build with the port. The Mac
    repository still carries the in-process IronRDP test server its tests
    use, so this script fetches those few files from the public Mac
    repository at one pinned commit (each checked against a SHA-256 digest),
    wraps them in a small crate and builds it.

        tools\e2e\make-rdp-server.ps1 [-Dir <folder>]     default: build\e2e-rdp (gitignored)

    The first build downloads and compiles IronRDP: about three minutes.
    tools\e2e\servers.ps1 starts the result when it is there:

        127.0.0.1:3390   demo / secret, plain graphics, honours resize requests,
                         pastes whatever the client announces on its clipboard
        127.0.0.1:3391   the same with the graphics pipeline (H.264)

    Options of the server itself: --port <n>, --graphics-pipeline, --paste,
    --copy-text <text>. It logs sizes and counts, never clipboard contents.
    Its certificate is self-signed, so the app shows the certificate warning
    first.
#>
[CmdletBinding()]
param([string]$Dir)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2

$Root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not $Dir) { $Dir = Join-Path $Root 'build\e2e-rdp' }

# The Mac repository at Toolbox 0.3.0, and the IronRDP revision it pins.
$MacCommit = '63eaabd5eca92a4674c609010be2a11a6ca40082'
$MacRaw = "https://raw.githubusercontent.com/tacticalbadger/Toolbox/$MacCommit"
$IronRdp = '0a79fd2178c79b3d8920b6e76f2a9da5db6331ca'
$Files = @(
    @{ From = 'core/toolbox-core/src/rdp_test_server.rs'; To = 'src\rdp_test_server.rs'
       Sha256 = '41fc25b7903b4f5f8c99c73a8e85cb3e91dac67d9fed3bd37056bc3aa7d814bb' },
    @{ From = 'core/toolbox-core/examples/rdp_server.rs'; To = 'src\main.rs'
       Sha256 = '218f3c73924fbae9a1d615a2e0617387bacc3382b75de394eb514118074d410b' },
    @{ From = 'core/toolbox-core/tests/data/quadrants.h264'; To = 'tests\data\quadrants.h264'
       Sha256 = '15c0640af09933f62e5fd110c81bc97cdf1223cd05acb9d84900364f146e7b3e' },
    # Keeps the dependencies at the versions the Mac build was tested with.
    @{ From = 'core/Cargo.lock'; To = 'Cargo.lock'
       Sha256 = '7d0abca843e252486babde4a4f8b54dd667a801ac06b0e7e7ebb696896f9af61' }
)

$extra = @(
    (Join-Path $env:USERPROFILE '.cargo\bin'),
    'C:\Program Files\LLVM\bin',
    'C:\Program Files\CMake\bin'
) | Where-Object { (Test-Path $_) -and (($env:PATH -split ';') -notcontains $_) }
if ($extra) { $env:PATH = ($extra -join ';') + ';' + $env:PATH }
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) { throw 'cargo not found. Install Rust: winget install Rustlang.Rustup' }

New-Item -ItemType Directory -Force (Join-Path $Dir 'src'), (Join-Path $Dir 'tests\data') | Out-Null
$Dir = (Resolve-Path $Dir).Path
$stamp = Join-Path $Dir 'source.txt'
$wanted = "$MacCommit $IronRdp"

if (-not ((Test-Path $stamp) -and ((Get-Content $stamp -Raw).Trim() -eq $wanted))) {
    Write-Host "==> fetching the test server's source (Mac repository at $($MacCommit.Substring(0, 7)))"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    foreach ($file in $Files) {
        $target = Join-Path $Dir $file.To
        Invoke-WebRequest -Uri "$MacRaw/$($file.From)" -OutFile $target -UseBasicParsing
        $actual = (Get-FileHash $target -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $file.Sha256) {
            Remove-Item $target -Force
            throw "$($file.From): SHA-256 mismatch (got $actual). Refusing to use it."
        }
    }
    # The example names the library it came from.
    $main = Join-Path $Dir 'src\main.rs'
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    $text = [System.IO.File]::ReadAllText($main, $utf8).Replace('toolbox_core::rdp_test_server', 'rdpsrv::rdp_test_server')
    [System.IO.File]::WriteAllText($main, $text, $utf8)
    [System.IO.File]::WriteAllText((Join-Path $Dir 'src\lib.rs'), "pub mod rdp_test_server;`n", $utf8)

    $manifest = @"
# Written by tools\e2e\make-rdp-server.ps1; not part of the app.
[package]
name = "rdpsrv"
version = "0.0.0"
edition = "2021"
publish = false

[workspace]

[dependencies]
ironrdp = { git = "https://github.com/Devolutions/IronRDP", rev = "$IronRdp", features = ["connector", "session", "graphics", "input", "svc", "dvc", "displaycontrol", "cliprdr", "pdu", "core", "server", "acceptor"] }
ironrdp-egfx = { git = "https://github.com/Devolutions/IronRDP", rev = "$IronRdp" }
ironrdp-server = { git = "https://github.com/Devolutions/IronRDP", rev = "$IronRdp", features = ["egfx"] }
tokio = { version = "1", features = ["rt-multi-thread", "net", "io-util", "sync", "time", "macros"] }
rcgen = "0.14"
tempfile = "3"
async-trait = "0.1"
"@
    [System.IO.File]::WriteAllText((Join-Path $Dir 'Cargo.toml'), $manifest.Replace("`r`n", "`n") + "`n", $utf8)
    Set-Content -Path $stamp -Value $wanted -Encoding ascii
}

Write-Host '==> building the RDP test server (the first build takes a few minutes)'
Push-Location $Dir
try {
    cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build failed (exit code $LASTEXITCODE)" }
}
finally { Pop-Location }

$exe = Join-Path $Dir 'target\release\rdpsrv.exe'
if (-not (Test-Path $exe)) { throw "the build did not produce $exe" }
Write-Host "ok: $exe"
Write-Host 'next: tools\e2e\servers.ps1 start; tools\e2e\launch.ps1 --connect rdp-local'
