// Spike (issue #2): runs bundled Pyodide inside a (module) Web Worker. No network access is used.
import { loadPyodide } from './pyodide/pyodide.mjs';

let pyodide = null;
let loadMs = null;

async function ensureLoaded() {
    if (pyodide) return;
    const t0 = performance.now();
    pyodide = await loadPyodide({ indexURL: new URL('pyodide/', self.location.href).href });
    loadMs = Math.round(performance.now() - t0);
}

self.onmessage = async (e) => {
    const { id, code } = e.data;
    try {
        await ensureLoaded();
        let out = '';
        pyodide.setStdout({ batched: s => { out += s + '\n'; } });
        const t0 = performance.now();
        await pyodide.runPythonAsync(code);
        self.postMessage({ id, ok: true, stdout: out, loadMs, runMs: Math.round(performance.now() - t0) });
    } catch (err) {
        self.postMessage({ id, ok: false, error: String(err) });
    }
};
