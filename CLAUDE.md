# Toolbox (Windows) — working notes for Claude

Read this first on any machine. It is the handoff between sessions and
machines; memory files do not travel, this file does.
**Update the "Current state" section at the end in every commit** (write it
before committing, so the commit describes itself).

## What this is

Toolbox is a replacement for Termius + Royal TS for the owner (infra admin
in a Windows/Active Directory shop; uses Termius, Royal TS/TSX and
1Password daily; prefers Rust). SSH terminals and local terminals, RDP and
VNC with dynamic resolution, file transfer; every session in a workspace
tab of its own, with split panes for putting sessions side by side;
connection objects in a Royal-style folder tree, a credential vault, SSH
key identities, dynamic folders fed by 1Password (`op` CLI) and Active
Directory, and updates over the air. Royal TS
reference material the owner provided is in `docs/royalts-reference/`. The
owner's Royal layout to mirror: document "Work" → folder "ACME" → dynamic
folders "1Password" and the AD provider (OU hierarchy), folder "Jump
Boxes".

It began as a macOS app (Swift/AppKit shell over a Rust core, repo
https://github.com/tacticalbadger/Toolbox, public since 2026-11-19 with its
history rewritten). On 2026-11-17 the owner decided to port it to Windows.
This repo (https://github.com/tacticalbadger/Toolbox_Windows.git, public
since the license commit of 2026-11-23; it was private before) is
the Windows app. **One branch: `main`.** Commit and push to `main`
directly; no feature branches unless the owner asks. Commit only when the
owner asks.

**The Mac app is the behavioural reference, and the goal is feature
parity** (owner, 2026-11-19). Two places to read it:
- Commit `3462de1` in this repo is a snapshot from 2026-11-16:
  `git show 3462de1:App/Sources/Workspace/PaneView.swift`,
  `git ls-tree -r --name-only 3462de1 App/`.
- The Mac repo itself has moved on since: 0.3.0 (2026-11-20: RDP
  clipboard, graphics pipeline, credential search), 0.3.1 (2026-11-23:
  one provider per folder, folders follow provider names), 0.4.0
  (2026-11-24: sessions in tabs, local terminals, find, a keyboard fix)
  and 0.5.0 (2026-11-24: Sparkle updates). Windows 0.3.0 carries all of
  them. Clone it somewhere outside this repo to compare (it is public;
  `git clone` into a scratch folder); do not add it as a remote here. Its
  README "What works" is the parity checklist, `docs/release-notes/` its
  changelog, and its CLAUDE.md says why things are the way they are. Its
  `core/` is the same crate minus the Windows changes: for a change in a
  shared file, `git merge-file` with the Mac's previous version as the
  base brings it over (0.3.1's `store.rs` merged without a conflict; a
  test that writes a shell script needs a batch-file twin, see
  `waiting_op`).

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
  IronRDP left the build. So the Mac's RDP work (codecs, clipboard
  protocol, painting) has no code to port: on Windows each of those is a
  matter of how the control is set up and driven.
- **VNC: a Rust VNC crate inside the core** (chosen over the C#
  MarcusW.VncClient to keep protocol code in Rust; the Rust VNC ecosystem
  is thin, expect to patch a crate, and macOS hosts need Apple's ARD
  authentication). Frames cross the FFI into a WPF `WriteableBitmap`.
  **Not started.**
- **Terminal: xterm.js inside WebView2.** (Claude's choice, not contested.)
  That is the SSH terminal.
- **Local terminals: the user's own Windows Terminal, embedded** (owner,
  2026-11-25). Offered: a pseudo-console behind the app's xterm.js pane
  (Claude's recommendation, and what the Mac does with its own engine),
  Microsoft's WPF terminal control from a CI package, or the real Windows
  Terminal window in a pane. The owner picked the last one, having been
  told what it costs: none of the app's themes, text size or find bar, no
  app shortcuts while it has the keyboard, and a window of another
  process to place and watch. Do not swap it for another approach unless
  they ask. (The owner believed the Mac used iTerm2. It does not: iTerm2
  is GPL and has no library form; the Mac runs a shell in SwiftTerm.)
- **Updates over the air: a feed signed with the app's own key** (owner,
  2026-11-25, offered next to Velopack and "not now"). The mechanism is
  Claude's: `update.json` on every GitHub Release, ECDSA P-256 signature,
  the app replaces its own folder (see the invariants). No library: the
  install form is a zip folder, which the frameworks do not handle, and
  signing and checking use what Windows and .NET already have.
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
  started. From 0.3.0 an installed copy updates itself from there.
- **Data: start fresh on Windows.** Nothing migrated from the Mac.
- Claude's choices, open to change: one window with a shared sidebar and
  workspaces as tabs inside it (Windows has no native window tabs);
  closing the last workspace leaves a fresh one; app shortcuts on
  Ctrl+Shift / Alt+Shift (README has the table) because Ctrl+letter
  belongs to the terminal, with the Mac's ⌘ becoming Ctrl+Shift and its
  ⌘⇧ becoming Ctrl+Alt (so Find is Ctrl+Shift+F as in Windows Terminal,
  New Local Terminal Ctrl+Alt+T, and Browse Files moved from Ctrl+Shift+F
  to Ctrl+Alt+F in 0.3.0); RDP certificate problems show the standard
  Windows warning; nothing but the clipboard is redirected; the window
  has no Mica backdrop (see the gotchas); Windows versions are numbered
  on their own (0.2.1 carries the Mac's 0.3.0 changes, 0.3.0 the Mac's
  0.3.1 to 0.5.0).
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
  sessions (and where they go), editor and folder actions, testing aids.
  `Workspaces/` (`PaneTree` + `PaneLayout`, `PaneContainer`, `PaneView`,
  `Workspace`, `DragController`, `LiveResize`, `IPaneContent` with
  `ISearchablePane`, `NativePanePeer`), `Terminal/` (`TerminalPane` for
  SSH, `Theme`; `LocalTerminalPane`, `WindowsTerminal`,
  `EmbeddedWindowHost`, `TerminalLedger` for local terminals), `Files/` (`FileBrowserPane`,
  `FileBrowserModel` with `LocalNames`), `Remote/` (`RdpPane`,
  `RdpClientHost` with `RdpClipboard`), `Sidebar/` (`SidebarView`,
  `SidebarNode`, the four editors, `FormBuilder`, `CredentialPicker`),
  `Model/` (`AppModel`, `VaultManager`,
  `OwnerCheck`, `CredentialResolver`, `ProviderSync`, the drafts),
  `Support/` (`AppPaths`, `AppSettings`, `Log`, `Prompts`, `Icons`, and
  the updater: `Updates`, `UpdateFeed`, `UpdateInstaller`,
  `UpdateWindow`), `Assets/Terminal/` (the terminal page with its find
  bar; `xterm/` is fetched).
  `app/Generated/` and `app/runtimes/` come from the core build;
  `app/nuget.config` pins nuget.org.
- `app/Toolbox.Tests/` — xunit tests for the app's own logic (how a
  desktop is placed in a pane, credential search, names and text that
  come from a server, tab titles, folder moves, Terminal profiles, the
  update feed and the file swap). `tools\build.ps1 test` runs them after
  the Rust tests. Not covered yet: `PaneTree`, `PaneLayout`, most of the
  drafts.
- `tools/build.ps1` — targets `core`, `assets`, `app`, `test`, `run`,
  `package`, `feed`, `clean`. `tools/publish.ps1` — GitHub Release from
  the packages and the feed (the counterpart of the Mac's `make
  publish`). `tools/update-key.ps1` — the update key: create, check,
  sign, verify, export, import. `tools/e2e/` — `make-fixture.ps1`,
  `make-rdp-server.ps1`, `servers.ps1`, `launch.ps1`, `capture.ps1`,
  `ui.ps1` (UI Automation and synthetic input), `update-rehearsal.ps1`.
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
.NET SDK 10.0.404 (11.0.100 is there too since 2026-11-20 and is the one
`dotnet` picks; the app still targets .NET 10 and packages carry runtime
10.0.15), `uniffi-bindgen-cs` from tag `v0.12.0+v0.32.0`, LLVM 23 and
CMake 4.4, cargo-audit 0.23 (since 2026-11-20). **clang and cmake are
mandatory on ARM64** (`aws-lc-sys`, `ring`). WebView2 runtime ships with
Windows 11, and so does Windows Terminal (1.25 here; local terminals are
its windows). Not installed: Node (xterm.js is fetched by `build.ps1`),
`gh`, PowerShell 7, 1Password CLI, Python (`python` is the Store's stub:
edit files with the editing tools or `sed`, not a script).

**The update key is in this account's Windows key store** ("Toolbox
Update Key", made 2026-11-25 with `tools\update-key.ps1 create`). Releases
that installed copies accept can only be made from an account that has
it. Never print or export it in a session; exporting a copy for the
password manager is the owner's to do.

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
- **`build.ps1 app -Restart` and `build.ps1 run` start the app on the
  owner's real data.** For test runs build with plain `app` (close the
  fixture instance first) and start with `tools\e2e\launch.ps1`. Never
  stop every `Toolbox` process: filter by the build's path, as the
  scripts do.
- **The owner may be at this PC while a session works** (seen on
  2026-11-20: the clipboard changed and a dialog was answered mid-test).
  Test windows come to the front, and an RDP pane in use offers the real
  clipboard to the local test server. Do not write to the clipboard
  unless it holds plain text only, and put it back; prefer automation
  patterns and window messages to real keys and clicks.
- The clock stepped back seven minutes during one session, and again on
  2026-11-25: do not read much into gaps between log timestamps. It also
  fools the build: a source file saved "before" the last build is not
  recompiled, and the app then runs without the change (seen as a launch
  argument that did nothing). When a change seems to have no effect,
  check `Toolbox.dll`'s time or build with `dotnet build
  --no-incremental`. `package` always compiles afresh.
- Real keys and clicks from the harness go through `ui.ps1`, which
  refuses to type when another program is in front. The harness's
  permission check refused a test that started a Windows Terminal window
  of its own and then killed Toolbox outright; that one is the owner's to
  try (see "Not verified").

## Everyday commands

```powershell
tools\build.ps1                 # core + assets + app (first run restores packages and fetches xterm.js)
tools\build.ps1 app -Restart    # rebuild the app, close the running build, start the new one
tools\build.ps1 test            # cargo test --features test-support, then dotnet test on app\Toolbox.Tests
tools\build.ps1 run             # starts it on the real data, not the fixture
cd core; cargo clippy --features test-support --all-targets     # needs clang + cmake on PATH
cd core; cargo audit            # 2026-11-25: no vulnerability after `cargo update -p url`; basic-toml and chrono listed as unmaintained
tools\update-key.ps1 check      # is the update key in this account the one the app trusts?
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
tools\build.ps1 feed                           # update.json + update.json.sig for both (needs the update key)
tools\e2e\launch.ps1 -Exe build\release\Toolbox-<version>-win-x64\Toolbox.exe --connect echo   # test what ships
tools\publish.ps1 -DryRun                      # then without -DryRun
```

Releases on this repository are immutable (a GitHub setting): once one is
published its files and tag are fixed, and the tag name can never be used
for another release, deleted or not. `publish.ps1` therefore attaches the
files to a draft and publishes last. There is no second try for a version
number: read the dry run, and test the packages first. **Publishing is
also the moment installed copies start being offered the version**, so
the packages have to be right before it, not after.

After a release, the nearest thing to a real update is a test copy told
to look at the real feed: copy a package's folder somewhere, start it
with `tools\e2e\launch.ps1 -Exe <copy>\Toolbox.exe --updater-feed github
--updater-pretend-build 1 --demo-update`, and it is offered the published
version, with the real key (add `--demo-update-install` to let it
install itself without the dialog).

## End-to-end checks (no real hosts needed)

```powershell
tools\e2e\make-fixture.ps1            # keys, known_hosts, fake op.cmd (62 logins), store, settings -> build\e2e
tools\e2e\make-rdp-server.ps1         # once: RDP test server -> build\e2e-rdp (about three minutes)
tools\e2e\servers.ps1 start           # SSH+SFTP :2222, FTP :2121, FTPS :2122, RDP :3390 and :3391 (-ExitAfter 6 for auto-close)
tools\e2e\launch.ps1 --connect echo --connect echo-1p     # relaunch against the fixture
tools\e2e\capture.ps1                 # PNG of every Toolbox window (and Windows Security dialogs) -> build\e2e\shots
tools\e2e\ui.ps1 tree                 # also: find, invoke, expand, select, setvalue, value, focus, press, click, clickat, drag, keys, text
tools\e2e\update-rehearsal.ps1        # a whole self-update from a test feed with a test key, and what must be refused
tools\e2e\servers.ps1 stop
```

Fixture ids: `echo` (key file, folder Jump Boxes), `echo-1p` (1Password
credential through the fake `op.cmd`), `echo-folder` (folder credential →
prompts as `folderuser`, which the echo server rejects: the failure case),
`rdp-local` (127.0.0.1:3390, plain graphics, resizes), `rdp-gfx`
(127.0.0.1:3391, graphics pipeline), `vnc-local`, File Transfer connections
`sftp-local`, `ftp-local`, `ftps-local` (self-signed, so it prompts), plus
AD-style generated connections under `ACME/CORP-AD/…`. The fixture has its
own vault, vault key, log (`build\e2e\toolbox.log`) and settings with the
owner check off; `make-fixture.ps1` resets them.

Launch arguments since 0.3.0: `--connect`, `--files` and
`--local-terminal` may repeat (the first session takes the empty
workspace, every further one a tab of its own);
`--local-terminal-profile <name>`; `--demo-merge` (after 3 s every other
tab's sessions move into the first workspace); `--demo-select-tab <n>`
(shows that tab after 3 s and the last one again after 6 s);
`--demo-find <text>` (after 4 s, searches the focused SSH terminal and
logs `terminal: find … match i of n`); `--demo-provider-rename <id>
<name>` and `--demo-folder-rename <path> <new path>` (after 3 s, log
`provider-rename:` / `folder-rename:` with where things went or why it
was refused); `--rdp-expose-control` (see the gotcha on lost keys); and
the updater's, which only a run against a fixture honours:
`--updater-feed <folder|github>`, `--updater-key <public key>`,
`--updater-pretend-build <n>`, `--demo-update` (look 2 s after launch,
quietly, as the daily look does), `--demo-update-install` (take the
update without the dialog). `--demo-move` only does something for a pane
that shares its workspace.

How things are checked: assertions go through the log (`ssh: connected`,
`layout: …`, `vault: …`, `prompt answered: …`, and since 0.3.0 `tabs: a
(1) | *b (2)`, one line per burst of changes to the tab row with the tab
in front starred and its number of sessions, `local terminal: …`,
`sidebar: folder a -> b; n open folders followed`, `updates: …`) and the
automation tree
(`ui.ps1 find -Id RdpStatus`); screenshots are for looks. Automation ids:
`SidebarTree` items are `conn:<id>`, `cred:<id>`, `folder:<path>`,
`prov:<id>`; prompts `PromptTitle`, `PromptInput`,
`PromptOk`, `PromptCancel`; editors `EditorSave`, `EditorCancel`,
`Validation`, fields by property name (`Name`, `Host`, `AuthMethod`,
`FamilyRdp`…); toolbar `ToolSplitRight` etc.; file browser `Files…`;
menu `RemoteDesktopMenu` with `RdpFastGraphics` and `RdpShareClipboard`
(`ui.ps1 expand -Name View`, `expand -Id RemoteDesktopMenu`, then
`invoke`). A credential picker is a button with the field's id
(`Credential`, `BindCredential`…) whose help text is the current choice;
`invoke` opens its list, `setvalue -Id CredentialSearch` types the
search, rows are `pick:<credential id>` (`pick:` for none) and `select`
on a row picks it.
`invoke`/`setvalue`/`select`/`value`/`focus`/`press` touch neither mouse
nor keyboard; `click`, `drag`, `keys`, `text` are real input on the
owner's desktop. System file
dialogs do not answer to patterns: click the field, `text`, then
`keys '{ENTER}'` (the folder picker also needs `click -Name 'Select
Folder'`). Drop-downs: `click -Id …` then `keys '{DOWN}{ENTER}'`.
More ids since 0.3.0: menu items `NewLocalTerminal`,
`TerminalProfilesMenu`, `EditMenu` with `EditFind`…, `CheckForUpdates`,
`CheckForUpdatesAutomatically`; toolbar `ToolLocalTerminal`; panes
`RdpPane`, `LocalTerminalPane` (one element each, see the gotcha) with
`RdpStatus` / `LocalTerminalStatus` while they show text; the update
offer `UpdateOffer` with `UpdateSummary`, `UpdateNotes`, `UpdateInstall`,
`UpdateLater`. Workspace tabs are not in the automation tree (a `Border`
has no peer): read the `tabs:` log line.

The sidebar builds only the rows on screen (since 0.3.0), so a row
further down is not in the automation tree until it is scrolled to.
`ui.ps1` does that by itself when an id is not found: it pages the
tree from the top. A row found that way may still be outside the
window; patterns work on it, a real click does not.

Typing where it matters is checked with real keys: `ui.ps1 focus -Id
SidebarFilter`, `ui.ps1 text -Text web`, `ui.ps1 value -Id
SidebarFilter`. `text` sends its keys in one burst, which is what shows
keys being lost while the window is busy; typed slowly, the same keys
arrive. An embedded terminal takes keys after `ui.ps1 clickat` inside
it. The certificate warning of the RDP test servers is answered with
`ui.ps1 press -Id 14004`.

`update-rehearsal.ps1` needs `build.ps1 app` and the fixture, makes
`build\e2e-update`, and removes it when it passes. What it proves and
what it does not is in its header; in short, everything but the download
over HTTPS, which `--updater-feed github` covers once a release with a
feed exists.

**The RDP test server** is the Mac repo's IronRDP one. It is not in this
repo: `make-rdp-server.ps1` fetches its three source files from the
public Mac repo at the 0.3.0 commit (SHA-256 checked) into `build\e2e-rdp`
and builds them; `servers.ps1` starts it on :3390 (`--paste`: asks for
whatever the app announces on its clipboard and logs the size) and on
:3391 (the same with `--graphics-pipeline`). `-RdpArgs '--copy-text hi'`
makes the first one copy text on its side instead. Logs:
`build\e2e\rdp_server.log`, `rdp_gfx_server.log` (`layouts=[…]` are the
resize requests, `graphics=` the pipeline's capability set).

- Its certificate is self-signed, so the control's certificate warning
  comes first and blocks the whole window. Nothing of UI Automation can
  press its buttons (they are panes without patterns): send `BM_CLICK`
  (0x00F5) to the window of the element with automation id `14004`
  ("Yes"). Ticking its "Don't ask me again" stores the certificate's hash
  under `HKCU\Software\Microsoft\Terminal Server Client\Servers\127.0.0.1`
  (one is there since 2026-11-20; harmless, and stale once the server is
  restarted with a new certificate).
- The server keeps its desktop size from one connection to the next.
- **Resizes can only be checked on :3390.** In pipeline mode the server
  answers a resize with a graphics reset and no reactivation, which
  Microsoft's control does not take as a new desktop (no size event, a
  wrong picture); and the control does not show the server's H.264 test
  picture, only its green fill. What :3391 is good for: seeing that the
  control takes the pipeline (`graphics=Some("V10_6…")`, `h264_frames`).
- It does not sign out, so the clean-end path of RDP is untested.
- A window-edge drag can be played without the mouse: send
  `WM_ENTERSIZEMOVE` (0x0231), resize with `SetWindowPos` in steps under
  250 ms apart, send `WM_EXITSIZEMOVE` (0x0232). The log must show no
  "desktop resized" during the steps and one right after.

## Conventions

- Commit only when the owner asks, on `main`. Commit messages end with
  the co-author line for the Claude model doing the work.
- **Commit as the account, not as a person.** This clone's `user.name`
  and `user.email` still name the old account, with a name in the
  address. Since 0.3.0 commits are made the way the Mac notes prescribe:
  with the identity of the newest commit on GitHub given on the command
  line (`git -c user.name=… -c user.email=… commit`; read it with `git
  log -1 --format='%an <%ae>' origin/main`), and the setting left alone
  for the owner to change. The commits up to 0.2.1 carry the old one.
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
- **The license is proprietary** (the owner, 2026-11-23, for every public
  repository on the account: "the most restricting license my code can
  have"). `LICENSE` is all rights reserved, with one permission: to run
  the builds the owner hands out. Its copyright line carries the account
  name `tacticalbadger`, which the owner chose over a real name so that
  the rule above holds. Do not add an open-source license or a `license`
  field to a manifest, and keep the README's License section in step with
  `LICENSE`. Third-party components keep their own licenses.
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
- **Where a session goes is decided in one place**,
  `MainWindow.PlaceForNewSession()`: an empty pane of the workspace in
  front (the focused one first), otherwise a new workspace tab at the end
  of the row. `Open`, `OpenFiles` and `OpenLocalTerminal` go through it
  when no pane is named; a drop on a pane names its own, and a pane moved
  from another workspace uses `PaneContainer.PaneForAdoptedSession`
  (which still splits). The owner asked for this on the Mac (2026-11-24):
  sessions open in tabs, arranging them is the user's business. Do not
  bring back automatic splits.
- A tab's title is the user's name for it (`Workspace.CustomName`, set by
  Rename, cleared by an empty name), else its sessions' `TabTitle`s
  ("a", "a, b", "a, b + 3"), else "Workspace N". `IPaneContent.TabTitle`
  defaults to `Title`; the SSH pane keeps to the connection's name
  because remote shells change the title with every prompt.
- A workspace whose last pane is closed or lifted is told through
  `PaneContainer.Emptied` and closes its tab on the next turn of the
  dispatcher, unless it is the only tab. Empty panes the user split off
  keep a tab alive (`Emptied` is not raised while other panes remain).
- What closing a pane would interrupt is `IPaneContent.CloseWarning`
  (null: close without asking). The quit and close-workspace questions
  count `SessionsToWarnAbout`, not live sessions: an idle local shell is
  live and loses nothing.
- **One folder, one provider**, and a provider's folder is its location
  plus its name (the store: `checked_provider`, `check_provider_folders`,
  `separate_provider_folders`; the Mac's notes have the full account).
  The app calls `validate_provider` before it writes typed-in accounts,
  so a refusal leaves nothing in the vault, and writes those accounts to
  the folder the provider is in at that moment (its save then moves them
  with the rest). Everything that renames or moves a folder or a provider
  raises `AppModel.FolderMoved`; the sidebar keeps open folders open
  across it (a folder node's id holds its path, so the node is a new one
  and its expansion has to be carried over, in memory and in
  `settings.json`).

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
- **The control must be the `WindowsFormsHost`'s own child.** With a
  WinForms panel in between, the control's window stays hidden after the
  host goes `Hidden` then `Visible` (the host shows its child, the child
  does not re-show the ActiveX window). To give the control an exact
  pixel size, size the host: `Width`/`Height` in DIPs = pixels / DPI
  scale, top left; layout rounding makes them whole pixels.
- mstscax scaling, measured 2026-11-20. Given more room than the desktop,
  the control centres it in a grey margin; given less, it shows scroll
  bars. `SmartSizing` only ever shrinks, can be switched while connected,
  and works in pipeline mode. **`ZoomLevel` (the extended setting) must
  not be used**: set while no session is up it ends the process with an
  access violation inside the control; it does not scale pipeline output
  (the desktop's frame grows, the picture does not); and smart sizing is
  refused while it is not 100. Hence `RdpPane.LayoutDesktop`: the host is
  exactly the desktop's size when it fits, a same-shape smaller size with
  smart sizing when it does not, and a smaller desktop is never
  magnified. This is why the Mac's Retina Resolution switch has no
  Windows counterpart yet.
- mstscax clipboard, measured the same day. With `RedirectClipboard`
  alone the control announces the clipboard to every connected session at
  every change, focused or not. With the extended setting
  `ManualClipboardSyncEnabled` it announces only when asked through
  `IMsRdpClipboard` (from `IMsRdpClientNonScriptable7.Clipboard`), and
  takes an offer back by itself when the clipboard changes. That object
  answers no `QueryInterface` for its own IID, so `RdpClipboard` calls it
  through its function table (slots from the type library). A C# method
  named `_VtblGap…` does not skip table slots unless it is
  `rtspecialname`, which C# cannot write: do not declare partial COM
  interfaces that way.
- Other extended settings that exist: `EnableHardwareMode` (false: no
  Direct3D device at all, checked by the modules the process loads; this
  is Fast Graphics off) and `DisableH264HardwareDecode`. No setting turns
  the graphics pipeline off: the control takes it (H.264 included)
  whenever the host offers it, colour depth and connection type
  notwithstanding. The control's device redirections all default to off.
- A WinForms child can only take the focus once its host is shown: focus
  it from a dispatcher callback at `Input` priority, not in the handler
  that made it visible. `PaneView` gives a session the keyboard when it
  comes up in the active pane that way.
- **Mica and native child windows.** With the Fluent theme's window
  backdrop, Windows treats what a child window draws through GDI as
  see-through: scaled or software-drawn RDP pictures come out washed
  with the backdrop, GDI black as the backdrop itself. The backdrop is
  off (`Switch.System.Windows.Appearance.DisableFluentThemeWindowBackdrop`
  in `Toolbox.csproj`); the window background is then plain.
- Under the Fluent theme a `MenuItem` shows its check mark only when it
  is `IsCheckable`; setting `IsChecked` alone shows nothing.
- A `Popup`'s rows for UI Automation: a `ListBoxItem` is not invokable,
  only selectable. The credential picker treats a selection it did not
  make itself as a pick, which serves `ui.ps1 select` and screen readers.
- `tokio::process::Command` needs `CREATE_NO_WINDOW` in a windowed app,
  or every `op` call flashes a console.
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
- **The same for the C# assembly.** The compiler records the full path of
  its debug file in `Toolbox.dll`. `package` asks for no debug
  information, but `dotnet publish` reuses an assembly the `app` target
  compiled at the same commit, path included; the check caught that on
  2026-11-20. `app/Directory.Build.props` now maps the checkout's folder
  to `toolbox` (`PathMap`) for every build, and `package` deletes the
  intermediate output first so it always compiles afresh.
- The virtual host for the terminal page is under `.example`; other
  made-up names can cost a DNS lookup on every load.
- `e2e\ui.ps1` must not tap Alt to get the foreground: it opens the menu
  bar and eats the next key.
- **A hosted window in the accessibility tree loses keystrokes.** With an
  `HwndHost` that shows another program's window (the RDP control; a
  Windows Terminal window) among WPF's automation peers, and any UI
  Automation client listening (a password manager, Windows' own text
  input, this harness), keys typed anywhere in the window while WPF is
  laying out are dropped: of "web" typed quickly into the sidebar's
  filter, the "w" arrived. What happens: after layout WPF raises
  automation events; for each, UI Automation asks for the window's host
  provider (`AutomationInteropProvider.HostProviderFromHandle`), turning
  that pointer into a .NET object waits (`SynchronizationContext.
  WaitHelper`), and that wait, when it lasts a few milliseconds, takes
  key messages out of the queue (`PeekMessage` with `PM_REMOVE |
  PM_NOYIELD`) and throws them away. It only lasts that long with such a
  window in the tree. A COM message filter (`CoRegisterMessageFilter`) is
  asked (`MessagePending`) and its answer changes nothing; the RDP
  control's `KeyboardHookMode` has nothing to do with it; the keys do not
  reach the remote desktop either. The cure is `NativePanePeer`: a pane
  that hosts such a window is one element to accessibility, without the
  host among its children. Any new pane type that hosts a foreign window
  must use it. `--rdp-expose-control` brings the old behaviour back for a
  comparison. The WebView2 of the SSH terminal does not have the problem
  (measured). Found with a thread-level `WH_GETMESSAGE` hook that logged
  a stack trace for every key message removed from the queue.
- **The sidebar tree is virtualized, and under the Fluent theme that
  takes three things**: `VirtualizingStackPanel.IsVirtualizing` and
  `ScrollViewer.CanContentScroll` on the `TreeView`, and an `ItemsPanel`
  of `VirtualizingStackPanel` in the `TreeViewItem` style (the theme's
  own style keeps a plain panel, so every expanded folder built all its
  rows). Before: 2.7 s for the first filter keystroke with 2,400
  connections; after: under 0.1 s. Recycling mode is not used: it writes
  stale expansion back through the two-way binding.
- xterm.js's search addon draws its highlights through an interface the
  terminal only hands out with `allowProposedApi: true` (decorations);
  without it the first search throws. The addon's file comes with the
  other assets, and a build whose fetched assets are older than the list
  in `build.ps1` is refused (`Assert-Assets`): the page stops at the
  first script it cannot load, and the terminal stays black.
- **Windows Terminal as a pane** (`LocalTerminalPane`,
  `EmbeddedWindowHost`, `WindowsTerminal`). `wt.exe -w new --focus`
  opens a window without tabs or title bar. It can be told apart only by
  being a `CASCADIA_HOSTING_WINDOW_CLASS` window that was not there
  before, so windows are asked for one at a time; it exists about 0.4 s
  after the launch and is still hidden then, which is when it is taken
  (no flash). Child style first, then `SetParent`. All of the user's
  Terminal windows are one process; the shell is that process's child
  (not the console host's), which is how the pane finds it, and a shell
  with a child of its own is "running something". Closing posts
  `WM_CLOSE` and waits up to 2.5 s; a window that is still there is given
  back to the desktop (`Release`), never destroyed; the host window is
  only disposed after that, and `DestroyWindowCore` releases whatever is
  still inside. At exit the app waits up to 1.5 s for them
  (`LocalTerminalPane.FinishAll`), pumping messages, because a child
  window that is destroyed tells its parent with a sent message. Keyboard
  focus in the terminal is seen by polling `GetFocus` (the two threads
  share input state once one window is the other's child). The pane's
  title is the window's title.
- **A killed Toolbox orphans its terminals.** When the app's process is
  ended from outside (Task Manager, a crash, and `launch.ps1`, which
  stops the previous fixture instance that way), Windows destroys the
  embedded window with its parent and Windows Terminal keeps the shell
  running without any window. `TerminalLedger` writes each run's shells
  to `%LOCALAPPDATA%\Toolbox\terminals\<pid>.txt` and the next start
  (fixture or real: the folder is shared, the files are per run) ends
  those of runs that are gone, matching process id, start time and name
  (`local terminal: ended powershell (…), left without a window…` in the
  log). A shell is taken off the list when its terminal ends, closes, or
  is handed back to the desktop. What is not cleaned up: a Windows
  Terminal process left with no window and no shell (it stays until
  logoff, and serves the next `wt` launch normally; the app does not end
  Terminal itself). After fixture runs with local terminals, check
  `Get-Process WindowsTerminal`.
- **The update key and the two .NETs.** `update-key.ps1` runs in Windows
  PowerShell (.NET Framework, CNG): `ECDsaCng.SignData` gives r and s side
  by side (64 bytes), which .NET checks as
  `DSASignatureFormat.IeeeP1363FixedFieldConcatenation`; the public half
  travels as X and Y (the last 64 bytes of an `ECCPUBLICBLOB`). A named
  key can be created from an `ECCPRIVATEBLOB` property but not from
  PKCS#8 ("not supported"), so that is the export format. A test checks a
  signature made by the script against the app's code.
- **Updates: what has to stay true** (`Support/Updates.cs` and friends,
  `docs/SECURITY-REVIEW.md` 0.3.0 addendum).
  - A copy takes an update only if `update.json` carries the update key's
    signature and the package matches the length and SHA-256 in it.
    Nothing else gets an update in, and nothing gets one in without the
    key: there is no fallback, so a lost key means every copy is replaced
    by hand. One key; never make a second while the first exists
    (`update-key.ps1 create` refuses).
  - The feed is `update.json` + `update.json.sig`, files on every GitHub
    Release; copies read `releases/latest/download/update.json`. A release
    without them stops updates for everyone until the next one, silently,
    so `publish.ps1` refuses to publish without a feed that describes
    exactly the zips it uploads, signed with the key in the source. The
    address is built into every copy: no renaming of the repository or
    the account without a release that knows the new address first.
  - An update is a higher build number, and the build number is the
    commit count. Never rewrite history in a way that lowers it, and
    never publish from a branch with fewer commits than the last release.
  - The feed carries no address and no free file name: the package is
    `Toolbox-<version>-<arch>.zip` on the release `v<version>`.
  - Only a packaged build looks for updates (`AppInfo.IsPackaged`, set by
    `-p:ToolboxPackaged=true` in `package`); `build.ps1 app` builds do
    not, or a release would replace the dev build. A run against a
    fixture looks nowhere unless `--updater-feed` names a feed, and only
    such a run takes a feed, a key or a pretended build from the command
    line.
  - The swap is done by the new version: the unpacked copy is started
    with `--apply-update <folder> <pid>` (handled first thing in
    `App.OnStartup`, before any store or window), waits for the old
    process, moves the old files aside and copies its own in, rolls back
    on failure, starts the app from its usual folder. `files.txt`, which
    `package` writes into every package, is what tells an update which
    old files to remove; files neither version lists are left alone.
  - Downloads go to `%LOCALAPPDATA%\Toolbox\updates` (a fixture run:
    `TOOLBOX_UPDATES_DIR`), removed 20 s into the next start.

## Not verified against real infrastructure

A real Windows RDP host, for everything: sign-out detection, CredSSP
against a domain, and what is new in 0.2.1 (clipboard text and files both
ways under the manual mode, the resize at the end of a drag, shrinking
while dragging, Fast Graphics off). All RDP checks ran against the
IronRDP test server. Also: a real SSH host with the Windows agent or
1Password's agent (the `ssh-agent` service is disabled here), the real
`op.exe` (including that it still prompts properly now that it runs
without a console window), an AD domain controller from this machine,
Windows Hello, completing the password owner check, an encrypted key
file's passphrase prompt, private keys from the vault, Windows 10, the
x64 build on x64 hardware (it has run under emulation here), and a
coworker actually installing a release.

New in 0.3.0 and not verified: an update between two published releases
(the first real one will be 0.3.0 to whatever comes next); an update of
a copy in a folder it may not write to; cancelling a download; the
update question when sessions are open. For local terminals: what the
user's other Windows Terminal windows do when Toolbox is killed outright
with a terminal in a pane (they are one process; the harness's
permission check refused that test; with no other Terminal window open,
the shell is orphaned and the next start ends it, which was seen), a
Terminal that asks before closing
(the pane then hands the window back to the desktop: read in the code,
not seen), moving a terminal pane to another workspace by drag, Windows
Terminal versions other than 1.25, and a PC without Windows Terminal
(the pane says so). The typing fix was shown with injected keys against
the RDP test server, not with a person typing into a real session.

## Parity with the Mac app

Done and exercised against the local test servers (2026-11-19 and
2026-11-20): tabbed workspaces, split panes with kept divider ratios,
pane and sidebar drag and drop, move pane to a workspace, automatic close
after a clean end, SSH terminals with themes and text size, SFTP/FTP/FTPS
browser (list, new folder, upload, download, untrusted-certificate
prompt), RDP with dynamic resolution, sidebar tree with filter, the four
editors, folder credentials, stored passwords, vault on DPAPI with the
owner check, 1Password provider sync through a fake `op`, provider-owned
accounts, launch arguments, build script, harness.

From the Mac's 0.3.0, in Windows 0.2.1:
- RDP clipboard, only for the pane in use (the control's manual mode).
  Wider than the Mac in one way: files from the host are accepted too.
- Modern graphics: nothing to do, the control always took the pipeline.
  Fast Graphics exists as a switch with a Windows meaning (hardware or
  software decoding).
- Resize: asked for when a drag ends (250 ms into a pause, 80 ms after
  other layout changes); the picture is kept, and shrunk to fit while a
  pane gets smaller. Not magnified while it grows.
- Searchable credential picker; sidebar without the "All" groups.
- Testing aids: `--demo-split`, `--assume-keyboard`,
  `--demo-credential-search`. No `--snapshot`: `capture.ps1` works here.

From the Mac's 0.3.1, 0.4.0 and 0.5.0, in Windows 0.3.0:
- 0.3.1: the store's provider rules (the same code), the provider editor
  without a hidden folder name, open folders kept across renames. The
  Windows sidebar already reopened its folders at start.
- 0.4.0, sessions in tabs: the same rules (see the invariants). One
  sidebar serves every tab here, so there is nothing to hand from tab to
  tab.
- 0.4.0, local terminals: **different on purpose** (see Decisions). The
  Mac shows folder and foreground program in the tab; here the tab shows
  what Windows Terminal titles its window. No `--demo-type`: the pane
  cannot type into another program's window; real keys through `ui.ps1`
  do.
- 0.4.0, find: xterm.js's search addon with a find bar in the terminal
  page, for SSH terminals. Local terminals have Windows Terminal's own.
- 0.4.0, the keyboard fix: the Mac's bug (a live remote desktop taking
  ⌘V from the whole window) does not exist here; keys typed outside a
  remote desktop never reached it. What did exist was keys getting lost
  (see the gotcha), which is fixed.
- 0.5.0, updates: the app's own signed feed in place of Sparkle; Help ▸
  Check for Updates… and Check for Updates Automatically.
- Not from the Mac: the sidebar tree builds only the rows on screen.

Missing, in suggested order:
1. **VNC** (`vnc.rs` in the core + a test server + `VncPane`).
2. Code signing and an installer. Zips, `publish.ps1` and updates exist
   (the Mac side has Developer ID signing, notarization and a DMG);
   without a certificate every recipient gets the SmartScreen warning at
   the first download (updates do not show it). The x64 package has only
   run under emulation on the ARM64 dev machine.
3. **Retina Resolution's counterpart** (a desktop with fewer pixels,
   magnified). Needs magnification the control cannot be trusted with
   (see the gotchas). The way that might work: host the control in a
   DPI-unaware child window (`SetThreadDpiHostingBehavior` mixed mode) so
   Windows scales it; not tried.
4. App icon (`.ico` from the Mac drawing in `bridge/make-icon.swift` at
   `3462de1`).
5. Sidebar multi-selection (the Mac deletes and drags several at once).
6. More of `Toolbox.Tests`: `PaneTree`, `PaneLayout`, the drafts.
7. Smaller: File menu wording says "Browse Files" (Mac: "Browse Files
   (SFTP)"); file rename/delete from the browser were not driven by a
   test; sidebar rows do not ellipsize long detail text; terminal output
   has no back-pressure for very fast producers; OSC 52 clipboard writes
   are not supported (the Mac allows them); a pane's own close button
   closes a live session without asking (the toolbar's and the menu's
   ask).
8. Third-party notices in the package (only xterm.js's license travels
   today; the crates, WebView2's loader and the .NET runtime ask for
   theirs too). The Mac has the same gap.
9. For local terminals, if the owner wants them: app shortcuts while a
   terminal has the keyboard (needs a keyboard hook, which the security
   review would have to look at first), and a way to tell a busy
   terminal by more than the shell's children.

## Ideas not started

Session restore (workspaces + layouts), broadcast input, jump hosts, port
forwards, a Tasks concept like Royal's, per-connection RDP options
(drives, printers, gateway), Kerberos (current-user) binds for the AD
provider, binding the vault key to a Windows Hello credential, requiring
NLA for RDP (see the security review of 2026-11-20; direct CredSSP may do
it and needs a real host to try).

## Current state (update in every commit)

As of 2026-11-25, on `main`. **Version 0.3.0 (build 10)**: the Mac's
0.3.1, 0.4.0 and 0.5.0 brought over, and a release.

The owner asked in one message for parity with the Mac's new versions,
tested, "and a new build pushed at the end", and for options on "terminal
integration" (they took the Mac's local terminal for iTerm2). Asked in
the session, they chose: local terminals as the embedded Windows Terminal
window; self-update with a signed feed; commit, push and publish 0.3.0.

What is in this change set:

- **Providers and folders (Mac 0.3.1).** The Mac's `store.rs` merged in
  (six new tests; `a_sync_lands_where_the_provider_is_by_then` got a
  batch-file `op` for Windows), the per-connection channel tables of the
  SSH test server, the provider draft without `Leaf`, `validate_provider`
  before accounts are written, `AppModel.FolderMoved`, the sync that
  names a provider by its name of now and says nothing about one deleted
  meanwhile.
- **Sessions in tabs (Mac 0.4.0).** `PlaceForNewSession`, `Workspace`
  titles, `PaneContainer.Emptied`, `CloseWarning`; Move to New Workspace
  is off for a session that has its tab to itself; Rename takes an empty
  name to mean "automatic again"; a `tabs:` log line.
- **Find in SSH terminals (Mac 0.4.0).** `@xterm/addon-search` 0.16.0 and
  a find bar in the terminal page; Edit menu; Ctrl+Shift+F, with Browse
  Files moved to Ctrl+Alt+F.
- **Local terminals (Mac 0.4.0, done the owner's way).** A Windows
  Terminal window in a pane; File ▸ New Local Terminal (Ctrl+Alt+T), a
  submenu of Terminal's profiles, a toolbar button; `TerminalLedger`, so
  that shells orphaned by a killed app are ended at the next start.
- **Updates (Mac 0.5.0).** `Updates`, `UpdateFeed`, `UpdateInstaller`,
  `UpdateWindow`; Help ▸ Check for Updates… and the automatic switch;
  `tools\update-key.ps1`, `build.ps1 feed`, `publish.ps1` uploading the
  feed; `files.txt` in every package; `update-rehearsal.ps1`. The update
  key was made on the dev PC in this session.
- **Two things nobody asked for, found while testing.** Keys typed into
  the sidebar's filter were lost while a remote desktop was on screen
  (`NativePanePeer`; a day's worth of tracing is in the gotcha). And the
  sidebar took seconds per keystroke with a few thousand connections
  (virtualized now).
- `ui.ps1`: `press`, `focus`, `value`, and paging through the sidebar
  for rows that are not built yet.

Verified: 43 Rust tests, 102 app tests. In the app against the fixture
and the local servers: SSH, SFTP and FTP each in a tab named after it and
in the order opened; four sessions merged into one workspace, the emptied
tabs closing; a tab closing when its shell logs out; switching tabs; RDP
connecting in a tab that is not in front; the provider rename (62
credentials and an open folder follow), a rename refused before anything
is written, a folder move with its credential; the find bar finding and
highlighting; a local terminal taking typed keys, the close question
naming `ping`, `exit` closing the tab, three sessions with two terminals
and a quit that closes them, a named profile, the profile menu, a shell
orphaned by a killed run ended by the next start; "web"
typed in a burst into the filter with a remote desktop on screen arriving
whole (and arriving as "w" or "wb" with `--rdp-expose-control`), with the
test server's key count unmoved; the filter with 2,400 generated
connections; the update rehearsal (an update applied and the app back up,
nothing offered afterwards, an altered package and an altered feed
refused); the update window; a look at the real feed address (404 until
this release is out, taken quietly).

Not verified: see "Not verified against real infrastructure".

For the owner:
- **Back up the update key**: `tools\update-key.ps1 export -To <file>`,
  put the file in the password manager, delete it. Until then the only
  copy is in this Windows account. Without the key no update can be
  made; with it anyone can make one.
- Local terminals are Windows Terminal's: Toolbox's shortcuts do nothing
  while one has the keyboard, and Ctrl+Shift+T there opens a second tab
  inside that Terminal window (which the pane cannot show: it is in
  "focus" mode). If that grates, say so (Parity, item 9).
- The store on this PC may hold two providers in one folder, as the
  Mac's did: the hidden one shows up with " 2" after its name; delete it
  from its context menu if it is not needed.
- Still open from before: the password fallback of the owner check, a
  code-signing certificate, the reduced-resolution switch, NLA for RDP,
  the old clones.

Next: VNC, then signing and an installer.

Released: **v0.3.0 (build 10)** is published from this commit as two
self-contained, unsigned zips (win-arm64, win-x64) with `SHA256SUMS`,
`update.json` and `update.json.sig`. It is the first release an installed
copy can find by itself, and so the last one anybody downloads by hand:
0.2.1 and earlier cannot be told that it exists. If this paragraph is
still here but the Releases page has no v0.3.0 with five files, the
release step did not finish: `tools\build.ps1 core assets package`,
`tools\build.ps1 core package -Arch x64`, `tools\build.ps1 feed`,
`tools\publish.ps1` (it carries on with a draft it finds).

**License, 2026-11-23, on the owner's word and pushed:** `LICENSE` added
(proprietary, see Conventions), a License section in `README.md`, and the
`license = "MIT"` line taken out of `core/toolbox-core/Cargo.toml`. No
code changed. The commit was made on the Mac from a fresh clone.

**The 0.2.1 state (2026-11-20)**: the Mac's 0.3.0 brought
over, a security pass, and a release. (0.2.0 is the same app; its release
went out without files and the number could not be used again, see
"What happened to 0.2.0" below.)

The 0.2.1 change set, asked for by the owner in one go ("the Mac version was
updated; give this app parity, vet the code for security issues, update
the docs, push and release"):

- **RDP clipboard for the pane in use.** `RdpClipboard` in
  `RdpClientHost.cs`, the clipboard part of `RdpPane`. 0.1.0 already
  shared the clipboard, with every connected session at once; see the
  gotchas and the security review.
- **RDP resize and placement.** `LiveResize` (window edge, pane dividers,
  sidebar edge), `RdpPane.LayoutDesktop` and `FitFor`. A session takes
  the keyboard when it comes up in the active pane.
- **View ▸ Remote Desktop**: Fast Graphics and Share Clipboard, in
  `settings.json` as `rdpFastGraphics` and `rdpShareClipboard`, read when
  a session connects.
- **Credential picker with search** (`Sidebar/CredentialPicker.cs`) in
  the connection editor, the folder credential dialog and the provider
  editor. **Sidebar**: the "All Connections" and "All Credentials" groups
  are gone.
- **Security pass**: `rsa` advisory cleared by a dependency update
  (`core/Cargo.lock`); download names (`LocalNames`); terminal title
  length; log entries on one line; every RDP device redirection off by
  name. Addendum in `docs/SECURITY-REVIEW.md`.
- Also: no Mica backdrop; menu switches show their check marks (the
  vault's and the themes' never did); `op` without a console window;
  build paths kept out of `Toolbox.dll` too (`PathMap`, and `package`
  compiles afresh);
  `app/Toolbox.Tests`; `tools\e2e\make-rdp-server.ps1`, RDP servers in
  `servers.ps1`, 62 logins in the fixture.
- **Not done from the Mac's 0.3.0:** the Retina Resolution switch (see
  Parity, item 3). An attempt built on the control's zoom was thrown
  away after it crashed the process and left pipeline pictures unscaled.

Verified for 0.2.1: 38 Rust tests, clippy clean, `cargo audit` without
vulnerabilities, 44 app tests. In the app against the local servers:
SSH (key file, and 1Password through the fake `op` on the rebuilt core),
SFTP and FTP browsers connecting, RDP on both test servers (the pipeline
taken on :3391), resize after a split, a played window-edge drag (no
request during it, one at its end, picture shrunk meanwhile with true
colours), the clipboard offered to the session in use and not to a second
one open beside it, clipboard off (the server sees no clipboard channel),
software graphics, the picker searching 62 credentials and saving a
choice, the menu switches and their check marks.

Seen once in that work and not since: one RDP
session ended with "An internal error has occurred" (reason 2568) a
second after its first resize, in a run where the certificate warning had
been open for a while; it did not happen again in a dozen later runs.

First things to check with the owner on a real Windows host: connect,
drag-resize, copy text both ways, copy a file each way, then open a
second remote desktop and confirm that only the one in front sees a new
copy. If a picture is garbled, View ▸ Remote Desktop ▸ Fast Graphics off
and reconnect.

**v0.1.0 (build 4)**
stays on the Releases page as it was (two unsigned zips whose
`toolbox_core.dll` names the build account's profile folder; the owner
chose to leave it).

Still true from 2026-11-19:
- **Both repos had their history rewritten on 2026-11-19** (a real
  hostname and other identifiers). In this repo the Mac snapshot is
  `3462de1` and the 0.1.0 commit `beea2b6`. **Any clone from before
  that, the Mac itself included, must `git fetch origin` and `git reset
  --hard origin/main` before other work and must never push its old
  history.** A clone of the Mac repo sits next to this one on the dev
  machine (`..\Toolbox`) with the old history and the old address as its
  `origin`: do not push from it. GitHub keeps unreferenced commits for
  some time; GitHub Support removes them on request.
- The GitHub account is `tacticalbadger`; old addresses redirect.
  `publish.ps1` takes the owner from `origin`.
- `build.ps1` keeps build paths out of the binaries and `package`
  refuses a build that names the profile folder.

Open for the owner since 0.2.1:
- The password fallback of the owner check (see Decisions). Try one vault
  unlock with the real account password; turn it off in the Vault menu if
  it is not wanted.
- A code-signing certificate, so coworkers stop seeing SmartScreen.
- Whether Windows should get the reduced-resolution switch at all (Parity,
  item 3), and whether NLA should be required for RDP once a real host is
  at hand to try it.
- Reset the Mac's clone and the old clone on this PC to the rewritten
  history before working there, and consider asking GitHub Support to
  remove the Mac repo's old, now unreachable commits.

**v0.2.1 (build 8)** was published from `ee88598` as two
self-contained, unsigned zips (win-arm64, win-x64) with SHA256SUMS, each
run against the fixture before publishing (x64 under emulation).

**What happened to 0.2.0.** The repository has had "immutable releases"
switched on since 0.1.0 (not by these sessions; v0.1.0 itself is not
immutable). `publish.ps1` published the release and then tried to attach
the files, which GitHub now refuses, so **v0.2.0 exists as a release with
notes and no downloads**, at commit `8ec9dc0`. Such a release cannot take
files later, and if it is deleted its tag name still cannot be used
again; it was left in place with a note pointing at 0.2.1. The script
now creates a draft, attaches the files and publishes last, and refuses
`-Replace` on an immutable release. Consequences to remember: a release
cannot be re-uploaded any more (the release notes can still be edited),
so check the packages before publishing, and every mistake costs a
version number. The same run showed that the script read the release
notes as ANSI; it reads UTF-8 now.