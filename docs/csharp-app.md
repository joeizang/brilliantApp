# C# app starter (#148)

`apps/csharp/manifest.yaml` builds **Brilliant C#** with its own purple C# icon,
accent and app ID `com.joeizang.brilliantapp.csharp`. DSA keeps its installed identity,
paths, credential key, pack and progress. There is no in-app subject switcher.

The C# pack lives in `content/csharp/`. Its starter lesson covers target framework
versus language version, inferred types, and following assignments to predict output.
Explanation, choice and predict-output steps work through the existing lesson and
review views, and the first track after it, "Types and the compiler", is described in [csharp-track-1.md](csharp-track-1.md). The manifest also enables write-code and declares `csharp`;
it enables no Python runtime. The starter now includes AddTwo, whose reference
solution is compiled and tested by the CLI gate (#149). Native execution still
requires #150, so that exercise cannot yet be completed in the app. Trace steps
are recorded when the pack is built and played on every device; see
[csharp-recorded-traces.md](csharp-recorded-traces.md) (#153).

```sh
dotnet run --project tools/Brilliant.Cli -- validate --app apps/csharp/manifest.yaml
dotnet run --project tools/Brilliant.Cli -- pack --app apps/csharp/manifest.yaml
scripts/build-app.sh csharp mac Release
scripts/build-app.sh csharp android Release
```

The signed Android artifact is `dist/brilliant-csharp.apk`. The Mac bundle is
`src/Brilliant.App/bin/csharp/Release/net10.0-maccatalyst/Brilliant C#.app`.
Install it alongside DSA; both application IDs are independent. Android installation
uses `adb install -r dist/brilliant-csharp.apk` (or the APK helper's `--install`).

## Pack version pins

```yaml
id: pack.csharp.starter
version: 0.6.0
targetFramework: net10.0
langVersion: '14'
```

The validator requires both pins for an app that enables C#. Supplied values must
be exactly `net10.0` and `14`; framework-qualified native host TFMs such as
`net10.0-android` are not curriculum target frameworks. The pins are carried in
`manifest.json`. Old DSA packs remain readable, and absent pins stay omitted from
DSA JSON. The CLI uses these same pins in its C# compilation gate; see [C# reference checks](csharp-reference-checks.md).

## Separate pairing and synchronization

Rebuild the household server so its shipped manifest catalog includes C# (#145).
Mint a code for the new identity:

```sh
docker compose exec server dotnet Brilliant.Server.dll pairing-code --app com.joeizang.brilliantapp.csharp
```

Pair from the C# app's Sync screen. Its token, local event log, drafts, pack cache,
review queue and streak are separate from DSA. Pair another C# installation with
another C# code to share progress; a DSA token or code cannot pair the C# app.
CLI publishing also pairs independently:

```sh
dotnet run --project tools/Brilliant.Cli -- pair https://server:8443 CODE --name Mac --app apps/csharp/manifest.yaml
dotnet run --project tools/Brilliant.Cli -- publish --app apps/csharp/manifest.yaml
```

## Verification

Check both native bundle IDs and install alongside DSA. Open the starter lesson,
answer its choices and output prediction, then sync its completion to another C#
installation. DSA's completion, queue and streak must remain unchanged. Inspect
native packages and restore graphs: no `Brilliant.Runtime.Python` dependency,
Pyodide assets or Python worker should be present in the C# app. A launch alone
does not verify lesson interactions or synchronization; real-client integration
coverage is separate from native/device smoke checks.
