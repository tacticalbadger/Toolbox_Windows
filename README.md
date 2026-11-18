# Toolbox

A native macOS client for the servers you touch every day: SSH sessions in
split panes and workspace tabs (the part Termius gets right), plus RDP and VNC
with a credential vault, identities and 1Password sync (the part Royal TSX gets
right). Swift for the app, Rust for the connection engine.

## Layout

```
core/     Rust crate toolbox-core: SSH (russh), SFTP, RDP (IronRDP), the vault,
          1Password and Active Directory providers, the connection store.
          Exposed to Swift through UniFFI.
bridge/   Build scripts: compile the core and generate Swift bindings
          (build-core.sh), bundle the app (bundle-app.sh), local code-signing
          identity (signing-identity.sh), icon rendering.
App/      SwiftPM package (and an optional XcodeGen project.yml). AppKit
          shell, SwiftUI for forms, SwiftTerm for terminals, RoyalVNCKit for VNC.
tools/e2e Throwaway fixture, local test servers and screenshot helpers.
docs/     Security review and Royal TS reference material.
```

## Dependencies

Required on every Mac that builds the app:

| What | Version known to work | Install |
|---|---|---|
| macOS on Apple silicon | 14 or newer (built on 26.7) | |
| Xcode | 16 or newer (built with 27.2) | App Store; open it once so it finishes installing components |
| Command Line Tools | matching Xcode | `xcode-select --install` |
| Rust (rustup, stable) | 1.100 | `curl https://sh.rustup.rs -sSf \| sh` |
| git | any recent | ships with the Command Line Tools |

The Xcode license does not need to be accepted for `make app`: the build
uses the Command Line Tools' Swift and borrows only Xcode's SwiftUI macro
plugin, from `/Applications/Xcode.app`. Keep Xcode at that path.

Fetched automatically on first build (network needed once):

- Rust crates from crates.io, plus IronRDP from GitHub pinned to one
  revision in `core/toolbox-core/Cargo.toml`.
- Swift packages: SwiftTerm 1.99.3 and RoyalVNCKit (pinned revision), from
  `App/Package.swift` and `App/Package.resolved`.

Optional:

| What | For | Install |
|---|---|---|
| 1Password CLI (`op`) | 1Password providers and credentials | `brew install 1password-cli`, then enable CLI integration in the 1Password app |
| XcodeGen | `make project` / `make xcode` (also needs `sudo xcodebuild -license accept`) | `brew install xcodegen` |
| cargo-audit | dependency advisories | `cargo install cargo-audit` |
| GitHub CLI | pull requests and releases | `brew install gh` |

## Build on a fresh Mac

```sh
git clone https://github.com/mattdobbs/Toolbox.git && cd Toolbox
make core      # first run downloads and compiles the Rust crates (several minutes)
make app       # first run resolves the Swift packages; produces build/Toolbox.app
make signing   # once per Mac; asks for your login password (see below)
make app       # rebuild so the bundle carries the stable signature
make run       # or: open build/Toolbox.app
make test      # Rust unit and integration tests
```

On a Mac that already has a clone: `git pull`, then `make core && make app`.
Always re-run `make core` after pulling, since the Swift bindings and the
static library are generated from the Rust code and are not in git.

Your connections, credentials and vault live in
`~/Library/Application Support/Toolbox/` on each Mac and are not synced by
git. The vault key is in that Mac's login Keychain.

## Build targets

```sh
make core      # Rust core → Swift bindings + static library (release)
make app       # SwiftPM build wrapped into build/Toolbox.app (no Xcode license needed)
make run       # build and launch
make test      # Rust unit + integration tests (in-process SSH, SFTP and RDP servers)
make signing   # one-time local code-signing identity
make icon      # re-render the app icon
make project   # App/project.yml → App/Toolbox.xcodeproj (XcodeGen)
make xcode     # build with xcodebuild (after sudo xcodebuild -license accept)
make clean     # remove all build output
```

### Code signing and the Keychain prompt

macOS remembers a Keychain "Always Allow" per code signature. Without a
signing identity `make app` signs ad hoc, and that signature changes with
every build, so the "Toolbox wants to use your confidential information"
dialog for the vault key comes back after each rebuild. `make signing`
fixes it once per Mac by creating and trusting a "Toolbox Local Signing"
certificate; the next launch asks once, then stays quiet.

`make app` prefers, in order: `TOOLBOX_SIGN_IDENTITY`, a Developer ID
Application identity, an Apple Development identity, Toolbox Local Signing,
then ad hoc (with a warning). To hand the app to coworkers, sign it with a
Developer ID and notarize it; a self-signed build makes them right-click →
Open the first time, and they answer the Keychain dialog once.

The remaining prompt, Touch ID (or the login password) before the vault
unlocks, is deliberate and can be turned off with Toolbox ▸ Require Touch ID
to Unlock Vault.

## What works

- Workspaces as native window tabs, split panes, SSH terminals (SwiftTerm on
  Metal) with themes and text-size shortcuts. Arrange panes by dragging:
  grab a pane's header and drop it on another pane's edge to split there or
  on its middle to swap; drag a connection from the sidebar onto a pane the
  same way to open it in place. Divider positions are kept when panes come
  and go. Sessions follow their pane: terminals reflow and RDP desktops are
  renegotiated at the new pixel size. A session that ends cleanly (RDP
  sign-out, shell logout) closes its pane by itself; one that fails stays so
  the message can be read.
- Workspaces are tabs (the bar stays visible; "+" or ⌘T adds one) and any mix
  of sessions can live in any of them. Move a pane to another workspace from
  its header's context menu or the Pane menu (⌘⇧M for a new workspace); the
  session keeps running through the move. Double-click a tab (or press ⌘⇧R)
  to rename the workspace.
- A sidebar tree like Royal TS: folders you create (nest them with `/`),
  providers as folders in place with what they generate underneath (an
  Active Directory provider shows its OU hierarchy), drag and drop between
  folders, context menus on every node, and flat "All Connections" and "All
  Credentials" lists at the bottom. Expansion is remembered.
- Credentials: username plus password or SSH key, kept encrypted in a local
  vault (key in the login Keychain, unlocked with Touch ID), fetched from
  1Password at connect time, or prompted for. A connection can pick an
  existing credential (the picker groups vault, 1Password and ask-each-time
  entries), carry its own username and password (stored in the vault just
  for that connection; the username lives under the method, not on the
  connection), or use its folder's credential: right-click a folder
  and choose Set Credential… and every connection underneath set to "Use the
  folder's credential" uses it, nearest folder first. Folders that hand one down show
  a key badge.
- Providers (dynamic folders): a 1Password provider turns vault items into
  credentials via the `op` CLI; an Active Directory provider turns computer
  objects into connections (Windows → RDP, macOS → VNC, Linux → SSH, OUs as
  folders, Domain Controllers get the admin credential). Each of the AD
  provider's accounts (bind, domain controllers, other computers) can use an
  existing credential or a username and password typed into the provider,
  which is kept in the vault as a credential inside the provider's folder.
  Providers refresh at launch and on a timer, or on demand with ⌘R.

- RDP through IronRDP: NLA logon, the desktop follows the pane size through
  Display Control (falling back to a reconnect at the new size), frames land
  in an IOSurface-backed layer, full keyboard and mouse.
- VNC through RoyalVNCKit, the engine Royal TS itself ships.
- File Transfer connections (Type ▸ File Transfer) over SFTP, FTP over TLS
  (explicit FTPS) or plain FTP, opening a file browser: navigate, upload,
  download with progress, new folder, rename and delete. An FTPS server with
  a self-signed or private certificate asks before connecting; plain FTP is
  labelled as unencrypted. Any SSH connection can also open the same browser
  over SFTP (sidebar context menu, ⌘⇧F, or the toolbar).

## Trying it without real hosts

`tools/e2e/` packages the throwaway setup used for end-to-end checks:
`make-fixture.sh` (keys, known_hosts, fake `op`, store), `servers.sh start`
(echo SSH+SFTP on 2222, RDP on 3390), `launch.sh --connect echo` and
`capture.sh` for screenshots. `CLAUDE.md` holds the working notes, machine
setup and current state for whoever (or whatever) picks the project up.

```sh
cd core
cargo run --example echo_server --features test-support -- --key <openssh-private-key>   # SSH + SFTP on 127.0.0.1:2222, matt / secret
                                                       # add --exit-after <secs> to have shells log out on their own
cargo run --example rdp_server --features test-support                                   # RDP on 127.0.0.1:3390, matt / secret
cargo run --example ftp_server --features test-support -- --port 2121 [--tls]            # FTP or FTPS, matt / secret
```

Launch overrides for a throwaway setup: `TOOLBOX_STORE_PATH`,
`TOOLBOX_VAULT_PATH`, `TOOLBOX_KNOWN_HOSTS`, `--connect <id>` to open a
connection on start, `--files <id>` to open its SFTP browser (both may repeat, each
opening another pane), `--expand-all` to expand the whole sidebar tree, `--demo-move` to move the
focused pane into a new workspace after launch, `--demo-close` to close the focused pane after launch,
`--edit-connection <id|new>` and
`--set-folder-credential <path>` to open those sheets at launch, `--unlock-vault` to unlock the vault at launch and
log the outcome, `TOOLBOX_KEYCHAIN_SERVICE` to use a separate Keychain item for the vault key, and
the standard defaults override `-vaultRequiresAuth NO` to skip the Touch ID step for that run.

`make icon` re-renders the app icon from `bridge/make-icon.swift`.
`docs/SECURITY-REVIEW.md` records the review findings and residual risks.

## Next

Clipboard and audio for RDP, a client-side RDP pointer, per-connection RDP
certificate verification, session restore, broadcast input, jump hosts and
port forwards.

`docs/royalts-reference/` holds the Royal TSX scripts the providers replace.
