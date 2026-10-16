// Runs bundled Pyodide inside a (module) Web Worker; no network access is used. See docs/spikes/pyodide-blazorwebview.md.
import { loadPyodide } from './pyodide/pyodide.mjs';

// Android's BlazorWebView serves .wasm without 'application/wasm', which the streaming APIs reject.
// Compile from raw bytes instead (works regardless of MIME type).
WebAssembly.compileStreaming = async (resp) => WebAssembly.compile(await (await resp).arrayBuffer());
WebAssembly.instantiateStreaming = async (resp, imports) => WebAssembly.instantiate(await (await resp).arrayBuffer(), imports);

let modules = null;

// Loads Pyodide once, then the test harness (python/harness.py) and the tracer (python/tracer.py) as importable modules.
async function ensureLoaded() {
    if (modules) return modules;
    const pyodide = await loadPyodide({ indexURL: new URL('pyodide/', self.location.href).href });
    for (const name of ['harness', 'tracer']) {
        const source = await (await fetch(new URL(`python/${name}.py`, self.location.href))).text();
        pyodide.FS.writeFile(`/${name}.py`, source);
    }
    pyodide.runPython('import sys\nsys.path.insert(0, "/")');
    modules = { harness: pyodide.pyimport('harness'), tracer: pyodide.pyimport('tracer') };
    return modules;
}

self.onmessage = async (e) => {
    const { id, kind, code, entrypoint, testsJson, watchJson } = e.data;
    try {
        const m = await ensureLoaded();
        self.postMessage({ id, started: true }); // loading is done; the run timeout starts now
        const result = kind === 'trace' ? m.tracer.trace_code(code, watchJson) : m.harness.run_tests(code, entrypoint, testsJson);
        self.postMessage({ id, ok: true, result });
    } catch (err) {
        self.postMessage({ id, ok: false, error: String(err) });
    }
};
