# Toolbox for Windows

A client for the servers you touch every day: SSH sessions in split panes
and workspace tabs (the part Termius gets right), plus RDP and VNC with a
credential vault, identities and 1Password and Active Directory sync (the
part Royal TS gets right). Rust for the connection engine, C# and WPF for
the app.

This is the Windows port of the macOS Toolbox app. **The port is in
progress; nothing runs on Windows yet.** The Mac app is preserved in this
repository's history (commit `e2d77a4`) as the reference for the port.
`CLAUDE.md` holds the decisions, machine setup and current state.

## Layout

```
core/     Rust crate toolbox-core: SSH (russh), SFTP, FTP/FTPS, the vault,
          1Password and Active Directory providers, the connection store.
          Exposed to C# through UniFFI.
app/      (to be created) C# WPF application: workspaces, panes, terminal
          (xterm.js in WebView2), RDP (Microsoft's mstscax control), VNC,
          file browser, sidebar tree, editors.
tools/    (to be created) PowerShell build and end-to-end scripts.
docs/     Security review and Royal TS reference material.
```

## Dependencies

| What | Version known to work | Install |
|---|---|---|
| Windows 11 (ARM64 or x64) | 10.0.26100 | |
| Git | 2.56 | `winget install Git.Git` |
| Rust (rustup, stable) | 1.100 | `winget install Rustlang.Rustup` |
| Visual Studio 2022 Build Tools, C++ workload | 17.14 (MSVC 14.44) | `winget install Microsoft.VisualStudio.2022.BuildTools` with the `VCTools` workload |
| .NET SDK | 10.0 | `winget install Microsoft.DotNet.SDK.10` |
| LLVM (clang) and CMake | 23 / 4.4 | `winget install LLVM.LLVM Kitware.CMake` (required on ARM64 by the crypto crates behind russh) |
| uniffi-bindgen-cs | v0.12.0+v0.32.0 | `cargo install uniffi-bindgen-cs --git https://github.com/NordSecurity/uniffi-bindgen-cs --tag v0.12.0+v0.32.0` |

Optional: 1Password CLI (`winget install AgileBits.1Password.CLI`, then
enable CLI integration in the 1Password app) for 1Password providers.

## Build

Until `tools/build.ps1` exists, only the Rust core can be exercised:

```powershell
cd core\toolbox-core
cargo test --features test-support
```

## Security

`docs/SECURITY-REVIEW.md` records the review findings and residual risks
from the Mac build; addenda follow each security-relevant change in the
port (vault key storage, SSH agent, credential handling).
