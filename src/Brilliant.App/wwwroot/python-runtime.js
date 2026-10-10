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

// Terminates the worker and settles every outstanding request. An Error `outcome` rejects them all. Otherwise `outcome` is
// the timeout result of the request `ownerId`, which resolves with it; the other requests were not the ones that ran too
// long and a result shaped for another kind of request would be wrong for them, so they are rejected and can be retried.
function stop(outcome, ownerId) {
    const dead = worker;
    worker = null; // the next run starts a fresh worker
    dead?.terminate();
    const restarted = new Error('Python was restarted because another run took too long. Try again.');
    for (const [id, request] of pending) {
        request.finish();
        if (outcome instanceof Error) request.reject(outcome);
        else if (id === ownerId) request.resolve(outcome);
        else request.reject(restarted);
    }
    pending.clear();
}

function timedOutMessage(seconds) {
    return `Your code ran for more than ${seconds} seconds, so it was stopped. Check for a loop that never ends.`;
}

function testsTimedOut(seconds) {
    return JSON.stringify({ status: 'timedOut', stdout: '', traceback: null, errorLine: null, tests: [], message: timedOutMessage(seconds) });
}

function traceTimedOut(seconds) {
    return JSON.stringify({ status: 'error', frames: [], stdout: '', traceback: null, errorLine: null, message: timedOutMessage(seconds) });
}

// Sends `message` to the worker. Resolves with the worker's JSON result string, or `onTimeout(seconds)` when the run
// itself exceeds `runTimeoutMs`. Rejects if Python can't start (or doesn't within the load timeout).
function request(message, runTimeoutMs, onTimeout) {
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
                timer = setTimeout(() => stop(onTimeout(runTimeoutMs / 1000), id), runTimeoutMs);
            },
        });
        worker.postMessage({ id, ...message });
    });
}

/**
 * Resolves with the harness's JSON result string (see wwwroot/python/harness.py), or a `timedOut` result when the
 * run itself exceeds `runTimeoutMs`. Rejects if Python can't start (or doesn't within the load timeout).
 */
export function runTests(code, entrypoint, testsJson, runTimeoutMs = 5000) {
    return request({ kind: 'tests', code, entrypoint, testsJson }, runTimeoutMs, testsTimedOut);
}

/**
 * Resolves with the tracer's JSON result string (see wwwroot/python/tracer.py) for `code`, recording the lists named in
 * `watchJson` (a JSON array of variable names). A run that exceeds `runTimeoutMs` comes back as an error trace with no frames.
 */
export function trace(code, watchJson, runTimeoutMs = 5000) {
    return request({ kind: 'trace', code, watchJson }, runTimeoutMs, traceTimedOut);
}
