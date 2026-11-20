# Toolbox (Windows) — working notes for Claude

Read this first on any machine. It is the handoff between sessions and
machines; memory files do not travel, this file does.
**Update the "Current state" section at the end in every commit** (write it
before committing, so the commit describes itself).

## What this is

Toolbox is a replacement for Termius + Royal TS for the owner (infra admin
in a Windows/Active Directory shop; uses Termius, Royal TS/TSX and
1Password daily; prefers Rust). SSH terminals in tabbed workspaces with
split panes, RDP and VNC with dynamic resolution, connection objects in a
Royal-style folder tree, a credential vault, SSH key identities, and
dynamic folders fed by 1Password (`op` CLI) and Active Directory. Royal TS
reference material the owner provided is in `docs/royalts-reference/`. The
owner's Royal layout to mirror: document "Work" → folder "ACME" → dynamic
folders "1Password" and the AD provider (OU hierarchy), folder "Jump
Boxes".

It began as a macOS app (Swift/AppKit shell over a Rust core, repo
https://github.com/tacticalbadger/Toolbox, public since 2026-11-19 with its
history rewritten). On 2026-11-17 the owner decided to port it to Windows.
This repo (https://github.com/tacticalbadger/Toolbox_Windows.git, private) is
the Windows app. **One branch: `main`.** Commit and push to `main`
directly; no feature branches unless the owner asks. Commit only when the
owner asks.

**The Mac app is the behavioural reference, and the goal is feature
parity** (owner, 2026-11-19). Two places to read it:
- Commit `3462de1` in this repo is a snapshot from 2026-11-16:
  `git show 3462de1:App/Sources/Workspace/PaneView.swift`,
  `git ls-tree -r --name-only 3462de1 App/`.
- The Mac repo itself has moved on since (RDP sizing fix, release
  pipeline, identifier scrub). Clone it somewhere outside this repo to
  compare; do not add it as a remote here. Its README "What works" is the
  parity checklist; its `core/` is the same crate minus the Windows
  changes.

Swift tree → C# tree: `App/` (delegate, menu) → `App.xaml.cs`,
`MainWindow*.cs`; `Workspace/` → `Workspaces/`; `Terminal/` → `Terminal/`;
`Remote/` → `Remote/`; `Files/` → `Files/`; `Sidebar/` → `Sidebar/`;
`Model/` → `Model/`.

## Decisions (owner, 2026-11-17 and 2026-11-18, unless marked)

- **Shell: WPF on .NET 10 (LTS), C#.** A thin native shell over the Rust
  core, the same shape as the Mac build. Bindings come from
  `uniffi-bindgen-cs` (NordSecurity; v0.12.0 targets UniFFI 0.32.0), so
  the core keeps UniFFI 0.32. Rejected: Tauri (no mstscax, web look),
  WinUI 3 (cannot host mstscax cleanly), pure-Rust GUIs.
- **RDP: Microsoft's `mstscax` ActiveX control** hosted through
  `WindowsFormsHost`, as Royal TS does. `rdp.rs`, the RDP test server and
  IronRDP left the build.
- **VNC: a Rust VNC crate inside the core** (chosen over the C#
  MarcusW.VncClient to keep protocol code in Rust; the Rust VNC ecosystem
  is thin, expect to patch a crate, and macOS hosts need Apple's ARD
  authentication). Frames cross the FFI into a WPF `WriteableBitmap`.
  **Not started.**
- **Terminal: xterm.js inside WebView2.** (Claude's choice, not contested.)
- **Vault key: DPAPI; owner check: Windows Hello.** The 32-byte key is a
  per-user DPAPI blob next to the vault. **Claude added a fallback on
  2026-11-19, flagged to the owner, not yet confirmed:** where Hello is not
  set up (the dev machine), the Windows Security dialog asks for the
  account password and Windows verifies it, as the Mac build falls back to
  the login password. See the security review addendum of that date.
- **Audience: the owner plus coworkers soon.** x64 and ARM64 builds, an
  installer (MSIX or Inno Setup, not yet chosen) and a code-signing
  certificate are in scope. Since 2026-11-19 there are unsigned,
  self-contained zips for both architectures on the repo's Releases page
  (owner asked for a release that day); installer and certificate are not
  started.
- **Data: start fresh on Windows.** Nothing migrated from the Mac.
- Claude's choices, open to change: one window with a shared sidebar and
  workspaces as tabs inside it (Windows has no native window tabs);
  closing the last workspace leaves a fresh one; app shortcuts on
  Ctrl+Shift / Alt+Shift (README has the table) because Ctrl+letter
  belongs to the terminal; RDP certificate problems show the standard
  Windows warning; drives and printers are not redirected; `VERSION` file
  at 0.1.0.
- Paths: app data in
  `%APPDATA%\Toolbox\{connections.json,vault.json,vault.key,settings.json}`;
  log and WebView2 profile in `%LOCALAPPDATA%\Toolbox`; `known_hosts`
  shared with Windows OpenSSH at `%USERPROFILE%\.ssh\known_hosts`; SSH
  agent is the named pipe `\\.\pipe\openssh-ssh-agent` (1Password's agent
  serves the same pipe); `op.exe` looked up on `PATH`,
  `%LOCALAPPDATA%\Microsoft\WinGet\Links` and
  `C:\Program Files\1Password CLI`; scripts in PowerShell.

## Layout

- `core/toolbox-core/` — Rust core (edition 2021, UniFFI 0.32): `ssh.rs`
  (russh), `sftp.rs` (russh-sftp), `vault.rs` (XChaCha20-Poly1305),
  `onepassword.rs` (`op` subprocess), `ad.rs` (ldap3), `ftp.rs` (FTP /
  explicit FTPS via suppaftp), `store.rs` (connections/credentials/
  providers/folders JSON document), `display.rs` (`rdp_desktop_size`),
  `error.rs`; `vnc.rs` to be added. Test servers behind the `test-support`
  feature: `ssh_test_server.rs`, `ftp_test_server.rs` (libunftp); examples
  `echo_server` (SSH+SFTP :2222) and `ftp_server` (`--port`, `--tls`), all
  demo/secret. `uniffi.toml` names the C# namespace (`Toolbox.Core`) and
  the class for free functions (`CoreApi`).
- `app/Toolbox/` — the WPF project (`net10.0-windows10.0.22621.0`, WPF +
  Windows Forms for the ActiveX host, Fluent theme via `ThemeMode`).
  `App.xaml(.cs)` startup and launch arguments; `MainWindow.xaml(.cs)`
  menus, tab row, workspaces, shortcuts; `MainWindow.Sessions.cs` opening
  sessions, editor and folder actions, testing aids. `Workspaces/`
  (`PaneTree` + `PaneLayout`, `PaneContainer`, `PaneView`, `Workspace`,
  `DragController`, `IPaneContent`), `Terminal/` (`TerminalPane`,
  `Theme`), `Files/` (`FileBrowserPane`, `FileBrowserModel`), `Remote/`
  (`RdpPane`, `RdpClientHost`), `Sidebar/` (`SidebarView`, `SidebarNode`,
  the four editors, `FormBuilder`), `Model/` (`AppModel`, `VaultManager`,
  `OwnerCheck`, `CredentialResolver`, `ProviderSync`, the drafts),
  `Support/` (`AppPaths`, `AppSettings`, `Log`, `Prompts`, `Icons`),
  `Assets/Terminal/` (the terminal page; `xterm/` is fetched).
  `app/Generated/` and `app/runtimes/` come from the core build;
  `app/nuget.config` pins nuget.org.
- `tools/build.ps1` — targets `core`, `assets`, `app`, `test`, `run`,
  `package`, `clean`. `tools/publish.ps1` — GitHub Release from the
  packages (the counterpart of the Mac's `make publish`). `tools/e2e/` —
  `make-fixture.ps1`, `servers.ps1`, `launch.ps1`, `capture.ps1`,
  `ui.ps1` (UI Automation and synthetic input).
- `VERSION` holds the marketing version; the build number is the commit
  count (`FileVersion` 0.1.0.N, shown in Help ▸ About and the log's first
  line).
- `docs/` — `SECURITY-REVIEW.md` (an addendum per security-relevant
  change), `release-notes/<version>.md` (the "In this version" bullets of
  a release; written for teammates, no personal details), Royal TS
  reference.

## Dev machine and toolchain

Windows 11 Pro on **ARM64** (Rust host `aarch64-pc-windows-msvc`),
domain-joined, **200 % display scaling**, the account is not an
administrator. Installed 2026-11-17 via winget: Git 2.56, rustup with
stable 1.100, Visual Studio 2022 Build Tools 17.14 with the C++ workload,
.NET SDK 10.0.404, `uniffi-bindgen-cs` from tag `v0.12.0+v0.32.0`, LLVM 23
and CMake 4.4. **clang and cmake are mandatory on ARM64** (`aws-lc-sys`,
`ring`). WebView2 runtime ships with Windows 11. Not installed: Node
(xterm.js is fetched by `build.ps1`), `gh`, PowerShell 7, cargo-audit,
1Password CLI.

Things that bite on this machine:
- `C:\Program Files\LLVM\bin` is not on PATH; `build.ps1` and
  `servers.ps1` add it. For a bare `cargo` call, add it yourself.
- The user's NuGet config has no package sources; `app/nuget.config`
  supplies nuget.org.
- **Windows Hello is not set up** (`UserConsentVerifier` reports
  DeviceNotPresent), so the owner check always takes the password path
  here and the Hello path has never run.
- The harness runs Windows PowerShell 5.1 (`&&` is a parse error) and Git
  Bash. Keep scripts ASCII: 5.1 reads BOM-less files as ANSI. `2>&1` on a
  native command turns stderr lines into error records.
- Git Bash here is an x64 program running under emulation, and so is a
  `powershell.exe` started from it: `$env:PROCESSOR_ARCHITECTURE` says
  AMD64 there while cargo still builds for ARM64. A build started that
  way once put an ARM64 DLL in the x64 slot. `build.ps1` and `launch.ps1`
  now read the machine-level value, and `core` checks the header of the
  DLL it built; start the build scripts from PowerShell all the same.
- **Never pipe the output of something that leaves a process running**
  unless that process was started through the shell: a child started with
  inherited handles keeps the pipe open and the caller hangs until it
  exits. `launch.ps1` and `servers.ps1` start their processes with
  `UseShellExecute` for this reason.
- Bash `cd` and PowerShell `Set-Location` persist in the harness; use
  absolute paths.

## Everyday commands

```powershell
tools\build.ps1                 # core + assets + app (first run restores packages and fetches xterm.js)
tools\build.ps1 app -Restart    # rebuild the app, close the running build, start the new one
tools\build.ps1 test            # cargo test --features test-support
tools\build.ps1 run
cd core; cargo clippy --features test-support --all-targets     # needs clang + cmake on PATH
```

Run `core` after ANY Rust API change (bindings and DLL are generated).
After every app build, relaunch the app and tell the owner which build is
running; they once tested a stale instance and reported a missing feature.

Release flow (the order matters: the build number is the commit count and
`publish.ps1` checks it against the current commit):

```powershell
# bump VERSION if needed, add docs\release-notes\<version>.md, update Current state
git commit ...; git push origin main
tools\build.ps1 core assets package            # win-arm64 zip
tools\build.ps1 core package -Arch x64         # win-x64 zip (cross-compiled; runs here under emulation)
tools\e2e\launch.ps1 -Exe build\release\Toolbox-<version>-win-x64\Toolbox.exe --connect echo   # test what ships
tools\publish.ps1 -DryRun                      # then without -DryRun
```

## End-to-end checks (no real hosts needed)

```powershell
tools\e2e\make-fixture.ps1            # keys, known_hosts, fake op.cmd, store, settings -> build\e2e
tools\e2e\servers.ps1 start           # SSH+SFTP :2222, FTP :2121, FTPS :2122 (-ExitAfter 6 for auto-close)
tools\e2e\launch.ps1 --connect echo --connect echo-1p     # relaunch against the fixture
tools\e2e\capture.ps1                 # PNG of every Toolbox window (and Windows Security dialogs) -> build\e2e\shots
tools\e2e\ui.ps1 tree                 # also: find, invoke, expand, select, setvalue, click, clickat, drag, keys, text
tools\e2e\servers.ps1 stop
```

Fixture ids: `echo` (key file, folder Jump Boxes), `echo-1p` (1Password
credential through the fake `op.cmd`), `echo-folder` (folder credential →
prompts as `folderuser`, which the echo server rejects: the failure case),
`rdp-local` (127.0.0.1:3390), `vnc-local`, File Transfer connections
`sftp-local`, `ftp-local`, `ftps-local` (self-signed, so it prompts), plus
AD-style generated connections under `ACME/CORP-AD/…`. The fixture has its
own vault, vault key, log (`build\e2e\toolbox.log`) and settings with the
owner check off; `make-fixture.ps1` resets them.

How things are checked: assertions go through the log (`ssh: connected`,
`layout: …`, `vault: …`, `prompt answered: …`) and the automation tree
(`ui.ps1 find -Id RdpStatus`); screenshots are for looks. Automation ids:
`SidebarTree` items are `conn:<id>`, `all-conn:<id>`, `cred:<id>`,
`folder:<path>`, `prov:<id>`; prompts `PromptTitle`, `PromptInput`,
`PromptOk`, `PromptCancel`; editors `EditorSave`, `EditorCancel`,
`Validation`, fields by property name (`Name`, `Host`, `AuthMethod`,
`FamilyRdp`…); toolbar `ToolSplitRight` etc.; file browser `Files…`.
`invoke`/`setvalue`/`select` touch neither mouse nor keyboard; `click`,
`drag`, `keys`, `text` are real input on the owner's desktop. System file
dialogs do not answer to patterns: click the field, `text`, then
`keys '{ENTER}'` (the folder picker also needs `click -Name 'Select
Folder'`). Drop-downs: `click -Id …` then `keys '{DOWN}{ENTER}'`.

**RDP needs a server.** None is in this repo. On 2026-11-19 the Mac repo's
`rdp_test_server.rs` was copied into a scratch crate outside the repo
(`ironrdp` at the Mac repo's pinned revision with `server`, `acceptor`,
`displaycontrol`; `rcgen`, `tempfile`, `async-trait`, `tokio`), built
(about six minutes) and run on :3390 (demo / secret, self-signed, so the
certificate warning appears first). It does not sign out, so the
clean-end path of RDP is untested.

## Conventions

- Commit only when the owner asks, on `main`. Commit messages end with
  the co-author line for the Claude model doing the work.
- Before committing: scan the diff for keys/tokens, make sure no temporary
  probe code is left, run `tools\build.ps1 test`, and update **Current
  state**. Never commit `core/target/`, `build/`, `app/**/bin|obj`,
  `app/Generated/`, `app/runtimes/` or `app/Toolbox/Assets/Terminal/xterm/`.
- **Nothing of the owner's own setup goes in git** (the Mac repo's rule
  since its scrub, applied here from 2026-11-19): no real hostnames,
  domain names, usernames, account emails or employer. Use
  `corp.example`, `CORP\jdoe`, `ACME`, demo/secret in docs, tests and
  fixtures, and write "the owner" in these notes. The history was
  rewritten on 2026-11-19 so that this holds for every commit (see
  Current state). The same goes for what is handed out: `build.ps1`
  keeps the build machine's paths out of the binaries (see the gotchas).
- Keep `README.md` ("What works", "Not there yet") and
  `docs/SECURITY-REVIEW.md` current.
- Secrets never go in `connections.json`; they live in the vault,
  1Password, or are prompted for. New store fields need `#[serde(default)]`.
- Scripts are PowerShell (`.ps1`, CRLF per `.gitattributes`, ASCII);
  everything else is LF.

## Invariants and gotchas (each cost real time)

Carried over from the Mac:
- The echo test server must skip SFTP channels or it echoes SFTP INIT bytes.
- Session end: `SshListener.on_closed(clean)`; a pane auto-closes 0.7 s
  after a clean end only. Failures keep the pane with the message.
- `AuthKind` variants: `Password`, `KeyFile`, `Agent`, `Credential`,
  `StoredPassword` (secret in the vault under the connection id),
  `FolderCredential` (nearest folder's assignment). Credential-based
  methods take the credential's username.
- `ConnectionKind`: `Ssh`, `Rdp`, `Vnc`, and File Transfer kinds `Sftp`,
  `Ftp`, `Ftps`. The file browser talks to `SftpSession` or `FtpSession`
  through `IFileTransferSession`; the two core objects must keep identical
  method signatures. `UsesSsh` (ssh, sftp) decides key/agent auth; FTP
  uses username + password unsplit.
- AD provider accounts typed into the provider are vault credentials with
  id `<providerId>#bind|admin|user`, folder = the provider's folder, tag
  `provider-account`. Saving a provider writes them before the provider
  and deletes ones no longer used; deleting the provider deletes them.
- Every DC in the AD domain used for testing presents a 1024-bit RSA
  LDAPS certificate (checked from the Mac 2026-11-16); verified LDAPS
  cannot work there until they are reissued. The working provider uses
  "Accept untrusted certificates".
- Anything that removes a pane must fix the focused-pane id first
  (`PaneContainer.ClosePane` does; `FocusedPane` repairs stale focus).
- RDP desktop sizes go through `rdp_desktop_size` in the core (edges
  200…8192, height even, width a multiple of 4).

Found on Windows:
- **Static field order.** C# initialises static fields in file order.
  `AppSettings.Shared = Load()` ran before the JSON options below it
  existed, so settings were silently never read back. Anything a static
  initialiser needs goes above it.
- **Airspace.** The terminal (WebView2, another process) and the RDP
  control are native windows: nothing of WPF draws over them and their
  mouse input never reaches WPF. Hence: panes are direct children of
  `PaneContainer`, positioned from the tree (a layout change never
  re-parents a session); the drop highlight is its own click-through
  window; drags use mouse capture, not OLE drag and drop; clicks in the
  RDP control are seen in `ComponentDispatcher.ThreadFilterMessage`, and
  the terminal page reports its own focus; the RDP host is only visible
  while a session is up, because status text cannot overlay it.
- A drag source must capture the mouse on button-down. The pane header is
  26 px high; without capture a quick drag leaves it before it counts.
- WebView2 raises accelerator keys (Ctrl/Alt combinations) to WPF before
  the page sees them, so window-level `KeyBinding`s work with a terminal
  focused. Plain Ctrl+letter must stay unbound.
- A `ComboBox` loses its selection when its `ItemsSource` is replaced and
  writes null back through a two-way binding. The connection editor's
  Method drop-down is wired by hand in `Refresh` for that reason; draft
  choice lists are fixed instances.
- A `ListView` in `GridView` mode with an `ItemContainerStyle` based on
  the plain `ListViewItem` style loses its columns under the Fluent theme
  (rows show `ToString()`). Do not set one.
- uniffi-bindgen-cs must run from inside the crate folder (it calls
  `cargo metadata`). Generated enums with data are nested records
  (`AuthKind.Credential`), so those names shadow the top-level records
  inside the enum type only.
- mstscax: use `MsRdpClient9NotSafeForScripting`
  (`8B918B82-7985-4C24-89DF-C33AD2BBFBCD`) through `dynamic`; events
  through `AxHost.ConnectionPointCookie` and a public `[ComVisible]` sink
  for the dispatch interface `336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6`. The
  IronRDP test server never raises `OnLoginComplete`, and
  `OnRemoteDesktopSizeChange` is how the real size is learned. Ask for a
  new size only when the pane's wanted size differs from the last size
  asked for, or a server that answers with another size is asked forever.
- mstscax paints its waiting screen white whatever colours the host has,
  so `RdpPane` keeps the host `Hidden` (laid out, not shown) until
  `OnConnected` and shows its own status text meanwhile.
- The x64 core cross-compiles from ARM64 with the installed MSVC tools;
  `aws-lc-sys` wants NASM for x64 and `build.ps1` sets
  `AWS_LC_SYS_PREBUILT_NASM=1` when there is none. An x64 build runs on
  this machine under emulation only when self-contained (no x64 .NET
  runtime is installed), which is what `package` produces.
- **Build paths end up in the DLL.** Rust puts source paths in panic
  messages and aws-lc's C code uses `__FILE__`; both start with the
  profile folder (the account name), the C ones under its 8.3 spelling
  because aws-lc-sys hands short paths to the compiler. `build.ps1 core`
  rewrites them: `--remap-path-prefix` through `CARGO_ENCODED_RUSTFLAGS`,
  and `AWS_LC_SYS_CFLAGS_<target>` (flags for that crate only) with
  `/clang:-ffile-prefix-map=…` for clang-cl on ARM64 and `/d1trimfile:…`
  for cl on x64. clang-cl ignores a bare `-ffile-prefix-map`. `core`
  warns and `package` refuses when a binary still names the profile
  folder. A bare `cargo build --release` does none of this, and changing
  the flags rebuilds every crate.
- The virtual host for the terminal page is under `.example`; other
  made-up names can cost a DNS lookup on every load.
- `e2e\ui.ps1` must not tap Alt to get the foreground: it opens the menu
  bar and eats the next key.

## Not verified against real infrastructure

A real Windows RDP host (sign-out detection, CredSSP against a domain,
clipboard), a real SSH host with the Windows agent or 1Password's agent
(the `ssh-agent` service is disabled here), the real `op.exe`, an AD
domain controller from this machine, Windows Hello, completing the
password owner check, an encrypted key file's passphrase prompt, private
keys from the vault, the x64 build on x64 hardware (it has run under
emulation here), and a coworker actually installing a release.

## Parity with the Mac app

Done and exercised against the local test servers on 2026-11-19: tabbed
workspaces, split panes with kept divider ratios, pane and sidebar drag
and drop, move pane to a workspace, automatic close after a clean end,
SSH terminals with themes and text size, SFTP/FTP/FTPS browser (list,
new folder, upload, download, untrusted-certificate prompt), RDP with
dynamic resolution, sidebar tree with filter, the four editors, folder
credentials, stored passwords, vault on DPAPI with the owner check,
1Password provider sync through a fake `op`, provider-owned accounts,
launch arguments, build script, harness.

Missing, in suggested order:
1. **VNC** (`vnc.rs` in the core + a test server + `VncPane`).
2. Code signing and an installer. Zips and `publish.ps1` exist (the Mac
   side has Developer ID signing, notarization and a DMG); without a
   certificate every recipient gets the SmartScreen warning. The x64
   package has only run under emulation on the ARM64 dev machine.
3. App icon (`.ico` from the Mac drawing in `bridge/make-icon.swift` at
   `3462de1`).
4. Sidebar multi-selection (the Mac deletes and drags several at once).
5. A `Toolbox.Tests` project (`PaneTree`, `PaneLayout`, the drafts,
   `CredentialResolver.Split`); `build.ps1 test` already runs it if it
   exists.
6. Smaller: File menu wording says "Browse Files" (Mac: "Browse Files
   (SFTP)"); file rename/delete from the browser were not driven by a
   test; sidebar rows do not ellipsize long detail text; terminal output
   has no back-pressure for very fast producers; OSC 52 clipboard writes
   are not supported (the Mac allows them).

## Ideas not started

Session restore (workspaces + layouts), broadcast input, jump hosts, port
forwards, a Tasks concept like Royal's, per-connection RDP options
(drives, printers, gateway), Kerberos (current-user) binds for the AD
provider, binding the vault key to a Windows Hello credential.

## Current state (update in every commit)

As of 2026-11-19, on `main`.

This change set (after 0.1.0, no app code changed): `build.ps1` keeps
build paths out of the binaries, reads the machine's architecture
instead of the shell's and checks the DLL it built; `launch.ps1` takes
its app arguments when typed at a PowerShell prompt (the documented form
only worked through `powershell -File`); these notes and the README
follow the history rewrite and the GitHub account's new name; one
security review addendum covers the identifiers. Checked: 38 Rust tests;
both rebuilt DLLs searched for the profile folder (none); the ARM64
build and a scratch self-contained x64 build (under emulation) run
against the fixture with SSH, SFTP and FTP connecting. No package was
rebuilt: `build\release` still holds the published 0.1.0 files.

What 0.1.0, the first Windows app and its first release, consisted of:
- Core, ported to Windows (2026-11-18): Unix-only code behind
  `cfg(unix)` with Windows counterparts (permissions, SFTP test server
  file I/O, SSH agent over the named pipe, home directory, `op.exe`
  lookup); IronRDP and `rdp.rs` removed. Added on 2026-11-19:
  `display.rs` with `rdp_desktop_size` (from the Mac repo's fix), the Mac
  repo's identifier scrub in tests and examples (demo/secret, ACME,
  corp.example), the `staticlib` crate type dropped, `uniffi.toml` for
  C#. 38 tests pass, clippy clean.
- App: everything under `app/` is new. See "Parity with the Mac app" for
  what exists and what was exercised.
- Tools: `build.ps1` (with `package`), `publish.ps1`, the e2e harness.
- Docs: README rewritten, two security review addenda (the app; packaged
  builds), release notes for 0.1.0, these notes.

Released: **v0.1.0 (build 4) is on the Releases page** since 2026-11-19
as two self-contained, unsigned zips (win-arm64, win-x64) with
SHA256SUMS, built from the commit the annotated tag points at (`beea2b6`
since the rewrite below); GitHub's digests match the local checksums.
Both packages were run against the fixture and test servers before
publishing: ARM64 natively, x64 under emulation (terminal, FTP and SFTP
browser, vault, the RDP control loading). No x64 hardware has run it.
`publish.ps1` needed one fix during the run (commit `e80fdf7`, after the
tag).

**Both repos had their history rewritten on 2026-11-19**, after 0.1.0
was out (owner: the real hostname must not be in the repos):
- Mac repo (public): the internal hostname in the test data of
  `onepassword.rs` became `nutanix1.corp.example` in every commit.
  `main` (now `a244c52`) and `v0.2.0` (now `1c9af1d`) were force-pushed;
  the 0.2.0 release kept its files. **A clone from before that, the Mac
  itself included, must run `git fetch origin` and `git reset --hard
  origin/main` before any other work, and must never push its old
  history**, or the hostname is public again. The Mac repo's own notes do
  not say this yet.
- This repo: the two oldest commits lost the hostname, the AD domain
  names, a domain account, a private address, the profile folder name
  and the owner's first name (the usual placeholders instead), and the
  first three commits got the no-reply address. All five commit ids
  changed (the Mac snapshot is now `3462de1`, the release commit
  `beea2b6`); `v0.1.0` moved with its commit, and the release kept its
  files and its build number, since the commit count is the same.
  Any other clone of this repo needs the same fetch and reset.
- GitHub keeps commits that nothing points at for some time: whoever
  has an old commit id can still open it there until GitHub collects
  it. GitHub Support removes them on request.

Since the same day `build.ps1` keeps the build machine's paths out of
the binaries (see the gotchas). **The 0.1.0 downloads predate that**:
their `toolbox_core.dll` names the build account's profile folder a few
hundred times (no hostname, no secret). The repo is private and the
owner chose to leave 0.1.0 as it is; the next release is clean, and
`package` now refuses a build that is not.

The GitHub account was renamed: both repos are under `tacticalbadger`
(found on 2026-11-19 when a push answered "this repository moved"). The
old addresses redirect; this clone's `origin` and the links in these
notes and the README use the new one. `publish.ps1` takes the owner from
`origin`, so a clone that still has the old address must be updated
before publishing (`git remote set-url origin …`).

For the owner to decide or try:
- The password fallback of the owner check (see Decisions). Try one vault
  unlock with the real account password; turn it off in the Vault menu if
  it is not wanted.
- A code-signing certificate, so coworkers stop seeing SmartScreen.
- Reset the Mac's clone to the rewritten history before working there
  (see above), and consider asking GitHub Support to remove the Mac
  repo's old, now unreachable commits.
- The Mac repo's notes and README still link to the GitHub account's old
  name; the links redirect, but only while nobody else takes that name.

Next: VNC, then signing and an installer (see the list above).
