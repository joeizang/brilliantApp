# Per-app content (#142)

Each app has an authoring directory, normally `content/<app>/`, containing its
own `pack.yaml` and `tracks/`. The manifest in `apps/<app>/manifest.yaml` can
set `contentRoot` to another repository-relative directory. DSA explicitly uses
`content/dsa/`. Its 77 source files, pack ID, version and all learning IDs are
unchanged; moving the sources does not change packed bytes or learner progress.
The `flavor-check` installation fixture explicitly shares DSA's sources.

From the repository root:

```sh
# The directory convention infers apps/dsa/manifest.yaml:
dotnet run --project tools/Brilliant.Cli -- validate content/dsa
dotnet run --project tools/Brilliant.Cli -- pack content/dsa -o /tmp/dsa.pack.zip
dotnet run --project tools/Brilliant.Cli -- publish content/dsa

# Or select the app manifest; its contentRoot (or convention) supplies the source:
dotnet run --project tools/Brilliant.Cli -- validate --app apps/dsa/manifest.yaml
dotnet run --project tools/Brilliant.Cli -- pack --app apps/dsa/manifest.yaml -o /tmp/dsa.pack.zip
dotnet run --project tools/Brilliant.Cli -- publish --app apps/dsa/manifest.yaml

# No -o with --app: write dist/<app>/<manifest contentPack>:
dotnet run --project tools/Brilliant.Cli -- pack --app apps/flavor-check/manifest.yaml

# Shared/custom folders: explicitly choose the intended app, not the folder's app:
dotnet run --project tools/Brilliant.Cli -- validate content/dsa --manifest apps/flavor-check/manifest.yaml

# Existing helper remains DSA by default and keeps its original bundle asset output:
scripts/pack-content.sh
scripts/pack-content.sh flavor-check
```

`--manifest` is the final option for directory-based commands. A root that does
not match the selected manifest's source directory is rejected. Missing or invalid
manifests for `content/<app>/` fail with a diagnostic; they never silently disable
the allow-list. Standalone directories outside that convention still support
schema/reference validation without an app; use an explicit manifest to enforce
app capabilities for custom source locations.

Every lesson step, practice problem and nested phone alternative must use a type
in the selected manifest's `stepTypes`. Errors name the source file, step, type
and app ID. Validation happens before reference execution, pack creation and
publishing, so disabled content cannot be bundled or uploaded through the CLI.
Existing schema, reference-solution and trace checks still run for enabled types.

The host gets its content root and pack asset name from the selected manifest's
build metadata. Build inputs include that manifest plus only that app's YAML and
Markdown; changing the manifest, adding, removing or renaming content triggers
repacking. The bundle still contains the original DSA `track-1.pack.zip` asset.

Publishing transport and CLI pairing are unchanged. Server-side app scoping and
independent pairings belong to #145; this issue adds the authoring gate, not a
second server namespace.
