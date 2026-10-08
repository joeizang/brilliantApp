# Building and running on Mac (local)

## Prerequisites

- macOS with Xcode installed (`xcodebuild -version`)
- .NET 10 SDK
- The MAUI Mac Catalyst workload (needs admin rights, once):

  ```bash
  sudo dotnet workload install maui-maccatalyst
  ```

## Layout

| Path | Purpose |
|---|---|
| `src/Brilliant.Core` | Content Model: records, `ContentGraph` (lookup by stable ID), Content Pack reader/writer |
| `src/Brilliant.Data` | Local store (placeholder; SQLite arrives with the event log) |
| `src/Brilliant.Lessons.UI` | Razor Class Library: step renderers (`ExplainStepView`, `LessonViewer`) |
| `src/Brilliant.App` | Thin MAUI Blazor Hybrid host (Mac Catalyst) |
| `tools/Brilliant.Cli` | Validator & packer CLI |
| `content/` | Lesson source (YAML) — `pack.yaml`, `tracks/<track>/track.yaml`, `tracks/<track>/<lesson>/lesson.yaml` |
| `tests/` | xUnit tests for Core and the CLI |

## Workflow

```bash
# run tests (no MAUI workload required for the test projects)
dotnet test tests/Brilliant.Core.Tests && dotnet test tests/Brilliant.Cli.Tests

# validate content; prints readable errors (file + message) and exits 1 on bad content
dotnet run --project tools/Brilliant.Cli -- validate content

# validate + build the versioned Content Pack into the app's bundled assets
scripts/pack-content.sh

# run the app
dotnet build src/Brilliant.App -t:Run -f net10.0-maccatalyst
```

## Content rules enforced by the validator

- Every track, lesson and step has a stable `id` (`track.…`, `lesson.…`, `step.…`; lowercase words joined by `-` or `.`), unique across the pack.
- `pack.yaml` has an `id` and a `version` like `1.2.3`.
- Only `explain` steps exist so far. They need `title` and `body` (markdown); `snippets` and a C#↔Python `comparison` are optional but must be complete if present.
- Unknown YAML fields are errors, so typos aren't silently ignored.
