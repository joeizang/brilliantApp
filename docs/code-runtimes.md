# Code runtimes (#143)

`Brilliant.Core.Code.ICodeRuntime` declares a language key and the existing
`RunTestsAsync` / `TraceAsync` operations. Results and trace models stay in Core,
under `Brilliant.Core.Code`, with unchanged result shapes. A scoped
`CodeRuntimeRegistry` resolves only the runtimes registered by the app. Duplicate
keys fail registration; missing languages produce a clear error, without falling
back to another language's runtime.

Write-code views use `WriteCodeStep.Language`. Trace steps and walkthroughs accept
optional language metadata; missing metadata in existing packs means Python.
Null metadata is omitted when writing packs, preserving existing DSA pack bytes.
The trace player receives the language so loading and error text name the exercise
language. The paired C#/Python comparison labels describe their actual snippets;
comparison generalization remains #144.

`Brilliant.Runtime.Python` is a separate Razor class library. It owns
`PyodideRuntime`, the JS bridge, module worker, test harness, tracer and the bundled
Pyodide files. The bridge imports
`./_content/Brilliant.Runtime.Python/python-runtime.js`; its worker URL is resolved
relative to the module, and the worker's imports/fetches are relative to itself.
This keeps execution offline after moving the assets under the library's base path.
Worker timeouts, recovery and result handling are unchanged.

The host's `SelectAppRuntimes` target asks the CLI to parse the selected manifest
with Core's YAML parser before NuGet collects project references and before build
resolves/compiles them. It adds the Python project reference and the
`BRILLIANT_PYTHON` compilation constant only when Python is enabled. The host
registers that implementation in the BlazorWebView's scope; the shared UI and Core
have no dependency on the Python implementation. The CLI still uses CPython for
its publishing gate, embedding the same harness/tracer from the runtime project.

Build fixtures:

```sh
scripts/build-app.sh dsa mac Release
scripts/build-app.sh dsa android Release
scripts/build-app.sh runtime-check mac Release
scripts/build-app.sh runtime-check android Release
```

`runtime-check` has `runtimes: []` and its own one-explanation pack. Its restore
graph, binaries and packaged static assets must contain no Python runtime,
worker or Pyodide. It has its own app identity and data directory. It is a build
fixture, not the C# app (#148).

For Python builds, place the five pinned Pyodide 314.0.7 assets (`pyodide.mjs`,
`pyodide.asm.mjs`, `pyodide.asm.wasm`, `python_stdlib.zip`, `pyodide-lock.json`)
under `src/Brilliant.Runtime.Python/wwwroot/pyodide/`. They remain gitignored;
the original setup is recorded in [the spike](spikes/pyodide-blazorwebview.md).
They are unnecessary for a runtime-free build.

Verification must include launching packaged Release apps: run DSA write-code
against hidden tests and open a trace/walkthrough, checking that the module worker
loads its bundled assets at the library URL. Confirm existing progress survives.
Launch `runtime-check` too, and inspect both native bundles for the absence of
Python assets/assembly. Unit tests alone do not exercise native WebView startup
or module-worker asset resolution.
