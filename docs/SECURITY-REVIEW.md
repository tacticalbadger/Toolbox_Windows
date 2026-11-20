# Security and bug review — 2026-11-13

Scope: the whole repository as of the phase 4 build (SSH, SFTP, RDP, VNC,
vault, credentials, 1Password and Active Directory providers). Method: a
read-through of every Rust and Swift source file, `cargo clippy` on all
targets, `cargo audit` on the dependency tree, and the end-to-end runs
against the in-process SSH and RDP servers.

## Fixed in this pass

| Area | Finding | Fix |
|---|---|---|
| Core | A panic while holding a mutex would poison it and turn every later lock into a crash | `lock_or_recover` recovers the guard everywhere the core locks |
| Core | Records holding passwords derived `Debug`, so a stray log line could print them | Redacting `Debug` impls for `SshAuth`, `RdpConfig`, `BindCredentials`, `Secret`, `ResolvedCredential` |
| Core | `connections.json` and `vault.json` were written with default permissions | Written as mode 0600 |
| SFTP | A hostile server could answer a listing with names like `../x` and steer a download outside the chosen folder | Core drops names containing `/`, NUL, `.` or `..`; the app only ever writes the last path component |
| SSH | No timeout on the authentication exchange, so a server that accepts TCP but never answers would hang the pane | 60 s auth timeout |
| Vault | If the owner check (Touch ID or password) was unavailable the vault unlocked anyway | Unlock now refuses with a reason |
| App | Closing a pane while its credentials were still being resolved left a connected "zombie" session with no UI | Terminal, RDP, VNC and SFTP panes ignore a late start after close |
| App | An unreadable store could crash the app at launch | Falls back to a temporary store with an alert |
| App | Passwords lingered in closed RDP/VNC views | Cleared on terminate |

## Reviewed and acceptable

- **Host keys.** Unknown keys prompt with the SHA-256 fingerprint and are
  recorded in `~/.ssh/known_hosts`; a changed key refuses to connect.
- **Secrets at rest.** Vault entries use XChaCha20-Poly1305 with a random
  nonce and the entry id as associated data; the key is random, lives in the
  login Keychain as this-device-only, and is held in memory behind
  `Zeroizing`. 1Password items are fetched at use time and never written.
- **Subprocess use.** The `op` CLI is run with an argument vector, never a
  shell, with user-configured vault and account names as separate arguments.
- **LDAP.** LDAPS first; the plain-LDAP fallback is a per-provider setting
  that defaults on to match the previous Royal TS script, and a sync that
  used it reports a warning. Certificate validation defaults on.
- **Remote content.** Terminal OSC 52 clipboard reads are denied; link
  opening is limited to http, https and mailto.
- **Logging.** No code path logs credentials; error messages carry host and
  user names only.

## Residual risks and known gaps

- **RDP server certificates are accepted without validation.** This matches
  the default of most RDP clients for self-signed hosts, and NLA (CredSSP)
  still binds the server's TLS key into the credential exchange, but a
  per-connection "verify certificate" setting is the right next step.
- **`rsa` crate advisory RUSTSEC-2023-0071 (medium).** Timing side channel in
  RSA private-key operations. It is reached only when authenticating with an
  RSA SSH key, the version is pinned by russh, and the fix ships with the
  crate's final release. Ed25519 keys are unaffected.
- **Unmaintained transitive crates** flagged by `cargo audit`:
  `atomic-polyfill`, `basic-toml`, `pin-utils`, `rustls-pemfile`. No known
  vulnerabilities; worth re-checking when upstreams move.
- **Secrets in Swift strings** (prompts, resolved passwords) cannot be
  zeroized; they are released as soon as the session starts.
- **VNC clipboard redirection is on by default**, as in Royal TS. Turn it off
  in `VncSessionView` if sharing the clipboard with a remote is unwanted.
- **No hardened runtime or sandbox yet.** The SwiftPM bundle is ad-hoc
  signed; enable the hardened runtime and a signing identity in the Xcode
  build before distributing.
- **Launch overrides** (`TOOLBOX_STORE_PATH`, `TOOLBOX_VAULT_PATH`,
  `TOOLBOX_KNOWN_HOSTS`, `--connect`, `--files`) exist for testing. They only
  affect the user's own process.

## Not covered

Live testing against a Windows RDP host, a real VNC server, an Active
Directory domain controller, and the Keychain/Touch ID unlock, none of which
are reachable from the development machine.

## Addendum — 2026-11-13, later the same day

Reviewed with the sidebar tree, pane arranging, workspace moves and
automatic pane close added.

- **Drag and drop** uses app-private pasteboard types carrying only node or
  pane ids; drops from other applications are ignored. Folder moves refuse a
  folder dropped into itself, and provider-generated items cannot be moved.
- **Folder paths** are normalized in the core (empty and whitespace-only
  components dropped), so the slash-separated paths in the store cannot
  contain empty segments; deleting a folder requires it to be empty.
- **Moving a pane between workspaces** re-parents the live view; the
  session's credentials are not touched or re-resolved, and the source pane
  is closed without terminating the session.
- **Automatic close** fires only on an end the core marks clean (remote
  logout or sign-out, or a close started here). Failures keep the pane with
  the error text, so a dropped connection cannot silently vanish.
- A crash was fixed where a split view acting as its own delegate recursed
  in AppKit; the delegate is now a separate object.

Verification: 33 core tests, clippy clean, `cargo audit` unchanged from the
morning (one advisory on the pinned `rsa` release candidate, four
unmaintained transitive crates), end-to-end runs for the three-pane layout,
the workspace move and the automatic close.

## Addendum — 2026-11-13, evening: Keychain prompts and code signing

The owner reported a second password dialog ("Toolbox wants to use your
confidential information stored in 'Toolbox vault key'") that came back even
after Always Allow. Cause: the SwiftPM bundle was signed ad hoc, so its
designated requirement was `cdhash H"…"`, which changes with every build, and
the Keychain stores Always Allow against exactly that requirement.

- **Stable signature.** `make app` now signs with a certificate when one
  exists (`TOOLBOX_SIGN_IDENTITY`, Developer ID Application, Apple
  Development, or the "Toolbox Local Signing" identity that `make signing`
  creates) and warns when it falls back to ad hoc. A certificate-based
  requirement (`identifier "com.dobbs.Toolbox" and certificate leaf = H"…"`)
  does not change between builds.
- **The local identity** is a self-signed certificate in the login keychain,
  trusted for code signing by the user in the one-time `make signing` step
  (macOS asks for the login password). It proves nothing to other Macs;
  Gatekeeper still treats the app as unidentified. It only gives the Keychain
  a stable name for the app. Distribution should use Developer ID plus
  notarization, which the script path already supports (hardened runtime
  and timestamp are added for Developer ID identities).
- **Re-owning the key.** When the signing certificate changes, VaultManager
  rewrites the vault-key item so the running build is its creator (staged
  copy, delete old, rename), so a single "Allow" is enough and the key is
  present in at least one item at every step. Ad-hoc builds skip this; they
  have nothing stable to bind to.
- **Rejected alternative.** An identifier-only designated requirement would
  have kept the prompt away for ad-hoc builds too, but any app claiming the
  bundle id could then read the key once the user had allowed it once.
- **Testing aids** added: `TOOLBOX_KEYCHAIN_SERVICE` (separate Keychain
  item, labelled with the service name), `--unlock-vault` (unlock at launch
  and log the outcome) and the standard `-vaultRequiresAuth NO` defaults
  override. They only affect the user's own process; the Touch ID gate is a
  user preference (menu toggle, UserDefaults) the user can change anyway.
- **Denied access** now reads "Access to the vault key in the Keychain was
  denied" instead of a raw status.

Verification: the ad-hoc build creates its own test item
(`TOOLBOX_KEYCHAIN_SERVICE`) and re-reads it on two further launches of the
same build without any SecurityAgent dialog ("vault unlocked" logged each
time). The certificate-signed path could not be exercised in this session
because `make signing` needs the user's login password for the trust step;
the first signed build will ask once for the existing item and must stay
quiet across rebuilds after that.

## Addendum — 2026-11-13, night: connection credentials and folder inheritance

- **Stored passwords.** A connection set to "Username and password" keeps the
  password in the vault under the connection's own id (same XChaCha20-Poly1305
  vault, same Touch ID gate); the store document still never holds a secret.
  Deleting the connection, or switching it to another method, removes the
  secret when the vault is unlocked; a locked vault leaves an orphaned entry
  that nothing references, as with deleted vault credentials.
- **Folder credentials** are a path → credential-id map in the store. The
  core validates the credential exists when assigning, drops assignments when
  the credential or folder is deleted, and carries them through renames.
  Resolution walks up from the connection's folder and ignores assignments
  whose credential no longer exists, so a stale mapping cannot resolve to
  the wrong account. Generated (AD) connections keep their explicit
  credentials; inheritance only applies to connections that opt in.
- **Testing aids** `--edit-connection` and `--set-folder-credential` open
  sheets in the user's own process and touch nothing else.
- Core tests: 35 pass (two new: folder inheritance across renames, deletes
  and reopen; auth-kind JSON round trip). End-to-end: a connection in a
  folder with a prompt-type credential asked for that credential's username;
  a stored-password connection with nothing in the vault reported it instead
  of connecting.

## Addendum — 2026-11-16: File Transfer connections and provider accounts

- **FTP** sends the password and file contents unencrypted. The editor
  labels it "FTP (not encrypted)" with a warning, and the browser's status
  line says so while connecting. SFTP and FTPS are offered first.
- **FTPS** verifies the server certificate with the system trust store.
  When verification fails the user sees the reason and decides; "Connect
  Anyway" keeps encryption but not server authentication, for that one
  connection only (nothing is remembered). Tests cover refuse, accept and
  pre-approved paths.
- **Listing names** from FTP servers get the same guard as SFTP: names
  containing `/`, NUL, `.` or `..` are dropped, so a hostile server cannot
  steer a download outside the chosen folder.
- **Provider-owned accounts** for AD are ordinary vault credentials (same
  encryption and Touch ID gate), removed with their secret when the
  provider stops using them or is deleted.
- **Active Directory finding**: every domain controller behind
  the AD domain used for testing presents a 1024-bit RSA certificate, which macOS
  refuses. Verified LDAPS is impossible until those are reissued; the
  "Accept untrusted certificates" option keeps the bind encrypted but
  unauthenticated, which is still far better than the plain-LDAP fallback
  (that sends the bind password in clear and defaults to on — flagged to
  the owner, not changed).
- `cargo audit` unchanged by the new crates (suppaftp, async-native-tls;
  libunftp only under `test-support`).


## Addendum — 2026-11-18: Windows port, core changes

The core now builds and its 37 tests pass on Windows (ARM64). What changed
that touches security:

- **File permissions.** `restrict_permissions` set mode 0600 on
  `connections.json` and `vault.json`. On Windows it is a no-op: the files
  live under `%APPDATA%\Toolbox`, whose inherited ACL already grants
  access to the owning user, SYSTEM and Administrators only. Nothing is
  made more open than before; the function keeps its Unix behaviour
  behind `cfg(unix)`. If the store path is ever pointed outside the
  profile (the `TOOLBOX_STORE_PATH` override), the caller owns the ACL.
- **SSH agent.** On Windows the agent is reached through the named pipe
  `\.\pipe\openssh-ssh-agent` (Windows OpenSSH; 1Password's SSH agent
  serves the same name). An explicit `socket_path` on the connection or an
  `SSH_AUTH_SOCK` that names a `\.\pipe\` path overrides it. Named pipes
  are local-only and the OpenSSH agent checks the connecting user; same
  trust model as the Unix socket. Not yet exercised: the agent service is
  disabled on the dev machine.
- **1Password CLI lookup** now searches `PATH` before the known install
  folders (winget links, `C:\Program Files\1Password CLI`), on all
  platforms. A hostile `op.exe` earlier on `PATH` could impersonate the
  CLI, the same exposure as any tool launched by name; an explicit path in
  the provider settings still wins and is the recommendation for shared
  machines.
- **Home directory** for `~/` expansion comes from `std::env::home_dir`
  (`USERPROFILE` on Windows) instead of `HOME`.
- **IronRDP removed** with `rdp.rs` and its test server; the Windows app
  will use Microsoft's RDP client control, which brings its own
  certificate and CredSSP handling. The review items about RDP
  certificate verification no longer apply to the core.
- The vault key is no longer in a Keychain. Its Windows storage (DPAPI
  blob, Windows Hello gate) is app-side work still to come and gets its
  own addendum.

## Addendum — 2026-11-19: the Windows app (vault key, owner check, terminal, RDP)

The WPF shell now exists. What it adds or changes that touches security:

- **Vault key storage.** The 32-byte key is generated by the core on first
  use and kept in `vault.key` next to the vault (`%APPDATA%\Toolbox`),
  encrypted with DPAPI for the signed-in Windows account
  (`ProtectedData`, CurrentUser scope, a fixed purpose string as optional
  entropy). The file is JSON (`version`, `protection`, base64 blob) so a
  stronger scheme can replace it later. The key is handed to the core for
  the unlock call and the managed copy is zeroed afterwards.
  *What DPAPI does and does not give:* the blob is useless to other
  accounts and other machines, but any process running as the same user
  can ask DPAPI to decrypt it. That is the same position as the Mac
  build, where the app reads its Keychain item silently.
- **Key and vault must match.** A new key is only generated when no key
  file exists. If the vault already holds entries under a key that is
  gone, the freshly written key file is removed again and the user is
  told to restore `vault.key` or start a new vault, instead of leaving a
  key on disk that opens nothing. A key file that DPAPI cannot decrypt
  (other account, other PC) or that does not open the vault is reported
  as such; nothing is deleted.
- **Owner check.** Before the vault unlocks (once per launch), Windows is
  asked to confirm the person at the keyboard: Windows Hello through
  `UserConsentVerifier` when it is set up, otherwise the standard Windows
  Security dialog asking for the account's password
  (`CredUIPromptForWindowsCredentials`, current user pre-filled), which
  Windows then checks with `LogonUser`. **The password fallback goes
  beyond the recorded decision ("owner check: Windows Hello") and is
  flagged to the owner**: the dev machine is domain-joined with no Hello
  enrolled, where Hello alone would make the vault unusable, and the Mac
  build's check also falls back to the login password. Details:
  - Only the password is taken from the dialog. It is checked against
    the account Toolbox runs as, whatever name the dialog showed, so
    another account's password does not pass.
  - The password stays in unmanaged memory and is wiped before the
    buffers are freed; it is never stored, logged or turned into a
    managed string.
  - A wrong password counts towards the account's lockout policy like
    any other failed logon. Three attempts, then the unlock fails.
  - An interactive logon type is tried first (it also works from cached
    credentials with no domain controller in reach), then the network
    type where policy refuses the first.
  - Not exercised with a real password or with Windows Hello: the dialog
    appearing, its text, and cancelling were checked; the owner needs to
    complete one unlock each way.
- **The check is a gate, not encryption.** As on the Mac, it stops
  someone at an unlocked desk, not code already running as the user
  (which could call DPAPI itself). Turning the check off (Vault ▸ Confirm
  It's You Before Unlocking) now itself requires passing the check, which
  the Mac build does not do. The switch lives in `settings.json`, which
  the user's own processes can edit; there is deliberately no
  command-line flag that skips the check (the end-to-end fixture uses its
  own settings file instead). A stronger design would bind the key to a
  Windows Hello credential (`KeyCredentialManager`); not done.
- **Terminal.** The terminal is xterm.js inside WebView2. It only ever
  loads the page shipped next to the executable, served under a virtual
  host name in the reserved `.example` domain; any other navigation, new
  window or permission request is refused, dev tools, context menus,
  autofill, password saving and browser shortcuts are off, and the page's
  Content-Security-Policy allows no network and no inline script. Remote
  output is untrusted input to xterm.js's parser, as it was to SwiftTerm.
  OSC 52 clipboard access is not wired at all (the Mac build let remote
  programs write the clipboard); copy and paste are done by the app on
  explicit keys. Links open only with Ctrl+click and only `http`,
  `https` and `mailto`. The WebView2 profile lives in
  `%LOCALAPPDATA%\Toolbox\WebView2`.
- **xterm.js supply chain.** xterm.js and three addons are fetched from
  the npm registry by `tools\build.ps1 assets`, pinned by version and
  SHA-512; a tarball that does not match is refused. They are not in git.
  NuGet restores come from nuget.org only (`app\nuget.config`).
- **RDP.** Sessions run in Microsoft's client control (mstscax), with
  CredSSP on and authentication level 2: when the server's certificate
  cannot be verified the standard Windows warning is shown and the user
  decides, per connection. This closes the Mac build's "accept any RDP
  certificate" finding. The password is handed to the control for the
  connection and not kept by the pane. Clipboard redirection is on;
  drives and printers are not redirected.
- **Prompts.** Trusting an unknown SSH host key and accepting an
  untrusted FTPS certificate both default to Cancel; the fingerprint text
  can be selected and copied for comparison.
- **Log file.** `%LOCALAPPDATA%\Toolbox\logs\toolbox.log` records what
  happened (connections by name and host, prompts by title, provider
  sync counts, errors). No passwords, keys or key material are written;
  prompt titles can contain a username and host.
- **Launch arguments.** `--connect` and `--files` open connections;
  the remaining ones (`--edit-…`, `--demo-…`, `--unlock-vault`,
  `--expand-all`) are testing aids that only drive paths a person can
  reach with the mouse. None bypasses the owner check.
- **End-to-end harness.** `tools\e2e\ui.ps1` drives the app through UI
  Automation and synthetic input; it types only when Toolbox is in the
  foreground. The fixture's keys, vault and vault key live under
  `build\e2e` (gitignored) and are throwaway.
- `cargo audit` has not been run on this machine (not installed).

## Addendum — 2026-11-19: packaged builds and the Releases page

- **Not code-signed.** `tools\build.ps1 package` produces a zip per
  architecture (ARM64, x64) with no signature. Windows marks the download
  and SmartScreen warns before the first run; the release notes tell
  people to unblock the zip and choose "Run anyway". That trains exactly
  the habit a signature exists to prevent, and nothing lets a recipient
  tell a genuine build from a modified one except the `SHA256SUMS` file
  published next to it (which only helps if they check it, and only as
  far as they trust the Releases page). A code-signing certificate is the
  fix and is not in place.
- **The .NET runtime is inside the package** (self-contained), so nothing
  has to be installed, and so Windows Update does not service it:
  security fixes to .NET reach users only through a new Toolbox release.
  The WebView2 runtime is the system's and keeps updating itself.
- **Publishing.** `tools\publish.ps1` refuses packages whose checksums or
  build number do not match the current commit and requires that commit
  to be on `origin/main`, so a release always corresponds to code that is
  in the repository. It creates the release through the GitHub API with
  the token Git Credential Manager already holds for the repository,
  obtained with `git credential fill`; the token is used for those calls
  only and is not printed, logged or written to disk.
- **What is in the package.** The app, the core DLL, the .NET runtime,
  the WebView2 loader and the terminal page with xterm.js. No
  connections, credentials, keys or settings: each person's data is
  created on their own PC. The end-to-end fixture and its throwaway keys
  live under `build\e2e` and are not packaged.

## Addendum — 2026-11-19, later: identifiers in history and in released binaries

Scope: what the two repositories and the published packages reveal about
the environment they come from. No code path of the app changed.

- **A real internal hostname was public.** The test data of the
  1Password parser in the Mac repository named an internal system by its
  full DNS name, in every commit but the first and under the `v0.2.0`
  tag. It is not a credential, but it tells an outsider one host name and
  the internal DNS suffix. Both were replaced with `corp.example` names
  by rewriting the history, and `main` and the tag were force-pushed; a
  fresh clone was searched commit by commit afterwards and has no
  occurrence left. Residual: the name was public for some hours and must
  be treated as known; GitHub keeps commits that nothing points at for
  some time, reachable by their old ids, until its own cleanup or a
  request to GitHub Support; and a clone made earlier still holds the old
  history and would publish it again if it were force-pushed.
- **The same and more in this repository's oldest commits.** The
  imported Mac tree and the notes of that time carried that hostname, the
  names of the Active Directory domains used for testing, a domain
  account name, a private address, the profile folder of the build
  account and the owner's first name; the first three commits carried a
  personal email address in their metadata. This repository is private,
  so the audience was the people given access. The history was rewritten
  with the usual placeholders and the no-reply address and force-pushed
  with the `v0.1.0` tag; the release kept its files. The same residual
  applies, limited to those people.
- **Build paths in the released DLL.** `toolbox_core.dll` in the 0.1.0
  packages names the build account's profile folder (account name plus
  the domain's short name) a few hundred times: Rust stores source paths
  for panic messages, and the crypto library's C code stores `__FILE__`
  for its error queue. No hostname, key or other secret is among them
  (the machine name was searched for and is absent), and `Toolbox.exe`
  and `Toolbox.dll` are free of them. It hands every recipient a valid
  account name, which is of some use for phishing or password guessing
  and of none to coworkers who know it already. From now on
  `tools\build.ps1 core` rewrites those prefixes (`--remap-path-prefix`
  for Rust; compiler flags scoped to the crypto crate for C), and
  `package` refuses to produce a zip while `Toolbox.exe`, `Toolbox.dll`
  or `toolbox_core.dll` still contains the profile folder in either of
  its spellings, as text or UTF-16. Residual: the 0.1.0 downloads stay as
  they are until the next release replaces them (owner's decision); the
  check covers the three files built here, not the .NET runtime or the
  WebView2 loader, which are built elsewhere; and it looks for the
  profile folder only, so a new kind of embedded detail would need a new
  check.

## Addendum — 2026-11-20: Toolbox 0.2.1 (clipboard with remote desktops; a second read of the Windows app)

Reviewed: everything that changed since 0.1.0, and the whole Windows app and
the Windows-only parts of the core read again. The adversary in mind is a
hostile or compromised host at the other end of a session. Method: a
read-through, experiments against Microsoft's RDP control with a local
test server, 38 Rust tests and 44 app tests, `cargo clippy`, `cargo audit`
(its first run on this machine) and `dotnet list package --vulnerable`.

Result: nothing was found that lets a host reach beyond what a feature
hands it. One existing behaviour turned out wider than the earlier
addendum said (the clipboard, below) and was narrowed; five smaller
things were changed because of the review (marked "review").

**The clipboard, as it was.** 0.1.0 switched clipboard redirection on and
left the rest to the control, as mstsc does. Measured against the test
server: in that mode the control tells a connected session what the
clipboard holds at connect and again at every change, whichever window
has the keyboard, and hands the content over as soon as the host asks.
Every remote desktop open in Toolbox could therefore read everything
copied anywhere on the PC for as long as it stayed connected. The earlier
addendum's "Clipboard redirection is on" did not say so.

**The clipboard, now.** Sessions connect with the control's manual
clipboard mode (`ManualClipboardSyncEnabled`, then `IMsRdpClipboard`).
The control then tells the host nothing until the pane says so:

- A session is told what this PC's clipboard holds only while its pane
  has the keyboard: Toolbox's window is the active one and the keyboard
  focus is inside that session's control. Sessions in other panes, other
  workspaces or behind another program are told nothing. Checked in the
  app with two sessions open at once: the one in use was told and read
  the text, the other received only the empty list sent at connect.
- When the clipboard changes after an offer, the control itself takes
  the offer back (measured: it sends an empty list), so what was copied
  later in another program was never offered and cannot be asked for.
- What was copied on the remote side reaches this PC's clipboard only
  while the pane has the keyboard, or in the moment it gives it up.
- **What remains readable:** anything on the clipboard when you click
  into a remote desktop, and anything you copy while working in one,
  passwords copied from a password manager included. The host does not
  have to wait for a paste; it can ask the moment it is told. That is
  how every RDP client's clipboard works; Toolbox narrows it to the
  session in front.
- **What a host can do to this PC:** put text or files on the clipboard
  while its pane is in use. The risk is paste-jacking: a compromised
  host swaps the clipboard just before you paste somewhere else. Unlike
  the Mac build, files offered by a host are accepted; they are written
  by Microsoft's control, and only when you paste them.
- Toolbox itself never sees clipboard contents, only whether either side
  has something new. The log records that an offer or a fetch happened
  and for which connection.
- The control's clipboard object is called through its function table
  (it answers no interface query for itself). The positions are those of
  the type library in `mstscax.dll`, and the object is only fetched after
  the control has answered for the interface that defines them.
- **Fail-open, on purpose:** where the control has no manual mode (an
  older Windows), the session falls back to the automatic mode described
  above rather than losing the clipboard, and the log says so. On
  Windows 11 the manual mode is there.
- View ▸ Remote Desktop ▸ Share Clipboard off: new sessions get no
  clipboard channel at all (checked: the test server sees none). For a
  session that is already open, nothing new is offered or fetched, but
  what it was last told stays readable until the clipboard changes.
- `--assume-keyboard` drops the "has the keyboard" condition. It exists
  as a launch argument only, for checks that cannot bring the app to the
  front.

**Fast Graphics.** Off sets the control's `EnableHardwareMode` to false:
no Direct3D device, decoding and drawing in software. Either way the
host's graphics data is parsed by Microsoft's control; nothing of
Toolbox's own decodes it.

**Changed because of the review.**

- *Review:* `cargo audit` reported RUSTSEC-2023-0071 (the "Marvin"
  timing side channel in the `rsa` crate, reached through the SSH
  library when an RSA key signs). The dependencies were updated
  (`russh` 0.65.2, `rsa` 0.10.0, patch releases elsewhere); the audit
  now reports no vulnerability. It still lists two unmaintained crates:
  `basic-toml` (build tooling of the bindings) and `chrono` (dates in
  the SFTP and FTP libraries).
- *Review:* a file's name comes from the server. The download code
  already kept to a plain name inside the chosen folder, but it changed
  a name with separators into its last part without saying so, and it
  let through the names Windows reserves for devices: a download called
  `COM1` would have been written to the serial port, `NUL` thrown away.
  Such names, names with a colon, and names ending in a dot or a space
  (which Windows trims, so they can land on another file) are now
  refused with a message.
- *Review:* a remote program sets the terminal's title, and the terminal
  accepts titles of megabytes. The pane header showed them as they came,
  which a hostile host could use to stall the window. Titles are now
  kept to one line of at most 200 characters.
- *Review:* log entries could be forged. Error text from a server and
  names from a directory went into the log as they were, so a line break
  in them started what looked like a new entry. Control characters are
  now written in escaped form. The end-to-end checks assert on this log.
- *Review:* the RDP pane now switches off every device redirection by
  name (ports, smart cards, plug-and-play and point-of-service devices,
  besides drives and printers). These were the control's defaults; they
  no longer depend on staying so.

**Noted and not changed.**

- **Network Level Authentication is not enforced.** The control asks
  for it, but a server, or someone in the middle, that offers only TLS
  is accepted, and the password then goes to whoever presented the
  certificate, once the certificate warning has been answered with Yes.
  With NLA the same click reveals nothing to someone who does not know
  the password. The Mac build always requires NLA. No setting of the
  control was found that requires it and could be checked here (direct
  CredSSP without negotiation may be one; it needs a real Windows host
  to try). Until then: a certificate warning for a host that normally
  shows none is a reason to say No. The warning's "Don't ask me again"
  box stores the certificate's hash under
  `HKCU\Software\Microsoft\Terminal Server Client\Servers`.
- The certificate warning belongs to the control and blocks the whole
  window, every session in it included, until it is answered.
- The control's zoom (`ZoomLevel`) is not used: setting it while no
  session is up ends the process with an access violation inside the
  control, which would take every open session with it.
- `op` is started without a console window now. Nothing else about how
  it is found or run changed; the earlier note on `PATH` stands.
- The package still carries its own .NET runtime (10.0.15 in 0.2.1); the
  earlier note on servicing stands.

**Releases.** The check that keeps build paths out of a package stopped
the first package of this version: `Toolbox.dll` named the profile folder of the
build account. `dotnet publish` had reused the assembly the ordinary
build compiled a moment earlier at the same commit, and that one records
the full path of its debug file. Every C# build now maps the checkout's
folder to a neutral name, and the package step compiles afresh. 0.1.0's
`Toolbox.dll` was clean (searched again); its `toolbox_core.dll` is as
the previous addendum says.

The repository's releases are immutable (a GitHub setting): a published
release's files and tag cannot be changed, which is what a recipient
checking a download wants. It also means files must be attached before
publishing. The publish script did it the other way round, so 0.2.0 was
published with notes and no files; it stays that way, pointing at 0.2.1,
which is the same code. The script now attaches the files to a draft and
publishes last. Nothing was exposed by this; the cost was a version
number.

**Not verified.** No real Windows host was available: the clipboard rule
is tested against the IronRDP test server, text only (file copy rests on
the control), and the owner's clipboard was not overwritten for a test,
so "a change while another pane is in use is not offered" is shown with
the control alone and, in the app, with two sessions at once. Windows 10
has not run this build.