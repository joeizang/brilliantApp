// Runs bundled Pyodide inside a (module) Web Worker; no network access is used. See docs/spikes/pyodide-blazorwebview.md.
import { loadPyodide } from './pyodide/pyodide.mjs';

// Android's BlazorWebView serves .wasm without 'application/wasm', which the streaming APIs reject.
// Compile from raw bytes instead (works regardless of MIME type).
WebAssembly.compileStreaming = async (resp) => WebAssembly.compile(await (await resp).arrayBuffer());
WebAssembly.instantiateStreaming = async (resp, imports) => WebAssembly.instantiate(await (await resp).arrayBuffer(), imports);

let harness = null;

// Loads Pyodide once, then the test harness (python/harness.py) as an importable module.
async function ensureLoaded() {
    if (harness) return harness;
    const pyodide = await loadPyodide({ indexURL: new URL('pyodide/', self.location.href).href });
    const source = await (await fetch(new URL('python/harness.py', self.location.href))).text();
    pyodide.FS.writeFile('/harness.py', source);
    pyodide.runPython('import sys\nsys.path.insert(0, "/")');
    harness = pyodide.pyimport('harness');
    return harness;
}

self.onmessage = async (e) => {
    const { id, code, entrypoint, testsJson } = e.data;
    try {
        const h = await ensureLoaded();
        self.postMessage({ id, ok: true, result: h.run_tests(code, entrypoint, testsJson) });
    } catch (err) {
        self.postMessage({ id, ok: false, error: String(err) });
    }
};
