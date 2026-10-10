// Main-thread side of the Python Runtime: owns the Pyodide worker and matches replies to requests.
// Runaway code (e.g. `while True`) can't be interrupted inside the worker, so a run that exceeds its timeout
// is stopped by terminating the worker; the next run starts a fresh one. The UI thread is never blocked.
const LOAD_TIMEOUT_MS = 60000; // starting Pyodide (first run only); generous because it is slow on phones

let worker = null;
let nextId = 1;
const pending = new Map();

function start() {
    worker = new Worker('python-worker.js', { type: 'module' });
    worker.onmessage = e => {
        const { id, started, ok, result, error } = e.data;
        const request = pending.get(id);
        if (!request) return;
        if (started) {
            request.startRun();
            return;
        }
        request.finish();
        pending.delete(id);
        ok ? request.resolve(result) : request.reject(new Error(error));
    };
    worker.onerror = e => stop(new Error('The Python worker failed: ' + (e.message || 'unknown error')));
}

// Terminates the worker and settles every outstanding request with `outcome` (an Error rejects, anything else resolves).
function stop(outcome) {
    const dead = worker;
    worker = null; // the next run starts a fresh worker
    dead?.terminate();
    for (const [, request] of pending) {
        request.finish();
        outcome instanceof Error ? request.reject(outcome) : request.resolve(outcome);
    }
    pending.clear();
}

function timedOut(seconds) {
    return JSON.stringify({
        status: 'timedOut', stdout: '', traceback: null, errorLine: null, tests: [],
        message: `Your code ran for more than ${seconds} seconds, so it was stopped. Check for a loop that never ends.`,
    });
}

/**
 * Resolves with the harness's JSON result string (see wwwroot/python/harness.py), or a `timedOut` result when the
 * run itself exceeds `runTimeoutMs`. Rejects if Python can't start (or doesn't within the load timeout).
 */
export function runTests(code, entrypoint, testsJson, runTimeoutMs = 5000) {
    if (!worker) start();
    return new Promise((resolve, reject) => {
        const id = nextId++;
        let timer = setTimeout(
            () => stop(new Error('Python took too long to start. Close and reopen the app, then try again.')), LOAD_TIMEOUT_MS);
        pending.set(id, {
            resolve, reject,
            finish: () => clearTimeout(timer),
            startRun: () => {
                clearTimeout(timer);
                timer = setTimeout(() => stop(timedOut(runTimeoutMs / 1000)), runTimeoutMs);
            },
        });
        worker.postMessage({ id, code, entrypoint, testsJson });
    });
}
