# Toolbox — working notes for Claude

Read this first on any machine. It is the handoff between the machines this
app is developed on; memory files do not travel, this file does.
**Update the "Current state" section at the end in every commit** (write it
before committing, so the commit describes itself).

## Windows port (decided 2026-11-17, in progress)

The owner is porting Toolbox to Windows and the macOS app is being retired. The
rest of this file describes the macOS build and stays valid until the
Windows shell reaches parity; then `App/`, `bridge/`, `tools/e2e` (bash)
and the macOS sections here are deleted. Decisions the owner made on 2026-11-17:

- **Shell: WPF on .NET 10 (LTS), C#.** The owner chose ".NET 8" on the menu;
  .NET 8 left support on 2026-11-10, so the current LTS is used. Same
  shape as the Mac build: a native shell
  over the Rust core. Bindings come from `uniffi-bindgen-cs`
  (NordSecurity; v0.12.0 targets UniFFI 0.32.0, released 2026-11-09), so
  the core keeps its UniFFI version. Rejected: Tauri (no mstscax, web
  look), WinUI 3 (cannot host mstscax cleanly), pure-Rust GUIs (no
  terminal or tree widgets worth having).
- **RDP: Microsoft's `mstscax` ActiveX control** hosted through
  `WindowsFormsHost`, as Royal TS does. Clipboard, audio, drive and printer
  redirection, RD Gateway, CredSSP and smart cards come with it. `rdp.rs`
  and the IronRDP dependency leave the Windows build.
- **Vault key: DPAPI, owner check: Windows Hello.** The 32-byte key is a
  per-user DPAPI blob under the app data folder; Windows Hello
  (`UserConsentVerifier`) replaces Touch ID with the same
  "require authentication" toggle. No Keychain, no code-signing dependence.
- **Repo: replace in place.** This repository becomes the Windows app;
  the Mac app survives only in git history. Note the repo is
  https://github.com/mattdobbs/Toolbox_Windows.git, a fresh repository
  whose only commit is a one-line README; the Mac sources were copied in
  untracked. The Mac history stays in the original Toolbox repo.
- **VNC: a Rust VNC crate inside the core** (the owner, 2026-11-18; he chose
  this over the C# MarcusW.VncClient to keep protocol code in Rust).
  RoyalVNCKit is Swift only. Frames cross the FFI into a WPF
  `WriteableBitmap`, the same path RDP used on the Mac.
- **Audience: the owner plus coworkers soon** (2026-11-18). So x64 as well as
  ARM64 builds, an installer (MSIX or Inno Setup, not yet chosen) and a
  code-signing certificate are in scope, not deferred.
- **Data: start fresh on Windows** (2026-11-18). Nothing is migrated from
  the Mac; providers regenerate their contents, manual connections are
  re-created by hand. No vault export feature on the Mac side.
- **Mac sources: imported once, then deleted** (2026-11-18). The first
  real commit in this repo is the Mac tree as-is, so the Swift code stays
  reachable in history as the behavioural reference for the port; the
  next commit removes `App/`, `bridge/`, `tools/` and the Makefile. This
  also stops the VS Code Swift extension prompting for a toolchain, which
  it does whenever `App/Package.swift` exists.
- Follow-on choices made by Claude, not yet contested: terminal is
  xterm.js inside WebView2 (no native WPF terminal control exists);
  app data in `%APPDATA%\Toolbox\{connections.json,vault.json,vault.key}`;
  `known_hosts` shared with Windows OpenSSH at `%USERPROFILE%\.ssh\known_hosts`;
  `op.exe` looked up on `PATH` and in `%LOCALAPPDATA%\Microsoft\WinGet\Links`
  and `C:\Program Files\1Password CLI`; build scripts in PowerShell.

Core changes the port needs, confirmed by `cargo check --features
test-support` on Windows on 2026-11-17 (every third-party crate compiles;
exactly seven errors, all in our code):
`store.rs restrict_permissions` (`std::os::unix` + `from_mode`) and its
test; `ssh_test_server.rs` `FileExt` (`read_at`/`write_all_at`, use
`std::os::windows::fs::FileExt::seek_read`/`seek_write` or a seek + read);
`ssh.rs` agent auth uses `AgentClient::connect_uds`/`connect_env`, which
do not exist on Windows (the Windows OpenSSH agent is the named pipe
`\\.\pipe\openssh-ssh-agent`; russh has a named-pipe client under
`cfg(windows)`). Not compile errors but still wrong on Windows:
`ssh.rs expand_tilde` reads `HOME` (use `USERPROFILE`), and
`onepassword.rs CANDIDATE_PATHS` are Homebrew paths.

The Windows dev machine (`c:\Users\jdoe\Code\Toolbox_Windows`) is
**Windows 11 on ARM64** (host triple `aarch64-pc-windows-msvc`). Installed
2026-11-17 via winget, all per the ARM64 builds: Git 2.56, rustup with
stable 1.100 (`%USERPROFILE%\.cargo\bin`), Visual Studio 2022 Build Tools
17.14 with the C++ workload (MSVC 14.44, ARM64 and x64 compilers, Windows
SDK 10.0.26100), .NET SDK 10.0.404, `uniffi-bindgen-cs` from tag
`v0.12.0+v0.32.0`, plus LLVM 23 (`C:\Program Files\LLVM\bin`) and CMake
4.4 (`C:\Program Files\CMake\bin`): on Windows ARM64 the `aws-lc-sys`
crate that russh and rustls use, and `ring` used by rcgen, only build
with clang and cmake on `PATH`. WebView2 runtime ships with Windows 11. The shell is
Windows PowerShell 5.1 (`&&` is a parse error). A shell opened before the
installs does not see the new PATH entries; use full paths until relaunched.
Anything shipped to coworkers must also build for x64; add the
`x86_64-pc-windows-msvc` Rust target and a .NET `win-x64` runtime
identifier when packaging starts.

## What this is

Toolbox is a macOS native replacement for Termius + Royal TSX for the owner
(infra admin; uses Termius, Royal TSX and 1Password daily; prefers Rust).
SSH terminals in tabbed workspaces with split panes, RDP and VNC with
dynamic resolution, connection objects in a Royal-style folder tree, a
credential vault with Touch ID, SSH key identities, and dynamic folders fed
by 1Password (`op` CLI) and Active Directory. Royal TS reference material
the owner provided is in `docs/royalts-reference/`. The owner's Royal layout to
mirror: document "Work" → folder "AAN" → dynamic folders "1Password" and the
AD provider (OU hierarchy), folder "Jump Boxes".

Repo: https://github.com/mattdobbs/Toolbox.git. **One branch: `main`.**
The owner asked for a single branch on 2026-11-14; commit and push to `main`
directly and do not create feature branches unless he asks. The old
`toolbox-phases-1-3` branch was merged and deleted.

## Starting a session on either Mac

1. `git status` and `git pull` on `main`. If the working tree only shows
   `App/`, `core/` or `build/` as untracked and the source is missing, a GUI
   client (GitKraken did this once) switched to a stale branch: run
   `git checkout main && git pull` and never stage those folders. They hold
   build caches (`core/target` alone is ~14 GB) and `build/e2e` holds test
   private keys.
2. `make core && make app` (core first: bindings and the static library are
   generated, not in git). Then `make test`.
3. Read **Current state** at the end of this file, then carry on.
4. Before ending: update Current state, commit to `main`, push.

## Layout

- `core/toolbox-core/` — Rust core (edition 2021) exposed through UniFFI
  0.32: `ssh.rs` (russh), `sftp.rs` (russh-sftp), `rdp.rs` (IronRDP,
  git-pinned), `vault.rs` (XChaCha20-Poly1305), `onepassword.rs` (`op`
  subprocess), `ad.rs` (ldap3), `ftp.rs` (FTP / explicit FTPS via suppaftp), `store.rs` (connections/credentials/
  providers/folders JSON document), `error.rs`. Test servers behind the
  `test-support` feature: `ssh_test_server.rs`, `rdp_test_server.rs`,
  `ftp_test_server.rs` (libunftp); examples `echo_server` (SSH+SFTP :2222),
  `rdp_server` (:3390) and `ftp_server` (`--port`, `--tls`), all
  matt/secret.
- `App/` — SwiftPM package. AppKit shell: `App/` (delegate, menu),
  `Workspace/` (window = workspace tab, `PaneContainerViewController`,
  `PaneView`, `PaneLayout`), `Terminal/` (SwiftTerm), `Remote/` (RDP via
  IOSurface layer, VNC via RoyalVNCKit), `Files/` (SFTP browser, SwiftUI),
  `Sidebar/` (NSOutlineView tree + SwiftUI editors), `Model/` (AppModel,
  VaultManager, CredentialResolver, ProviderSync). `Sources/Generated/` and
  `Vendor/` are produced by `make core` and gitignored.
- `bridge/` — build scripts: `build-core.sh`, `bundle-app.sh`,
  `signing-identity.sh`, `make-icon.swift`.
- `tools/e2e/` — end-to-end harness (fixture, test servers, launch,
  capture). `docs/SECURITY-REVIEW.md` — review findings and addenda.

## First time on a Mac

The full dependency table is in README.md ("Dependencies"). In short:

1. Apple silicon Mac, macOS 14+. Xcode at `/Applications/Xcode.app`
   (built with 27.2; the SwiftUI macro plugin is borrowed from it, the
   license need not be accepted for `make app`) and the Command Line Tools
   (`xcode-select -p` should print `/Library/Developer/CommandLineTools`;
   the scripts unset `DEVELOPER_DIR`).
2. Rust via rustup, stable (built with 1.100). Network on first build:
   crates.io, IronRDP from GitHub, SwiftTerm and RoyalVNCKit via SwiftPM.
3. Optional: `brew install 1password-cli` (providers), `brew install
   xcodegen` (`make xcode`, needs `sudo xcodebuild -license accept`),
   `cargo install cargo-audit`, `brew install gh`.
4. `make core` then `make app` → `build/Toolbox.app`. `make test` → Rust
   tests (40 as of 2026-11-16). `make run` launches the bundle.
5. `make signing` once (asks for the login password): creates and trusts a
   self-signed "Toolbox Local Signing" certificate so the app's signature
   stays stable across builds and the Keychain's "Always Allow" for the
   vault key sticks. Without it `make app` signs ad hoc and warns; the
   Keychain then asks after every rebuild. A Developer ID identity, if one
   is in the keychain, is picked up automatically (`TOOLBOX_SIGN_IDENTITY`
   overrides).
6. `DEVELOPER_DIR` must be unset for cargo/swift builds; the scripts do it.

App data: `~/Library/Application Support/Toolbox/{connections.json,
vault.json}`; Keychain generic password service `com.dobbs.Toolbox.vault-key`
("Toolbox vault key"); UserDefaults domain `com.dobbs.Toolbox`
(`vaultRequiresAuth`, `vaultKeyOwnerCertificate`, sidebar expansion).
The owner's real setup: `op` at `/opt/homebrew/bin/op`, account `work`, vault
`work`; AD binds with his regular domain account.

## Everyday commands

```sh
make core          # Rust → bindings + static lib (run after ANY Rust API change)
make app           # SwiftPM build + bundle (relaunch the app afterwards and say so)
make test          # cargo test --features test-support
make icon          # re-render the icon
cd core && cargo clippy --features test-support && cargo audit
```

Relaunch the app after every `make app` and tell the owner which build is
running; he once tested a stale instance and reported a missing feature.

## End-to-end checks (no real hosts needed)

```sh
tools/e2e/make-fixture.sh            # keys, known_hosts, fake op, store → build/e2e
tools/e2e/servers.sh start           # SSH+SFTP :2222, RDP :3390, FTP :2121, FTPS :2122 (add --exit-after 6 for auto-close)
tools/e2e/launch.sh --connect echo --connect echo-1p   # relaunch against the fixture, Touch ID skipped
tools/e2e/capture.sh                 # screenshot every Toolbox window into build/e2e/shots, report dialogs
tools/e2e/servers.sh stop
```

Fixture ids: `echo` (key file, folder Jump Boxes), `echo-1p` (1Password
credential through the fake `op`), `echo-folder` (folder credential →
prompts as `folderuser`), `rdp-local`, `vnc-local`, File Transfer
connections `sftp-local`, `ftp-local`, `ftps-local` (self-signed, so it
prompts), plus AD-style generated connections under `AAN/CORP-AD/…`. The
`ad-fake` provider binds with a provider-owned account (`ad-fake#bind`),
uses an existing credential for domain controllers and none for others. The fixture uses its own Keychain item
(`TOOLBOX_KEYCHAIN_SERVICE`); delete it afterwards with
`security delete-generic-password -s com.dobbs.Toolbox.vault-key.e2e`.

Launch args: `--connect <id>` / `--files <id>` (repeatable), `--expand-all`,
`--demo-move` (focused pane → new workspace), `--demo-close` (close the
focused pane after 3 s), `--edit-connection <id|new>`,
`--edit-provider <id|new>`, `--set-folder-credential <path>`,
`--demo-provider-account <provider-id>` (saves a typed-in bind account,
then switches to an existing credential, logging each step),
`--unlock-vault`; defaults override
`-vaultRequiresAuth NO`. Env: `TOOLBOX_STORE_PATH`, `TOOLBOX_VAULT_PATH`,
`TOOLBOX_KNOWN_HOSTS`, `TOOLBOX_KEYCHAIN_SERVICE`.

Screenshots need Screen Recording permission for the terminal; GUI
scripting (clicks) is not available without Accessibility permission, so
UI paths are exercised with launch aids or synthetic `NSEvent`s posted from
inside the app. Crash reports land in `~/Library/Logs/DiagnosticReports/`
as `Toolbox-*.ips` (JSON: header line, then body with `threads[].frames`).

## Conventions

- Commit only when the owner asks, on `main`. Commit messages end with the
  co-author line for the Claude model doing the work, e.g.
  `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`; PR bodies with
  `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Before committing: scan the diff for keys/tokens, make sure no temporary
  probe code is left, run `make test`, and update **Current state** below.
  Never commit `build/`, `core/target/`, `App/.build/`, generated bindings
  or `App/Vendor/` (all gitignored); binaries are not shared through git.
- Keep `README.md` "What works" and `docs/SECURITY-REVIEW.md` (an addendum
  per security-relevant change) current.
- Secrets never go in `connections.json`; they live in the vault,
  1Password, or are prompted for. New store fields need `#[serde(default)]`.
- Write shell scripts in bash (`#!/usr/bin/env bash`); zsh word-splitting
  bit earlier scripts.

## Invariants and gotchas (each cost real debugging time)

- `PaneSplitView` must not be its own `NSSplitViewDelegate` (AppKit's
  sidebar category recurses in `respondsToSelector:`); a separate
  `ResizeObserver` is the delegate.
- Anything that removes a pane must fix `focusedID` first:
  `rebuild()` synchronously calls `onLayoutChange` → toolbar validation →
  `focusedPane`. `closePane` does this; `focusedPane` repairs stale focus.
- `make core` after any Rust signature change; the Swift bindings are
  generated, not hand-edited.
- All IronRDP crates must be pinned to the same git revision or `picky`
  versions conflict. The RDP test server is not `Send`: it runs on its own
  thread with a current-thread runtime.
- RoyalVNCKit is a dylib dependency (by revision); `bundle-app.sh` copies
  it into `Contents/Frameworks` and adds the rpath.
- The echo test server must skip SFTP channels or it echoes SFTP INIT bytes.
- Session end: `SshListener.on_closed(clean)` /
  `RdpListener.on_closed(reason, clean)`; a pane auto-closes 0.7 s after a
  clean end only. Failures keep the pane with the message.
- Keychain access is keyed on the code signature; ad-hoc signatures change
  per build (see `make signing`). `VaultManager` re-owns the vault-key item
  when the signing certificate changes.
- SwiftUI `Picker` popups are lazy: the `NSPopUpButton` has no item titles
  until opened; `performClick` on it opens the menu for a screenshot.
- The workspace tab bar is private AppKit: `NSTabBar` is the hit-test view
  (tabs are not separate views), `NSTabBarNewTabButton` is the "+".
- `AuthKind` variants: `Password`, `KeyFile`, `Agent`, `Credential`,
  `StoredPassword` (secret in the vault under the connection id),
  `FolderCredential` (nearest folder's assignment; store keeps a path →
  credential map). Credential-based methods take the credential's username.
- `ConnectionKind`: `Ssh`, `Rdp`, `Vnc`, and File Transfer kinds `Sftp`,
  `Ftp`, `Ftps` (explicit `AUTH TLS`). File Transfer connections open the
  file browser (`SftpPaneView`/`SftpModel`), which talks to either
  `SftpSession` or `FtpSession` through the Swift `FileTransferSession`
  protocol; the two core objects must keep identical method signatures.
  `usesSsh` (ssh, sftp) decides key/agent auth; FTP uses username +
  password unsplit (`CredentialResolver.ftpAuth`).
- AD provider accounts typed into the provider are vault credentials with
  id `<providerId>#bind|admin|user`, folder = the provider's folder, tag
  `provider-account`. `AppModel.saveProvider(draft)` writes them before
  the provider and deletes ones no longer used; deleting the provider
  deletes them. Renaming a provider moves everything inside its folder.
- Every DC in the AD domain used for testing presents a 1024-bit RSA LDAPS
  certificate (checked 2026-11-16); macOS refuses those, so verified LDAPS
  cannot work there until the AD team reissues them. The working provider
  uses "Accept untrusted certificates".

## Not verified against real infrastructure

A real Windows RDP host (sign-out detection relies on the server's
Terminate), a real VNC server, an AD domain controller, Touch ID on a
certificate-signed build, and distribution to other Macs (needs Developer
ID + notarization; self-signed builds need right-click → Open).

## Ideas not started

RDP clipboard/audio/pointer, per-connection RDP certificate verification,
session restore (workspaces + layouts), broadcast input, jump hosts, port
forwards, a Tasks concept like Royal's, terminal theme picker.

## Current state (update in every commit)

As of 2026-11-18, on `main` of the new Toolbox_Windows repo.

This commit imports the macOS tree exactly as it stood on 2026-11-16 in
the original Toolbox repo (plus the "Windows port" section above and a
`.gitattributes`), so the Swift app is in this repo's history as the
reference for the port. Nothing here builds on Windows yet: the Rust core
has the seven Unix-only errors listed above, and the Swift app needs a
Mac. The next commit removes the Mac-only directories. Toolchain for the
Windows build is installed on the owner's ARM64 machine (see above).

The remainder of this section describes the Mac state at import time.

This change set: File Transfer connections (SFTP, FTP, explicit FTPS) with
a Rust FTP client (`ftp.rs`, 3 integration tests against libunftp,
including the untrusted-certificate prompt) reusing the file browser; AD
provider accounts can each be an existing credential (grouped picker) or a
username/password typed into the provider (provider-owned vault
credential); credential pickers label untitled 1Password items by
username; provider renames carry everything inside the folder. Verified
end to end with the fixture: FTP and SFTP browsers listing, FTPS prompt,
provider editor, FTPS connection editor, and the provider-account
save/switch/cleanup lifecycle. 40 Rust tests, clippy clean, audit
unchanged.

Diagnosed 2026-11-16 (no code change): the owner's failing "acme" AD providers
fail because DC certificates are 1024-bit and their 1Password bind item
has a stale password; the "ACME-AD" provider works. Offered but not
done: default "Fall back to unencrypted LDAP" to off, a key-size hint in
the error, pinning CorpRootCA.

Done earlier and verified: SSH, SFTP, RDP and VNC panes; vault with
Keychain key + Touch ID; 1Password and AD providers; Royal-style sidebar
tree; draggable pane layouts; tabbed workspaces with pane moves and tab
rename; automatic pane close; connection auth methods with the username
under the method; folder credentials; stable code signing path; pane-close
crash fix.

Pending: the owner still has to run `make signing` on each Mac.
