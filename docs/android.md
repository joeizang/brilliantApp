# Android

Issue [#33](https://github.com/joeizang/brilliantApp/issues/33), PRD stories 50 and 72. The same MAUI Blazor Hybrid app runs on an Android phone: lessons, code execution (Pyodide) and review work offline, and the phone pairs and syncs with the household server ([server.md](server.md), [sync.md](sync.md)) on the home network.

The app is not on any store. You build a **signed APK** on your Mac and sideload it.

## Build and sign

Prerequisites: the .NET 10 SDK with the `maui-android` workload, a JDK (for `keytool`), and the Android SDK (default `~/Library/Android/sdk`; set `ANDROID_HOME` otherwise). `apksigner` comes from the SDK's build-tools. The Pyodide files must be present under `src/Brilliant.App/wwwroot/pyodide/` (see [the spike](spikes/pyodide-blazorwebview.md); they are gitignored).

```bash
scripts/build-apk.sh
```

This produces `dist/brilliant.apk` (about 45 MB, with `arm64-v8a` and `x86_64` native libraries) and checks its signature with `apksigner verify`. `dist/` is gitignored.

### The signing key

The first run creates a key in `~/.brilliant/android/` (override with `BRILLIANT_SIGNING_DIR`): `brilliant.keystore` (RSA 4096, alias `brilliant`, valid 100 years) and `store-password`, both mode 600 in a directory of mode 700. It never lives in the repo.

**Back that folder up.** Android only installs an update signed with the same key as the installed app. If the key is lost, the next build can only go on after uninstalling the app, which also deletes its on-device progress (progress already synced to the server comes back after pairing again).

## Install

**With a cable** (USB debugging on, `adb` from the SDK's platform-tools):

```bash
scripts/build-apk.sh --install    # build, then adb install -r
# or, for an APK you already have:
adb install -r dist/brilliant.apk
```

`-r` keeps the app's data, so progress survives an update.

**Without a cable:** copy `dist/brilliant.apk` to the phone (AirDrop is not available for Android; use a USB file transfer, a shared folder or a cloud drive), open it in the phone's Files app and allow **Install unknown apps** for that app when asked. Play Protect may warn that the app is from an unknown developer; that is expected for a personal build.

The app is called **Brilliant**, id `com.joeizang.brilliantapp`, and needs Android 7.0 (API 24) or newer.

## Pair and sync from the phone

1. Start the server on the Mac or the home machine ([server.md](server.md)) and mint a code:
   ```bash
   docker compose exec -T server dotnet Brilliant.Server.dll pairing-code
   ```
2. The phone must be on the **same Wi-Fi** as the server. Find the server's LAN address (for example `192.168.1.20`).
3. In the app: **Tracks → Sync**. Enter the address (`192.168.1.20:8080`; a bare host gets `http://` and port 8080), the code, and a name for the phone (it suggests the phone's model), then **Pair**.
4. The first sync runs at once, then every 2 minutes while the app is open, and **Sync now** forces one. Everything done offline is pushed at the next sync.

The server speaks plain HTTP, so Android is told to allow cleartext traffic (`usesCleartextTraffic` in `AndroidManifest.xml`). Android cannot limit that to private addresses; HTTPS with a local CA is [#35](https://github.com/joeizang/brilliantApp/issues/35). Do not expose the server's port to the internet.

The device token is kept in the Android Keystore through MAUI's `SecureStorage`, and falls back to a private file in the app's data directory if the keystore refuses it (see [server.md](server.md)).

## Testing on an emulator

An emulator is a good stand-in for the build, the install and the offline behaviour; the real phone is the final check.

- On Apple Silicon use an **arm64** image. If the device boots to a black screen or a locked keyguard that never clears, cold-boot it: `emulator -avd <name> -no-snapshot -gpu swiftshader_indirect`. (`Pixel_9_Pro_XL` worked; `Medium_Phone_API_36` got stuck.)
- From the emulator the host machine is **`10.0.2.2`**, so pair with `10.0.2.2:8080`.
- A Release build is not WebView-debuggable. Drive it with `adb shell input tap`, read it with `adb shell uiautomator dump`, and look at `adb exec-out screencap -p`.
- To check offline use, turn the network off (`adb shell svc wifi disable; adb shell svc data disable`) and run a lesson's code step.

## Android-specific behaviour

- **Edge-to-edge.** Apps targeting API 35+ draw under the status and navigation bars. `MainPage.xaml` sets `SafeAreaEdges` on Android so content starts below the status bar; Mac Catalyst is unchanged.
- **Pyodide.** It runs in a module worker, and Android's WebView serves `.wasm` with the wrong MIME type, which the worker works around. Load failures and timeouts are shown to the learner rather than hanging. Details in [the spike](spikes/pyodide-blazorwebview.md).
- **Device name.** Android reports its machine name as `localhost`, so the Sync screen suggests the phone's model (`DeviceInfo.Name`) via `DeviceLabel`.

## What was verified, and what is yours to verify

Verified on an arm64 Android 16 emulator (Pixel 9 Pro XL image) with the signed Release APK: install, launch, lessons and the on-screen keyboard, code execution with the network **off** (starter code fails its tests with the expected and actual values shown, a correct solution passes all four), review including a code step, pairing against the Docker server, events from the emulator arriving on the server and the Mac's events arriving on the emulator, and pairing surviving an app restart.

**Not done here, and the acceptance check for #33:** installing the APK on Joseph's physical phone and pairing over the real home network. Beyond that:

- The layout is the desktop layout squeezed narrow. The sidebar is hidden on narrow screens (Sync is reachable through Tracks), and nothing is tuned for a phone yet: that is [#34](https://github.com/joeizang/brilliantApp/issues/34), together with touch dragging for Parsons steps.
- Code drafts do not sync yet ([#30](https://github.com/joeizang/brilliantApp/issues/30)).
- Only `arm64-v8a` and `x86_64` are built, which covers every current phone and the Apple Silicon and Intel emulators.

## Where the code is

| Path | Purpose |
|---|---|
| `scripts/build-apk.sh` | build, sign, verify and optionally install the APK |
| `src/Brilliant.App/Platforms/Android/` | `AndroidManifest.xml`, `MainActivity.cs` |
| `src/Brilliant.App/Brilliant.App.csproj` | Android target, application id and version |
| `src/Brilliant.App/MainPage.xaml` | the Android safe-area setting |
| `src/Brilliant.Lessons.UI/DeviceLabel.cs` | the suggested device name |
