# Per-app content (#142)

Each app has an authoring directory, normally `content/<app>/`, containing its
own `pack.yaml` and `tracks/`. The manifest in `apps/<app>/manifest.yaml` can
set `contentRoot` to another repository-relative directory. DSA explicitly uses
`content/dsa/`. Its 77 source files, pack ID, version and all learning IDs are
unchanged by that move; moving the sources does not change packed bytes or learner progress.
The format-2 migration below changes the comparison wire shape without changing lesson IDs or progress.
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
build metadata. Build inputs include the shared schema/CLI/recorder sources and
that manifest plus only that app's YAML and Markdown; changing the manifest, adding, removing or renaming content triggers
repacking. The bundle still contains the original DSA `track-1.pack.zip` asset.

Publishing and CLI pairing are scoped by the selected manifest app ID; see
[Server app identity](server.md#app-identity) and
[per-app publishing](content-distribution.md#per-app-publishing).

## Language-neutral authoring (#144)

An explanation's optional `comparison` is an ordered list. DSA keeps its C# then
Python labels and code; any number of snippets and repeated languages are allowed:

```yaml
comparison:
  - label: Before
    language: csharp
    code: 'List<int> values = new();'
  - label: After
    language: csharp
    code: 'List<int> values = [];'
```

Every entry requires `label`, `language` and `code`. Display-only snippets need no
runtime. New packs use format 2 so older apps do not download an incompatible schema.
The updated reader accepts formats 1 and 2: cached format-1 packs containing the
old `{cSharp, python}` comparison object still load; new packs write the list. IDs, storage and progress are unchanged.

Write-code `tests` keep `input` and `expected` as source strings. They inherit the
exercise's required `language`; they are not JSON values or Python expressions in
other languages. For example, C# can use `input: 'new[] { 1, 2 }'` and
`expected: 'new[] { 2, 4 }'`. Equality belongs to the selected runtime: Python keeps
its existing equality and optional `compare: linked_list`; C# must use structural
comparison for arrays, records and tuples when its runner is implemented in #149.
The Python-only `compare` option is rejected for C#. DSA test inputs, expected
expressions and reference solutions were left unchanged.

Trace steps accept `language` (missing means Python for old content). Walkthroughs
inherit the parent step's language; their optional `language` must match it. All
write-code, trace and walkthrough languages must be enabled in the selected app
manifest's `runtimes`. This applies equally to practice and phone alternatives.
Fill-blank and Parsons also support C# text; Python colon-based indentation rules
apply only to Python Parsons solutions.

Reference and trace checks require a checker whose language matches the exercise.
The shipping CLI currently provides CPython only: C# executable content fails
validation/packing/publishing with a missing-checker error until #149 supplies the
C# checker. It is never executed by CPython or silently skipped.

Recorded sequence `type` and local `type` are runtime-owned display labels. A
sequence supplies `drawings` (`array`, `stack`, `queue`) independently of that
label; an integer local can supply `index` for sequence/heap pointers. Python now
emits drawing capabilities. Legacy Python traces without those fields keep their
list/tuple/deque and integer behavior through an isolated compatibility fallback;
new runtimes must supply semantic metadata rather than imitate Python type names.
