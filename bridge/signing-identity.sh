#!/usr/bin/env bash
# One-time, per-Mac setup of a local code-signing identity for Toolbox.
#
# Why: macOS remembers Keychain "Always Allow" decisions per code signature.
# Without an identity, `make app` signs ad hoc, and an ad-hoc signature
# changes with every build, so the vault-key dialog comes back after each
# rebuild. A certificate gives the app a signature that stays the same.
#
# What it does, asking for your password where macOS requires it:
#   1. creates a self-signed code-signing certificate "Toolbox Local Signing"
#      and imports it, with its private key, into your login keychain;
#   2. marks it trusted for code signing (macOS asks for your login password;
#      this is what makes codesign accept the certificate);
#   3. lets codesign use the key without a dialog on every build (asks for
#      the login keychain password in this terminal).
#
# Not needed if you have an Apple Development or Developer ID identity:
# `make app` picks those up by itself. Undo with:
#   security delete-identity -c "Toolbox Local Signing"
set -euo pipefail

NAME="Toolbox Local Signing"
LOGIN_KEYCHAIN="$HOME/Library/Keychains/login.keychain-db"
OPENSSL=/usr/bin/openssl   # LibreSSL writes a PKCS#12 that `security import` accepts; OpenSSL 3's default does not

if security find-identity -v -p codesigning 2>/dev/null | grep -Fq "$NAME"; then
  echo "'$NAME' already exists and is valid for code signing. Run 'make app'."
  exit 0
fi

WORK="$(mktemp -d)"
chmod 700 "$WORK"
trap 'rm -rf "$WORK"' EXIT

cat > "$WORK/cert.cnf" <<CNF
[req]
distinguished_name = dn
x509_extensions = ext
prompt = no
[dn]
CN = $NAME
[ext]
keyUsage = critical, digitalSignature
extendedKeyUsage = critical, codeSigning
subjectKeyIdentifier = hash
CNF

echo "Creating '$NAME'…"
"$OPENSSL" req -x509 -newkey rsa:3072 -nodes -sha256 -days 3650 \
  -config "$WORK/cert.cnf" -keyout "$WORK/key.pem" -out "$WORK/cert.pem" 2>/dev/null
P12_PASS="$("$OPENSSL" rand -hex 16)"
"$OPENSSL" pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
  -name "$NAME" -passout "pass:$P12_PASS" -out "$WORK/identity.p12"

echo "Importing it into your login keychain…"
security import "$WORK/identity.p12" -k "$LOGIN_KEYCHAIN" -P "$P12_PASS" \
  -T /usr/bin/codesign -T /usr/bin/security >/dev/null

echo "Trusting it for code signing (macOS will ask for your login password)…"
security add-trusted-cert -r trustRoot -p codeSign -k "$LOGIN_KEYCHAIN" "$WORK/cert.pem"

echo "Letting codesign use the key without a dialog (enter your login keychain password)…"
security set-key-partition-list -S apple-tool:,apple: -s -l "$NAME" "$LOGIN_KEYCHAIN" >/dev/null

if security find-identity -v -p codesigning | grep -Fq "$NAME"; then
  echo "Done. 'make app' now signs with '$NAME'."
  echo "The next launch asks once for Keychain access to the existing vault key; after that it stays quiet."
else
  echo "The identity was imported but is not reported as valid for code signing." >&2
  echo "Open Keychain Access, find '$NAME', and set Trust → Code Signing to Always Trust." >&2
  exit 1
fi
