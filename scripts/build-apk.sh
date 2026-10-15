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
APP="dsa"
INSTALL=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --app) [[ $# -ge 2 ]] || { echo "--app requires a flavor" >&2; exit 2; }; APP="$2"; shift 2 ;;
    --install) INSTALL=true; shift ;;
    *) echo "usage: $0 [--app <flavor>] [--install]" >&2; exit 2 ;;
  esac
done
if [[ ! "$APP" =~ ^[a-z][a-z0-9]*(-[a-z0-9]+)*$ ]] || [[ ! -f "apps/$APP/manifest.yaml" ]]; then
  echo "Unknown app flavor: $APP" >&2
  exit 2
fi

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

OUT="bin/apk/$APP"
rm -rf "$OUT"
dotnet publish src/Brilliant.App -f net10.0-android -c Release -o "$OUT" \
  -p:BrilliantApp="$APP" \
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
DESTINATION="dist/brilliant-$APP.apk"
[[ "$APP" != dsa ]] || DESTINATION="dist/brilliant.apk"
cp "$APK" "$DESTINATION"

APKSIGNER="$(ls "$SDK"/build-tools/*/apksigner 2>/dev/null | sort -V | tail -n1)"
"$APKSIGNER" verify --print-certs "$DESTINATION" | head -n 3
echo "Built $DESTINATION ($(du -h "$DESTINATION" | cut -f1))"

if [[ "$INSTALL" == true ]]; then
  "$SDK/platform-tools/adb" install -r "$DESTINATION"
fi
