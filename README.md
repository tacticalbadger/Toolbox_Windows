# Toolbox for Windows

A client for the servers you touch every day: SSH sessions in split panes
and workspace tabs (the part Termius gets right), plus RDP and file
transfer with a credential vault, identities and 1Password and Active
Directory sync (the part Royal TS gets right). Rust for the connection
engine, C# and WPF for the app.

This is the Windows port of the macOS Toolbox app
(https://github.com/tacticalbadger/Toolbox). **The port is in progress**: see
"What works" and "Not there yet" below. The Mac app is preserved in this
repository's history (commit `3462de1`) as the reference for the port.
`CLAUDE.md` holds the decisions, machine setup and current state.

## Download

The latest build is on the
[Releases page](https://github.com/tacticalbadger/Toolbox_Windows/releases/latest):
take the zip for your PC (`win-x64` for most, `win-arm64` for Windows on
ARM), unblock it (right-click ▸ Properties ▸ Unblock), extract it anywhere
and run `Toolbox.exe`. Nothing needs installing. The builds are not
code-signed yet, so Windows asks before the first run: More info ▸ Run
anyway. The repository is private, so the link works for people who have
been given access to it.

## Layout

```
core/     Rust crate toolbox-core: SSH (russh), SFTP, FTP/FTPS, the vault,
          1Password and Active Directory providers, the connection store.
          Exposed to C# through UniFFI.
app/      C# WPF application (.NET 10): workspaces and panes, terminal
          (xterm.js in WebView2), RDP (Microsoft's mstscax control), file
          browser, sidebar tree, editors, vault key and owner check.
tools/    build.ps1, publish.ps1 and the end-to-end harness (tools\e2e).
docs/     Security review, release notes and Royal TS reference material.
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
| WebView2 Runtime | any current | part of Windows 11 |

Fetched on first build (network needed once): Rust crates from crates.io,
NuGet packages from nuget.org (WebView2 SDK, Windows SDK projection), and
xterm.js with three addons from the npm registry, pinned by version and
SHA-512 in `tools\build.ps1`.

Optional: 1Password CLI (`winget install AgileBits.1Password.CLI`, then
enable CLI integration in the 1Password app) for 1Password providers and
credentials.

## Build

```powershell
tools\build.ps1                  # core, terminal assets and app, for this machine's architecture
tools\build.ps1 run              # start build\app\win-<arch>\Toolbox.exe
tools\build.ps1 test             # Rust tests (in-process SSH, SFTP and FTP servers), then the app's unit tests
tools\build.ps1 app -Restart     # rebuild the app and relaunch it
tools\build.ps1 core app -Arch x64
tools\build.ps1 clean
```

Targets: `core` (Rust → `toolbox_core.dll` + generated C# bindings),
`assets` (xterm.js), `app` (WPF app), `test`, `run`, `clean`. Run `core`
again after any change to the Rust API: the bindings and the DLL are
generated, not in git. `app` will not overwrite a build that is running
unless `-Restart` is given.

Your connections, credentials, vault and settings live in
`%APPDATA%\Toolbox`; the log and the terminal's browser profile in
`%LOCALAPPDATA%\Toolbox`. Nothing is synced by git.

## Sharing builds with the team

```powershell
Set-Content VERSION 0.3.0                  # when the version should change
tools\build.ps1 core assets package        # build\release\Toolbox-<version>-win-arm64.zip (on an ARM64 PC)
tools\build.ps1 core package -Arch x64     # ...-win-x64.zip; cross-compiles, no extra tools needed
tools\publish.ps1                          # GitHub Release v<version> with the zips and SHA256SUMS
```

A package is a folder with everything inside, the .NET runtime included,
zipped. The build number is the commit count, and `publish.ps1` insists
that the packages were built from the current commit and that the commit
is pushed, so commit and push first, then package, then publish. It
writes the release notes: the standard install text plus the bullets in
`docs\release-notes\<version>.md` if that file exists. `-Replace`
re-uploads an existing release's files, `-DryRun` shows what would be
sent. No GitHub CLI is needed; the script uses the credential Git already
has for the repository.

Releases on this repository are immutable: once one is published, GitHub
accepts no change to its files, and its version number cannot be used for
another release. `publish.ps1` attaches the files to a draft and
publishes last, so test the packages and read the dry run first. (0.2.0
was published without files before the script did it in that order, and
stays empty; 0.2.1 is the same app.)

Build the core with `tools\build.ps1 core`, not a bare `cargo build`:
the script rewrites the source paths compilers leave in the DLL, which
would otherwise carry your account name to everyone who downloads the
package. `package` refuses a build that still has them.

Still to do here: an installer and a code-signing certificate. Until
then every recipient sees the SmartScreen warning once.

## What works

- Workspaces as tabs ("+" or Ctrl+Shift+T), any mix of sessions in each;
  double-click a tab to rename it. Split panes; rearrange by dragging a
  pane's header onto another pane's edge (split there) or middle (trade
  places); drag a connection from the sidebar onto a pane the same way.
  Divider positions are kept when panes come and go. Move a pane to
  another workspace from its header's menu or the Pane menu; the session
  keeps running. A session that ends cleanly closes its pane by itself;
  one that fails stays so the message can be read.
- SSH terminals (xterm.js in WebView2) with six themes and text-size
  shortcuts, known_hosts shared with Windows OpenSSH, key files (with a
  passphrase prompt), private keys from the vault or 1Password, and the
  SSH agent pipe (Windows OpenSSH or 1Password).
- RDP through Microsoft's own client control: NLA, the usual certificate
  warning when a server cannot be verified, and the host's modern
  graphics (the graphics pipeline with H.264 when the host offers it,
  decoded by the graphics card). The desktop follows the pane's size in
  real pixels and is shown pixel for pixel. While a window edge or a
  divider is dragged the picture is kept (shrunk to fit when the pane gets
  smaller) and the host is asked for the new size when you let go.
- The clipboard is shared with remote desktops: text and files, in both
  directions. This PC's clipboard is offered to a remote desktop only
  while its pane is the one you are working in; `docs/SECURITY-REVIEW.md`
  says exactly what a host can and cannot get at.
- View ▸ Remote Desktop has two switches for every RDP connection, both
  applied when a session connects: Fast Graphics (off decodes and draws
  in software, for a PC whose picture comes out garbled or black) and
  Share Clipboard.
- File Transfer connections over SFTP, FTP over TLS (explicit FTPS) or
  plain FTP: navigate, upload, download with progress, new folder, rename,
  delete. An FTPS server with an untrusted certificate asks first; plain
  FTP is labelled as unencrypted. Any SSH connection can open the same
  browser over SFTP.
- A sidebar tree like Royal TS: folders (nest with `/`), providers as
  folders in place with what they generate underneath, drag and drop
  between folders, and context menus on every node. The filter field
  above it narrows the whole tree. Expansion is remembered.
- Credentials: kept encrypted in the local vault, fetched from 1Password
  when used, or asked for. A connection can pick a credential (the picker
  groups vault, 1Password and ask-each-time entries and has a search
  field: type part of a name, username or folder and everything else is
  hidden), carry its own username and password (stored in the vault), or
  use its folder's
  credential (right-click a folder ▸ Set Credential…; nearest folder wins;
  such folders show a key badge).
- Providers: 1Password items become credentials through the `op` CLI;
  Active Directory computers become connections (Windows → RDP, macOS →
  VNC, Linux → SSH, OUs as folders). Each AD account (bind, domain
  controllers, other computers) can be an existing credential or typed
  into the provider and kept in the vault. Providers refresh at launch,
  on a timer, and on demand (Ctrl+Shift+R).
- The vault key is kept in `vault.key`, protected by Windows for your
  account (DPAPI). Before the vault unlocks, Windows confirms it is you:
  Windows Hello, or your account password where Hello is not set up.
  Vault ▸ Confirm It's You Before Unlocking turns that off (after one
  last confirmation).

### Keyboard

App shortcuts use Ctrl+Shift and Alt+Shift so that plain Ctrl+letter keys
keep going to the terminal.

| | |
|---|---|
| New workspace / connection | Ctrl+Shift+T / Ctrl+Shift+N |
| Split right / down | Alt+Shift+= / Alt+Shift+- |
| Next / previous pane | Ctrl+Shift+] / Ctrl+Shift+[ |
| Close pane / workspace | Ctrl+Shift+W / Ctrl+Alt+W |
| Move pane to a new workspace | Ctrl+Shift+M |
| Next / previous workspace | Ctrl+Tab / Ctrl+Shift+Tab |
| Rename workspace | Ctrl+Alt+R |
| Browse files of the focused session | Ctrl+Shift+F |
| Sync providers | Ctrl+Shift+R |
| Toggle sidebar | Ctrl+Shift+B |
| Terminal text size | Ctrl+= / Ctrl+- / Ctrl+0 |
| Terminal copy / paste | Ctrl+Shift+C / Ctrl+Shift+V (or right-click) |

## Not there yet

Compared with the Mac app: **VNC** (a VNC connection says so instead of
opening), code signing and an installer (releases are unsigned zips), the
app icon, and selecting several sidebar items at once. From the Mac's
0.3.0, one switch is missing: View ▸ Remote Desktop ▸ Retina Resolution,
which asks for a desktop with a quarter of the pixels and magnifies it.
Microsoft's control cannot magnify a picture dependably (its zoom leaves
graphics-pipeline output unscaled and takes the app down when it is set
at the wrong moment), so Windows always asks for the pane's full pixel
size. `CLAUDE.md` has the details and the order.

## Trying it without real hosts

`tools\e2e` packages a throwaway setup; nothing in it touches your real
data.

```powershell
tools\e2e\make-fixture.ps1                           # keys, known_hosts, fake op with a few dozen logins, store -> build\e2e
tools\e2e\make-rdp-server.ps1                        # once: builds the RDP test server (fetched from the Mac repository, a few minutes)
tools\e2e\servers.ps1 start                          # SSH+SFTP :2222, FTP :2121, FTPS :2122, RDP :3390 and :3391 (demo / secret)
tools\e2e\launch.ps1 --connect echo --files ftp-local   # relaunch against the fixture, owner check off
tools\e2e\capture.ps1                                # screenshot every Toolbox window into build\e2e\shots
tools\e2e\ui.ps1 tree                                # drive the app: tree, invoke, click, drag, keys, text
tools\e2e\servers.ps1 stop
```

The test servers are also available on their own:

```powershell
cd core
cargo run --example echo_server --features test-support -- --key <openssh-private-key>   # SSH + SFTP on 127.0.0.1:2222
cargo run --example ftp_server --features test-support -- --port 2121 [--tls]            # FTP or FTPS
```

The RDP test server on 3390 draws plain bitmaps and honours resize
requests; the one on 3391 offers the graphics pipeline. Both ask for
whatever the app announces on its clipboard and log its size, never its
contents. Their certificates are self-signed, so the certificate warning
comes first.

Launch arguments: `--connect <id>` and `--files <id>` (both may repeat),
`--expand-all`, and the testing aids `--demo-move`, `--demo-close`,
`--demo-split` (splits the focused pane, which resizes what is in it),
`--edit-connection <id|new>`, `--edit-credential <id|new>`,
`--edit-provider <id|new>`, `--set-folder-credential <path>`,
`--demo-credential-search <text>` (credential pickers open with that
search typed), `--demo-provider-account <provider-id>`, `--unlock-vault`
and `--assume-keyboard` (treats remote desktop panes as the one in use
when the app cannot be in front; for clipboard checks, never for normal
use). Environment
overrides: `TOOLBOX_STORE_PATH`, `TOOLBOX_VAULT_PATH`,
`TOOLBOX_VAULT_KEY_PATH`, `TOOLBOX_SETTINGS_PATH`, `TOOLBOX_KNOWN_HOSTS`,
`TOOLBOX_LOG_PATH`, `TOOLBOX_WEBVIEW_DATA`.

## Security

`docs/SECURITY-REVIEW.md` records the review findings and residual risks,
with an addendum per security-relevant change (most recently the review
for 0.2.1: clipboard sharing with remote desktops, and what was hardened
after reading the whole Windows app again).

## License

Toolbox is proprietary software, not open source. Copyright (c) 2026
tacticalbadger. All rights reserved.

The source is public so that it can be read. You may download and run the
released app. Copying, changing or redistributing the code or the app needs
written permission. The terms are in [`LICENSE`](LICENSE).
