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

