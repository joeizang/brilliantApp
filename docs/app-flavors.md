# App flavors (#141)

One MAUI project builds a separate installed app for each `apps/<app>/manifest.yaml`.
`BrilliantApp` selects the folder; it defaults to `dsa`. Adding another flavor
requires a manifest and optional icon, not another host project.

```sh
scripts/build-app.sh dsa mac
scripts/build-app.sh flavor-check mac Release
scripts/build-app.sh dsa android Release
scripts/build-app.sh flavor-check android Release

# Equivalent direct build (works without a generation step first):
dotnet build src/Brilliant.App -f net10.0-maccatalyst -p:BrilliantApp=flavor-check
```

The script accepts `<app> <mac|android> [Debug|Release]`, with Debug as the default.
Android Release delegates to the signed APK builder, retaining the existing signing
key. Its original `scripts/build-apk.sh [--install]` invocation still builds DSA;
use `--app flavor-check` for another app.

Outputs:

- Host intermediates: `src/Brilliant.App/obj/<app>/`.
- Host binaries: `src/Brilliant.App/bin/<app>/<configuration>/<framework>/`.
- Mac Debug adds its RID directory (normally `maccatalyst-arm64`); universal Release
  places the installable `.app` directly under the framework directory. Its filename
  is the manifest's display name: `Brilliant.app` or `Brilliant Flavor Check.app`.
- Signed APKs: `dist/brilliant.apk` for DSA, `dist/brilliant-<app>.apk` otherwise.

Copy both Mac `.app` bundles into a test directory or Applications and open them
individually. Install both APKs with `adb install -r <apk>`; never uninstall DSA as
part of an upgrade because uninstalling removes progress. The two app IDs install
side by side. DSA retains `com.joeizang.brilliantapp` and its default icon. The
`flavor-check` build fixture has a purple F icon and a separate ID; it intentionally
uses the DSA pack until per-app content arrives in #142. It is not the C# app (#148).

## Build inputs

`PrepareAppFlavor` runs the CLI's `prepare-app` command before native packaging,
icon collection and static-web-asset discovery. It parses the selected YAML through
the same Core parser as startup and generates XML build metadata, an Android
manifest copied from the platform template, and initial HTML
in the flavor/framework/RID's intermediate directory. Only changed generated files
are written. Invalid manifests, missing icons and display names containing path
separators fail the build with a diagnostic.

Application ID, native display name, icon and bundled pack name come from this
metadata. The pack filename and logical asset name both use the one generated
`ContentPack` value. Mac packaging refreshes the SDK's cached bundle name before
its bundle-name target runs, so Finder/menu identity also follows the manifest.
The HTML title, accents and background dots are correct before Blazor renders.
DSA keeps its exact existing dark and light dot colors.
The generated Android manifest points its launcher icon at the selected MAUI
icon's mipmap resource while preserving the existing permissions and application
flags. With no icon selected, DSA retains its existing platform default.

Content is still sourced from `content/`. #142 moves it per app; #143 makes runtime
references conditional. Both flavors in this issue enable Python.

## State compatibility and isolation

`AppStoragePaths` preserves DSA's platform data directory and the existing
`brilliant.device-credentials` secure-storage key. Its `brilliant.db`, `drafts.db`,
device ID, credential fallback file, pack cache and sync cursor keep their paths;
no migration or copying is needed. Other app IDs use an `apps/<appId>/` subdirectory
of their platform data root and an app-ID-prefixed secure-storage key. Their native
package IDs also provide the OS's normal app sandbox isolation.

Server-side app scoping and independent pairing checks belong to #145/#147.
Do not use the fixture to share an existing DSA pairing.

When changing startup/build generation, run Core/CLI/UI tests and launch the
packaged Mac and Android Release builds. Check that DSA retains progress, the
fixture starts empty, both names/icons are distinct, and each reaches Today.

Verified on 2026-10-15: both Mac Release bundles launched together from a test
install directory; DSA retained its existing completed lessons/streak and the
fixture had no progress. Both signed Release APKs installed together and reached
Today on an Android API 36 ARM64 emulator. Package metadata confirmed their
separate IDs/labels and the fixture's launcher icon. Physical Galaxy pairing/sync
validation remains #147.
