// Main-thread side of the Python Runtime: owns the Pyodide worker and matches replies to requests.
let worker = null;
let nextId = 1;
const pending = new Map();

function start() {
    worker = new Worker('python-worker.js', { type: 'module' });
    worker.onmessage = e => {
        const { id, ok, result, error } = e.data;
        const request = pending.get(id);
        pending.delete(id);
        if (request) ok ? request.resolve(result) : request.reject(new Error(error));
    };
    worker.onerror = e => {
        for (const [, request] of pending) request.reject(new Error('The Python worker failed: ' + (e.message || 'unknown error')));
        pending.clear();
        worker = null; // the next run starts a fresh worker
    };
}

/** Resolves with the harness's JSON result string (see wwwroot/python/harness.py). */
export function runTests(code, entrypoint, testsJson) {
    if (!worker) start();
    return new Promise((resolve, reject) => {
        const id = nextId++;
        pending.set(id, { resolve, reject });
        worker.postMessage({ id, code, entrypoint, testsJson });
    });
}
