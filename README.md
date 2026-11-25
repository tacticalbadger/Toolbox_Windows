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
take the files for your PC (`win-x64` for most, `win-arm64` for Windows
on ARM).

- **The installer**, `Toolbox-<version>-win-<arch>-setup.exe`: run it. It
  installs for your Windows account only, asks for no administrator, puts
  Toolbox in the Start menu, and is removed again under Settings ▸ Apps ▸
  Installed apps.
- **The disc image**, `Toolbox-<version>-win-<arch>.iso`: double-click it
  and Windows opens it as a disc holding that installer and a `Toolbox`
  folder. Run the installer, or copy the folder to a place of your own
  (for example `%LOCALAPPDATA%\Programs`) and run `Toolbox.exe` from the
  copy.
- The `.zip` is the same folder again; it is what a copy of Toolbox
  downloads when it updates itself.

The builds are not code-signed yet, so Windows asks before the first
run: More info ▸ Run anyway.

That is the last download by hand: Toolbox looks for a new version once
a day and under Help ▸ Check for Updates…, shows what changed, and
installs it when you agree (see "Updates" below). An installed copy and
a copied folder update themselves the same way.

## Layout

```
core/     Rust crate toolbox-core: SSH (russh), SFTP, FTP/FTPS, the vault,
          1Password and Active Directory providers, the connection store.
          Exposed to C# through UniFFI.
app/      C# WPF application (.NET 10): workspaces and panes, SSH terminal
          (xterm.js in WebView2), local terminals (a window of Windows
          Terminal in a pane), RDP (Microsoft's mstscax control), file
          browser, sidebar tree, editors, vault key and owner check,
          updates.
tools/    build.ps1, publish.ps1, update-key.ps1 and the end-to-end
          harness (tools\e2e).
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
| Inno Setup (only for `package`) | 7.1.1 | `winget install --id JRSoftware.InnoSetup.7 -e --custom /CURRENTUSER` (no administrator needed) |
| WebView2 Runtime | any current | part of Windows 11 |

Fetched on first build (network needed once): Rust crates from crates.io,
NuGet packages from nuget.org (WebView2 SDK, Windows SDK projection), and
xterm.js with four addons (fit, web links, WebGL, search) from the npm
registry, pinned by version and SHA-512 in `tools\build.ps1`.

Optional: 1Password CLI (`winget install AgileBits.1Password.CLI`, then
enable CLI integration in the 1Password app) for 1Password providers and
credentials. Windows Terminal for local terminals (part of Windows 11; in
the Microsoft Store for Windows 10).

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
`assets` (xterm.js), `app` (WPF app), `test`, `run`, `package`, `feed`,
`clean`. Run `core`
again after any change to the Rust API: the bindings and the DLL are
generated, not in git. `app` will not overwrite a build that is running
unless `-Restart` is given.

Your connections, credentials, vault and settings live in
`%APPDATA%\Toolbox`; the log and the terminal's browser profile in
`%LOCALAPPDATA%\Toolbox`. Nothing is synced by git.

## Sharing builds with the team

```powershell
Set-Content VERSION 0.3.1                  # when the version should change
tools\build.ps1 core assets package        # build\release\Toolbox-<version>-win-arm64: -setup.exe, .iso and .zip (on an ARM64 PC)
tools\build.ps1 core package -Arch x64     # ...-win-x64; cross-compiles, no extra tools needed
tools\build.ps1 feed                       # update.json and its signature: what installed copies read
tools\e2e\check-disc.ps1                   # the disc image opened as a drive, the app started from it (-Arch x64 for the other)
tools\e2e\check-installer.ps1              # the installer run for real: install, self-update, uninstall (-Arch x64 likewise)
tools\publish.ps1                          # GitHub Release v<version> with all of them, SHA256SUMS and the feed
tools\e2e\check-published-update.ps1 -From <previous version>   # afterwards: that release takes the new one from GitHub
```

A package is a folder with everything inside, the .NET runtime included,
handed out three ways: an installer (made with Inno Setup from
`tools\installer\Toolbox.iss`), a disc image holding that installer and
the folder (made with what Windows has for writing discs), and a zip for
copies that update themselves. The installer is per user and needs no
administrator, because the app replaces its own files when it updates
and so has to live where its user may write; that is also why it is not
an MSI. The build number is the commit count, and `publish.ps1` insists
that the packages were built from the current commit and that the commit
is pushed, so commit and push first, then package, then publish. It
writes the release notes: the standard install text plus the bullets in
`docs\release-notes\<version>.md` if that file exists (the same bullets
are what the update window shows). `-Replace` re-uploads an existing
release's files, `-DryRun` shows what would be sent. No GitHub CLI is
needed; the script uses the credential Git already has for the
repository.

Publishing is the moment installed copies start being offered the
version. `feed` needs both packages and the update key, and `publish.ps1`
refuses to go on without a feed that describes exactly the files it is
about to upload and carries the key's signature: a release without one
would silently stop updates for everyone until the next release.

**The update key.** Updates are signed with one ECDSA P-256 key. Its
public half is in the app (`app\Toolbox\Support\Updates.cs`); its private
half is in the Windows key store of the account releases are made from,
under the name "Toolbox Update Key", and nowhere else: not in a file, not
in the repository. `tools\update-key.ps1` is how it is handled:

```powershell
tools\update-key.ps1 check                 # is the key in this account the one the app trusts?
tools\update-key.ps1 export -To key.txt    # the private half, for a password manager; delete the file afterwards
tools\update-key.ps1 import -From key.txt  # on another PC that should make releases
```

Keep a copy in a password manager. Whoever has the key can make every
installed copy install anything; and without it no update can be made
that installed copies accept: there is no second way in, so a lost key
means everyone downloads the next version by hand.

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

Still to do here: a code-signing certificate. Until then every
recipient sees the SmartScreen warning once, for the installer as for
the app.

## What works

- Every connection opens in a workspace tab of its own, named after it:
  SSH, remote desktop, file transfer and local terminals alike. Put
  sessions side by side when you want to: split a pane and open into the
  empty half, drop a connection from the sidebar on a pane's edge, or move
  a pane to another workspace from its header's menu or the Pane menu (the
  session keeps running). Rearrange by dragging a pane's header onto
  another pane's edge (split there) or middle (trade places). Divider
  positions are kept when panes come and go. A tab closes when its last
  session ends or is moved away; a session that fails stays so the message
  can be read. Double-click a tab to give it a name of your own; clear the
  name to go back to the automatic one.
- SSH terminals (xterm.js in WebView2) with six themes and text-size
  shortcuts, known_hosts shared with Windows OpenSSH, key files (with a
  passphrase prompt), private keys from the vault or 1Password, and the
  SSH agent pipe (Windows OpenSSH or 1Password). Edit ▸ Find
  (Ctrl+Shift+F) searches the text of a terminal, including what has
  scrolled out of view; Enter and Shift+Enter (or F3 and Shift+F3) step
  through the matches.
- Local terminals: File ▸ New Local Terminal (Ctrl+Alt+T, or the toolbar
  button) puts a window of your own Windows Terminal in a tab, with your
  default profile; New Local Terminal With lists your other profiles. It
  is Windows Terminal itself, so its colours, fonts and key bindings are
  the ones you set there, and the app's themes, text size and find bar do
  not apply to it (Terminal has its own find on Ctrl+Shift+F). The tab
  carries the window's title. Closing an idle shell asks nothing; one that
  is running a program names it first. While a local terminal has the
  keyboard every key goes to Windows Terminal, the app's shortcuts
  included: click another pane or the sidebar to get them back.
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
  above it narrows the whole tree. Which folders are open is remembered,
  also across renaming or moving them. Only the rows on screen are built,
  so a tree of a few thousand computers stays quick to filter.
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
  on a timer, and on demand (Ctrl+Shift+R). A provider is a folder named
  after it: rename the provider and the folder is renamed with everything
  inside; a folder holds one provider.
- Updates: Help ▸ Check for Updates…, and once a day by itself unless
  Help ▸ Check for Updates Automatically is switched off. A new version is
  shown with its notes and installed when you agree; installing restarts
  the app, so it asks first when sessions are open. See "Updates" below.
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
| New local terminal | Ctrl+Alt+T |
| Split right / down | Alt+Shift+= / Alt+Shift+- |
| Next / previous pane | Ctrl+Shift+] / Ctrl+Shift+[ |
| Close pane / workspace | Ctrl+Shift+W / Ctrl+Alt+W |
| Move pane to a new workspace | Ctrl+Shift+M |
| Next / previous workspace | Ctrl+Tab / Ctrl+Shift+Tab |
| Rename workspace | Ctrl+Alt+R |
| Find in the terminal / next / previous | Ctrl+Shift+F / Ctrl+Shift+G / Ctrl+Alt+G |
| Browse files of the focused session | Ctrl+Alt+F (was Ctrl+Shift+F up to 0.2.1) |
| Sync providers | Ctrl+Shift+R |
| Toggle sidebar | Ctrl+Shift+B |
| Terminal text size | Ctrl+= / Ctrl+- / Ctrl+0 |
| Terminal copy / paste | Ctrl+Shift+C / Ctrl+Shift+V (or right-click) |

## Updates

Once a day, and when asked, Toolbox reads a small file attached to the
newest release on GitHub (`update.json`). An update is installed only if
that file carries a signature made with Toolbox's update key and the
downloaded package is exactly the file it describes (length and SHA-256):
whoever controls the place the files are served from can withhold an
update but not supply one. A check sends a request for that file with the
app's version, build number and processor type, and nothing about your
connections or credentials.

Installing replaces the files in the folder Toolbox runs from, so that
folder has to be one you may write to (not Program Files); otherwise the
app says so and you replace the folder by hand. Your connections, vault
and settings are elsewhere and are not touched. A copy you built yourself
(`tools\build.ps1 app`) does not look for updates.

## Not there yet

Compared with the Mac app: **VNC** (a VNC connection says so instead of
opening), code signing (the installer and the app are unsigned), the
app icon, and selecting several sidebar items at once. Local terminals
are Windows Terminal's windows where the Mac runs a shell in its own
terminal: the app's themes, text size and find bar do not reach them, and
its shortcuts do not work while one has the keyboard. From the Mac's
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
tools\e2e\update-rehearsal.ps1                       # a whole self-update from a test feed, and the tampering it must refuse
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

Launch arguments: `--connect <id>`, `--files <id>` and `--local-terminal`
(all may repeat; the first session takes the empty workspace, every
further one a tab of its own), `--local-terminal-profile <name>`,
`--expand-all`, and the testing aids `--demo-move` (the focused pane to a
new workspace, when it shares one), `--demo-merge` (every other tab's
sessions into the first workspace), `--demo-select-tab <n>`,
`--demo-close`, `--demo-split` (splits the focused pane, which resizes
what is in it), `--demo-find <text>` (searches the focused terminal and
logs what was found), `--edit-connection <id|new>`,
`--edit-credential <id|new>`, `--edit-provider <id|new>`,
`--set-folder-credential <path>`, `--demo-credential-search <text>`
(credential pickers open with that search typed),
`--demo-provider-account <provider-id>`,
`--demo-provider-rename <provider-id> <name>`,
`--demo-folder-rename <path> <new path>`, `--unlock-vault`,
`--assume-keyboard` (treats remote desktop panes as the one in use when
the app cannot be in front; for clipboard checks, never for normal use)
and `--rdp-expose-control` (puts the Remote Desktop control's windows
back into the accessibility tree, which brings back the lost keystrokes
0.3.0 fixed; for seeing the difference). The update rehearsal's aids
(`--updater-feed`, `--updater-key`, `--updater-pretend-build`,
`--demo-update`, `--demo-update-install`) are honoured only in a run
against a store named in the environment. Environment overrides:
`TOOLBOX_STORE_PATH`, `TOOLBOX_VAULT_PATH`, `TOOLBOX_VAULT_KEY_PATH`,
`TOOLBOX_SETTINGS_PATH`, `TOOLBOX_KNOWN_HOSTS`, `TOOLBOX_LOG_PATH`,
`TOOLBOX_WEBVIEW_DATA`, `TOOLBOX_UPDATES_DIR`.

## Security

`docs/SECURITY-REVIEW.md` records the review findings and residual risks,
with an addendum per security-relevant change (most recently the review
for 0.3.0: updates over the air and the key they depend on, local
terminals as windows of another program, and the search addon in the
terminal page).

## License

Toolbox is proprietary software, not open source. Copyright (c) 2026
tacticalbadger. All rights reserved.

The source is public so that it can be read. You may download and run the
released app. Copying, changing or redistributing the code or the app needs
written permission. The terms are in [`LICENSE`](LICENSE).
