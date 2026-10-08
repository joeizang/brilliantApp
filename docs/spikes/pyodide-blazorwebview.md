# Spike: Pyodide offline inside BlazorWebView (issue #2)

**Status: not started.** This spike needs a physical Android phone and a Mac with the MAUI workloads, so it has to be run by Joseph. This file is the checklist; fill in the findings and the go/no-go.

## Questions

1. Does a bundled (no network) Pyodide load in a Web Worker inside BlazorWebView on Mac Catalyst?
2. Same on a physical Android phone?
3. Can a Python snippet run in the worker and return stdout to C#?
4. What is the cold-start load time, and the app/APK size increase?

## Suggested approach

- Add Pyodide (`pyodide.js`, `pyodide.asm.js`, `pyodide.asm.wasm`, `python_stdlib.zip`, `pyodide-lock.json`) under the host's `wwwroot/pyodide/`.
- Start a worker from a bundled `pyodide-worker.js` (`importScripts('/pyodide/pyodide.js')`, `loadPyodide({indexURL:'/pyodide/'})`); post `{code}` and reply with captured stdout.
- Call it from Blazor via JS interop (`IJSRuntime`) and round-trip the output to C#.
- Disable networking (Wi-Fi off / airplane mode; on Mac, remove the network client entitlement) while testing.
- Watch for: WASM MIME type (`application/wasm`) from the WebView's asset handler, worker support for `file://`/custom schemes on Android WebView, and memory use.

## Results (to fill in)

| Check | Mac Catalyst | Android phone |
|---|---|---|
| Loads offline | | |
| Python → stdout reaches C# | | |
| Cold-start load time | | |
| App / APK size increase | | |

## Recommendation

_go / no-go and fallback (e.g. run Python via a different mechanism) — TBD._
