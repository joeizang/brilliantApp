#!/usr/bin/env bash
# Builds a signed Release APK of the Android app for sideloading.
#
#   scripts/build-apk.sh            build dist/brilliant.apk
#   scripts/build-apk.sh --install  ...and install it on the connected phone or emulator (adb)
#
# The signing key lives OUTSIDE the repo, in ~/.brilliant/android/ (override with BRILLIANT_SIGNING_DIR). It is created on
# the first run. Back that folder up: Android only accepts an update signed with the key that signed the installed app, so
# losing the key means uninstalling the app (and its on-device progress) before a newer build can go on.
set -euo pipefail

cd "$(dirname "$0")/.."
SDK="${ANDROID_HOME:-${ANDROID_SDK_ROOT:-$HOME/Library/Android/sdk}}"
DIR="${BRILLIANT_SIGNING_DIR:-$HOME/.brilliant/android}"
STORE="$DIR/brilliant.keystore"
PASSFILE="$DIR/store-password"
ALIAS="brilliant"
APPID="com.joeizang.brilliantapp"

if [[ ! -f "$STORE" ]]; then
  echo "No signing key yet: creating one in $DIR"
  mkdir -p "$DIR" && chmod 700 "$DIR"
  (umask 077 && openssl rand -hex 24 > "$PASSFILE")
  keytool -genkeypair -v -keystore "$STORE" -alias "$ALIAS" -keyalg RSA -keysize 4096 -validity 36500 \
    -storepass "$(cat "$PASSFILE")" -keypass "$(cat "$PASSFILE")" \
    -dname "CN=Brilliant (Joseph Izang), O=Personal" >/dev/null
  chmod 600 "$STORE"
fi
export BRILLIANT_KEY_PASS="$(cat "$PASSFILE")"

OUT="bin/apk"
rm -rf "$OUT"
dotnet publish src/Brilliant.App -f net10.0-android -c Release -o "$OUT" \
  -p:AndroidPackageFormat=apk \
  -p:AndroidKeyStore=true \
  -p:AndroidSigningKeyStore="$STORE" \
  -p:AndroidSigningKeyAlias="$ALIAS" \
  -p:AndroidSigningKeyPass="env:BRILLIANT_KEY_PASS" \
  -p:AndroidSigningStorePass="env:BRILLIANT_KEY_PASS" \
  --nologo -v:m

APK="$(ls "$OUT"/*-Signed.apk 2>/dev/null | head -n1 || true)"
[[ -n "$APK" ]] || { echo "No signed APK was produced in $OUT" >&2; exit 1; }
mkdir -p dist
cp "$APK" dist/brilliant.apk

APKSIGNER="$(ls "$SDK"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -n1)"
"$APKSIGNER" verify --print-certs dist/brilliant.apk | head -n 3
echo "Built dist/brilliant.apk ($(du -h dist/brilliant.apk | cut -f1))"

if [[ "${1:-}" == "--install" ]]; then
  "$SDK/platform-tools/adb" install -r dist/brilliant.apk
fi
