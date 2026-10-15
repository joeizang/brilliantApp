# App Manifest (#140)

`apps/dsa/manifest.yaml` defines the existing DSA app. The MAUI host bundles it as
`app-manifest.yaml`, parses it at startup and registers the immutable
`Brilliant.Core.Apps.AppManifest` in DI. Invalid YAML or fields fail startup with an
`AppManifestException` diagnostic rather than silently selecting defaults.

| Field | Meaning |
|---|---|
| `appId` | Stable app identity. DSA retains `com.joeizang.brilliantapp`. |
| `displayName` | Native window and web document title. |
| `icon` | Optional relative icon asset path; `null` preserves DSA's current platform-default icon. |
| `accent` | Six-digit hex `dark`, `light`, `onDark` and `onLight` colors. The latter two are text drawn on the accent. |
| `contentPack` | Relative bundled pack asset name, currently `track-1.pack.zip`. |
| `runtimes` | Explicit runtime capability keys, currently `[python]`. An empty list is allowed. |
| `stepTypes` | Nonempty list of enabled step-type keys. |

Capability keys use lowercase words separated by hyphens and are compared exactly.
Unknown keys are rejected with a list of valid keys. Step keys come from
`Brilliant.Core.Content.StepTypes.All` (tested against the model's JSON
discriminators); the only runtime currently supported is `python`. #143 will
connect runtime capabilities to the runtime registry as new runtimes are added.
Duplicate keys/capabilities, unknown YAML fields, missing required fields, invalid
colors and absolute/traversing asset paths are rejected. `icon` may be omitted.
The manifest can declare capabilities; this parser does not install runtimes or
validate packs against the allow-list.

App IDs use lowercase dot-separated segments, each beginning with a letter, with
at least two segments. This is a common subset of Android and Apple identifiers.
The bundled pack name remains in the host project for now; a regression test
compares both its output filename and logical asset name against the DSA manifest.
#141 will derive these build inputs from the selected manifest.

The host uses the manifest's display name, colors and pack. `AppAppearance` applies
both theme variants with a media query, so changing system appearance still works
live. The DSA manifest contains exactly the existing colors. Shared theme defaults
remain available to standalone UI consumers and tests.
The HTML title, pre-render accent defaults and light-theme background dots still
use DSA values. #141 must make these follow the selected flavor before Blazor
renders, so a second app never briefly displays DSA branding.

`MainPage.xaml` registers the app root before `HeadOutlet`. Release startup smoke
testing reproduced a permanent "Loading…" page with the head root first;
registering it after the app root lets the native WebView bootstrap correctly.
Core/bUnit tests do not exercise this native startup ordering, so launch the
packaged Release builds as well as running the tests when changing these roots.

Build and run as before:

```sh
dotnet build src/Brilliant.App -f net10.0-maccatalyst
dotnet build src/Brilliant.App -f net10.0-android
```

This issue adds the manifest seam. Build-time selection, native package metadata
(application ID/title/icon), and storage namespaces follow in #141; per-app content
folders and validator enforcement follow in #142; runtime registration follows in
#143. The existing application ID, content sources, databases, credentials, Python
registration and sync behavior are unchanged here. Grading-model configuration
will be added with the grading provider.
