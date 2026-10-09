# Spike: Pyodide offline inside BlazorWebView (issue #2)

**Recommendation: GO.** A bundled Pyodide loads and runs Python in a Web Worker inside BlazorWebView on both Mac Catalyst and a physical Android phone, with the network off, and returns stdout to C#. Two integration gotchas are documented below and both have working workarounds.

## Results

Pyodide 314.0.7 (npm package), bundled under `wwwroot/pyodide/` (5 files, ~13 MB uncompressed). Test snippet printed a Python version string and a sum; stdout was returned to C# via JS interop.

| Check | Mac Catalyst | Android phone (Samsung, `R5CWC2V6LRW`) |
|---|---|---|
| Loads from bundled assets, network off | Yes | Yes (airplane mode) |
| Python in a Web Worker → stdout to C# | Yes | Yes |
| Cold-start Pyodide load | 867 ms | 1577 ms |
| Run of trivial snippet | 3 ms | 6 ms |
| Round-trip C# → worker → C# (incl. load) | 886 ms | 1607 ms |
| Size impact | ~13 MB added to the `.app` (uncompressed files) | **+6.4 MB** on a Release APK (30.2 MB → 36.5 MB; unoptimised, unsigned-config build) |

Load time is measured inside the worker (`loadPyodide`), on first use after process start. Subsequent runs reuse the loaded worker.

## Gotchas (must carry into the Python Runtime module)

1. **Module worker required.** Pyodide 314 throws `Classic web workers are not supported`. Start the worker with `new Worker(url, { type: 'module' })` and `import { loadPyodide } from './pyodide/pyodide.mjs'`. Bundle `pyodide.mjs`, `pyodide.asm.mjs`, `pyodide.asm.wasm`, `python_stdlib.zip`, `pyodide-lock.json`.
2. **Android serves `.wasm` with the wrong MIME type** (BlazorWebView asset handler on `https://0.0.0.1/`). `WebAssembly.compileStreaming`/`instantiateStreaming` then fail with `Incorrect response MIME type. Expected 'application/wasm'`, and the failure left the worker call hanging forever with no error surfaced. Workaround, applied at the top of the worker: replace both with versions that compile from `arrayBuffer()`. Mac Catalyst did not need it, but the workaround is harmless there.
3. The Python Runtime should therefore add a **load timeout** and surface worker errors (the PRD already requires timeouts that terminate and recreate the worker).

## Caveats

- Memory use and behaviour under a long-running or looping script were not measured.
- Only the stdlib was loaded; no extra packages (numpy etc.), which v1 does not need.
- APK size is from a plain Release build; trimming/AOT settings will change absolute numbers but the ~6 MB delta from compression should hold roughly.
- Throwaway code: `src/Brilliant.App/Components/Spike.razor`, `wwwroot/spike.js`, `wwwroot/pyodide-worker.js`. The Pyodide files are gitignored; fetch the `pyodide` npm package 314.0.7 and copy the 5 files above into `src/Brilliant.App/wwwroot/pyodide/`.

## Fallback if this regresses

Photino.Blazor is already the fallback host for Mac; for Python, a CPython subprocess on desktop only would be the fallback (not viable on Android), which is why gotcha 2 matters.
