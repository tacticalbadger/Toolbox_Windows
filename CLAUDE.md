# Toolbox (Windows) — working notes for Claude

Read this first on any machine. It is the handoff between sessions and
machines; memory files do not travel, this file does.
**Update the "Current state" section at the end in every commit** (write it
before committing, so the commit describes itself).

## What this is

Toolbox is a replacement for Termius + Royal TS for the owner (infra admin in
a Windows/Active Directory shop; uses Termius, Royal TS/TSX and 1Password
daily; prefers Rust). SSH terminals in tabbed workspaces with split panes,
RDP and VNC with dynamic resolution, connection objects in a Royal-style
folder tree, a credential vault, SSH key identities, and dynamic folders
fed by 1Password (`op` CLI) and Active Directory. Royal TS reference
material the owner provided is in `docs/royalts-reference/`. The owner's Royal layout
to mirror: document "Work" → folder "AAN" → dynamic folders "1Password" and
the AD provider (OU hierarchy), folder "Jump Boxes".

It began as a macOS app (Swift/AppKit shell over a Rust core, repo
https://github.com/mattdobbs/Toolbox). On 2026-11-17 the owner decided to port
it to Windows and retire the Mac app. This repo
(https://github.com/mattdobbs/Toolbox_Windows.git) is the Windows app.
**One branch: `main`.** Commit and push to `main` directly; no feature
branches unless the owner asks. Commit only when the owner asks.

**The Mac app is the behavioural reference.** It was imported whole in
commit `e2d77a4` and removed in the next commit. To read it:
`git show e2d77a4:App/Sources/Workspace/PaneView.swift`, or
`git ls-tree -r --name-only e2d77a4 App/` for the file list. The Swift
tree: `App/Sources/App/` (delegate, menu), `Workspace/` (window =
workspace tab, pane container, `PaneView`, `PaneLayout`), `Terminal/`
(SwiftTerm), `Remote/` (RDP, VNC), `Files/` (SFTP browser), `Sidebar/`
(tree + editors), `Model/` (AppModel, VaultManager, CredentialResolver,
ProviderSync). The old `README.md` at that commit lists "What works",
which is the parity checklist for the port.

## Decisions (the owner, 2026-11-17 and 2026-11-18)

- **Shell: WPF on .NET 10 (LTS), C#.** A thin native shell over the Rust
  core, the same shape as the Mac build. The owner chose ".NET 8" on the menu;
  .NET 8 left support on 2026-11-10, so the current LTS is used. Bindings
  come from `uniffi-bindgen-cs` (NordSecurity; v0.12.0 targets UniFFI
  0.32.0), so the core keeps UniFFI 0.32. Rejected: Tauri (no mstscax, web
  look), WinUI 3 (cannot host mstscax cleanly), pure-Rust GUIs (no
  terminal or tree widgets worth having).
- **RDP: Microsoft's `mstscax` ActiveX control** hosted through
  `WindowsFormsHost`, as Royal TS does. Clipboard, audio, drive and printer
  redirection, RD Gateway, CredSSP and smart cards come with it. `rdp.rs`,
  the RDP test server and the IronRDP dependency leave the build.
- **VNC: a Rust VNC crate inside the core** (chosen over the C#
  MarcusW.VncClient to keep protocol code in Rust; the Rust VNC ecosystem
  is thin, expect to patch a crate). Frames cross the FFI into a WPF
  `WriteableBitmap`.
- **Terminal: xterm.js inside WebView2.** No native WPF terminal control
  exists. (Claude's choice, not contested.)
- **Vault key: DPAPI; owner check: Windows Hello.** The 32-byte key is a
  per-user DPAPI blob next to the vault; Windows Hello
  (`UserConsentVerifier`) replaces Touch ID with the same "require
  authentication" toggle. No code-signing dependence.
- **Audience: the owner plus coworkers soon.** x64 and ARM64 builds, an
  installer (MSIX or Inno Setup, not yet chosen) and a code-signing
  certificate are in scope.
- **Data: start fresh on Windows.** Nothing migrated from the Mac.
- **Repo: the Mac tree was imported once and deleted**, see above.
- Paths (Claude's choices): app data in
  `%APPDATA%\Toolbox\{connections.json,vault.json,vault.key}`;
  `known_hosts` shared with Windows OpenSSH at
  `%USERPROFILE%\.ssh\known_hosts`; SSH agent is the named pipe
  `\\.\pipe\openssh-ssh-agent` (1Password's agent serves the same pipe);
  `op.exe` looked up on `PATH`, `%LOCALAPPDATA%\Microsoft\WinGet\Links`
  and `C:\Program Files\1Password CLI`; scripts in PowerShell.

## Planned layout

- `core/toolbox-core/` — Rust core (edition 2021, UniFFI 0.32): `ssh.rs`
  (russh), `sftp.rs` (russh-sftp), `vault.rs` (XChaCha20-Poly1305),
  `onepassword.rs` (`op` subprocess), `ad.rs` (ldap3), `ftp.rs` (FTP /
  explicit FTPS via suppaftp), `store.rs` (connections/credentials/
  providers/folders JSON document), `error.rs`; `rdp.rs` to be removed,
  `vnc.rs` to be added. Test servers behind the `test-support` feature:
  `ssh_test_server.rs`, `ftp_test_server.rs` (libunftp); examples
  `echo_server` (SSH+SFTP :2222) and `ftp_server` (`--port`, `--tls`),
  all matt/secret.
- `app/` — C# WPF solution (to be created): `Toolbox.sln`, the WPF
  project, `Generated/` for the UniFFI C# bindings (gitignored).
- `tools/` — PowerShell: `build.ps1` (core → bindings + DLL, app, test,
  package) and the e2e harness (fixture, test servers, launch, capture)
  re-created from the Mac one.
- `docs/` — `SECURITY-REVIEW.md` (add an addendum per security-relevant
  change; the vault and agent changes need one), Royal TS reference.

## Dev machine and toolchain

The owner's Windows machine is **Windows 11 Pro on ARM64** (Rust host
`aarch64-pc-windows-msvc`; winget pulls arm64 installers). Installed
2026-11-17 via winget: Git 2.56 (`C:\Program Files\Git\bin`), rustup with
stable 1.100 (`%USERPROFILE%\.cargo\bin`), Visual Studio 2022 Build Tools
17.14 with the C++ workload (MSVC 14.44, ARM64 and x64 compilers, Windows
SDK 10.0.26100), .NET SDK 10.0.404 (`C:\Program Files\dotnet`),
`uniffi-bindgen-cs` from tag `v0.12.0+v0.32.0`, LLVM 23
(`C:\Program Files\LLVM\bin`) and CMake 4.4 (`C:\Program Files\CMake\bin`).
**clang and cmake are mandatory on ARM64**: the `aws-lc-sys` crate behind
russh and rustls, and `ring` behind rcgen, fail their build scripts
without them. WebView2 runtime ships with Windows 11. The user account is
not an administrator (installers raise UAC).

Shell notes: the Claude harness runs Windows PowerShell 5.1 (`&&` is a
parse error) and Git Bash. A shell opened before an install does not see
new PATH entries; use full paths until relaunched. For x64 builds add the
`x86_64-pc-windows-msvc` Rust target and the .NET `win-x64` runtime
identifier.

## Everyday commands (until build.ps1 exists)

```powershell
cd core\toolbox-core
cargo check --features test-support      # needs clang + cmake on PATH
cargo test  --features test-support
cargo clippy --features test-support
```

## Core port status

`cargo check --features test-support` on Windows (2026-11-17): every
third-party crate compiles; exactly seven errors, all in our code:
`store.rs restrict_permissions` (`std::os::unix`, `from_mode`) and its
test; `ssh_test_server.rs` `FileExt` (`read_at`/`write_all_at`; use
`std::os::windows::fs::FileExt::seek_read`/`seek_write`); `ssh.rs`
agent auth uses `AgentClient::connect_uds`/`connect_env`, which do not
exist on Windows (russh has a named-pipe client under `cfg(windows)`).
Compiles but wrong on Windows: `ssh.rs expand_tilde` reads `HOME` (use
`USERPROFILE`), `onepassword.rs CANDIDATE_PATHS` are Homebrew paths.

## Conventions

- Commit messages end with the co-author line for the Claude model doing
  the work, e.g. `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>`.
- Before committing: scan the diff for keys/tokens, make sure no temporary
  probe code is left, run the Rust tests, and update **Current state**.
  Never commit `core/target/`, `build/`, `app/**/bin|obj`, generated
  bindings or native library copies; binaries are not shared through git.
- Keep `README.md` and `docs/SECURITY-REVIEW.md` current.
- Secrets never go in `connections.json`; they live in the vault,
  1Password, or are prompted for. New store fields need `#[serde(default)]`.
- Scripts are PowerShell (`.ps1`, CRLF per `.gitattributes`); everything
  else is LF.
- After every app build, relaunch the app and tell the owner which build is
  running; he once tested a stale instance and reported a missing feature.

## Invariants and gotchas carried over from the Mac (each cost real time)

- The echo test server must skip SFTP channels or it echoes SFTP INIT bytes.
- Session end: `SshListener.on_closed(clean)`; a pane auto-closes 0.7 s
  after a clean end only. Failures keep the pane with the message.
- `AuthKind` variants: `Password`, `KeyFile`, `Agent`, `Credential`,
  `StoredPassword` (secret in the vault under the connection id),
  `FolderCredential` (nearest folder's assignment; store keeps a path →
  credential map). Credential-based methods take the credential's username.
- `ConnectionKind`: `Ssh`, `Rdp`, `Vnc`, and File Transfer kinds `Sftp`,
  `Ftp`, `Ftps` (explicit `AUTH TLS`). File Transfer connections open the
  file browser, which talks to either `SftpSession` or `FtpSession`
  through one interface; the two core objects must keep identical method
  signatures. `usesSsh` (ssh, sftp) decides key/agent auth; FTP uses
  username + password unsplit.
- AD provider accounts typed into the provider are vault credentials with
  id `<providerId>#bind|admin|user`, folder = the provider's folder, tag
  `provider-account`. Saving a provider writes them before the provider
  and deletes ones no longer used; deleting the provider deletes them.
  Renaming a provider moves everything inside its folder.
- Every DC in the AD domain used for testing presents a 1024-bit RSA LDAPS
  certificate (checked 2026-11-16); macOS refused those and SChannel on
  Windows likely will too, so verified LDAPS cannot work there until the
  AD team reissues them. The working provider uses "Accept untrusted
  certificates". The owner's real setup: `op` account `work`, vault `work`; AD
  binds with his regular domain account.
- Anything that removes a pane must fix the focused-pane id first, before
  layout callbacks fire (the Mac shell crashed on this once).
- Regenerate the bindings after any Rust signature change; the C# bindings
  are generated, not hand-edited.

## Not verified against real infrastructure

A real Windows RDP host, a real VNC server, an AD domain controller from
this machine, Windows Hello, and distribution to coworkers.

## Ideas not started

Session restore (workspaces + layouts), broadcast input, jump hosts, port
forwards, a Tasks concept like Royal's, terminal theme picker, Kerberos
(current-user) binds for the AD provider.

## Current state (update in every commit)

As of 2026-11-18, on `main`.

This commit removes the Mac-only tree (`App/`, `bridge/`, `tools/`,
`Makefile`) imported in `e2d77a4`, rewrites this file and `README.md` for
the Windows port, and sets `.gitignore` for .NET output. The VS Code
Swift extension no longer prompts for a toolchain. The Rust core is
unchanged and still has the seven Windows compile errors listed under
"Core port status"; nothing builds or runs on Windows yet.

Next: fix the core's Unix-only code so `cargo test --features
test-support` passes on Windows, drop `rdp.rs` and IronRDP, then
scaffold `app/` (WPF project, UniFFI C# bindings, build.ps1) and get a
window with the sidebar tree reading `connections.json`.
