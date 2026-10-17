# App Manifest (#140)

`apps/dsa/manifest.yaml` defines the existing DSA app. The MAUI host bundles the selected manifest as
`app-manifest.yaml`, parses it at startup and registers the immutable
`Brilliant.Core.Apps.AppManifest` in DI. Invalid YAML or fields fail startup with an
`AppManifestException` diagnostic rather than silently selecting defaults.

| Field | Meaning |
|---|---|
| `appId` | Stable app identity. DSA retains `com.joeizang.brilliantapp`. |
| `displayName` | Native window and web document title. |
| `icon` | Optional relative icon asset path; `null` preserves DSA's current platform-default icon. |
| `accent` | Six-digit hex `dark`, `light`, `onDark` and `onLight` colors. The latter two are text drawn on the accent. |
| `contentRoot` | Optional authoring folder relative to the repository, defaulting to `content/<app>/`. DSA explicitly selects `content/dsa/`. |
| `contentPack` | Relative bundled pack asset name, currently `track-1.pack.zip`. |
| `runtimes` | Explicit runtime capability keys, `python` and `csharp` (declaring C# does not supply its implementation). An empty list is allowed. |
| `stepTypes` | Nonempty list of enabled step-type keys. |
| `graderModel` | Optional Ollama model tag (for example `gemma3:12b`) that gives advisory feedback on written answers. Omitted by the DSA app. The server uses it too, when a phone asks it to grade. See [Answer feedback](csharp-answer-feedback.md). |

Capability keys use lowercase words separated by hyphens and are compared exactly.
Unknown keys are rejected with a list of valid keys. Step keys come from
`Brilliant.Core.Content.StepTypes.All` (tested against the model's JSON
discriminators); the only runtime currently supported is `python`. The [runtime registry](code-runtimes.md) makes only the selected runtimes available.
Duplicate keys/capabilities, unknown YAML fields, missing required fields, invalid
colors and absolute/traversing asset paths are rejected. `icon` may be omitted.
The manifest can declare capabilities; this parser does not install runtimes or
validate packs against the allow-list.

App IDs use lowercase dot-separated segments, each beginning with a letter, with
at least two segments. This is a common subset of Android and Apple identifiers.
The build derives both the pack output filename and logical asset name from the
selected manifest. See [App flavors](app-flavors.md).

The host uses the manifest's display name, colors and pack. `AppAppearance` applies
both theme variants with a media query, so changing system appearance still works
live. The DSA manifest contains exactly the existing colors. Shared theme defaults
remain available to standalone UI consumers and tests.
The generated HTML includes the selected title, accents and background dots before
Blazor renders. Shared stylesheet defaults remain available to standalone consumers.

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

Build-time selection, native identity and local storage isolation are described in
[App flavors](app-flavors.md). [Per-app content](app-content.md) describes source
selection and validator enforcement; runtime selection is described in [Code runtimes](code-runtimes.md). The grading model is described in
[Answer feedback](csharp-answer-feedback.md).
