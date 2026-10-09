// Spike (issue #2): Blazor <-> Worker bridge.
let worker = null;
let nextId = 1;
const pending = new Map();

function start() {
    worker = new Worker('pyodide-worker.js', { type: 'module' });
    worker.onmessage = e => { pending.get(e.data.id)?.(e.data); pending.delete(e.data.id); };
    worker.onerror = e => { for (const [, r] of pending) r({ ok: false, error: 'worker error: ' + e.message }); pending.clear(); };
}

export function run(code) {
    if (!worker) {
        try { start(); } catch (err) { return Promise.resolve({ ok: false, error: 'cannot start worker: ' + err }); }
    }
    return new Promise(resolve => {
        const id = nextId++;
        pending.set(id, resolve);
        worker.postMessage({ id, code });
    });
}

export function info() {
    return { origin: location.origin, online: navigator.onLine, userAgent: navigator.userAgent };
}
