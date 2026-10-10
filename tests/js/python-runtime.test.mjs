// Checks the Python runtime bridge's timeout and recovery logic against a scripted fake Worker.
// Run:  node --test tests/js/python-runtime.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const workers = [];
class FakeWorker {
    constructor() { this.terminated = false; this.sent = []; this.script = FakeWorker.next; workers.push(this); }
    terminate() { this.terminated = true; }
    postMessage(msg) {
        if (this.terminated) return;
        this.sent.push(msg);
        queueMicrotask(() => {
            if (this.script === 'hang') { this.onmessage({ data: { id: msg.id, started: true } }); return; } // never answers
            this.onmessage({ data: { id: msg.id, started: true } });
            this.onmessage({ data: { id: msg.id, ok: true, result: '{"status":"passed"}' } });
        });
    }
}
globalThis.Worker = FakeWorker;
const { runTests, trace } = await import('../../src/Brilliant.App/wwwroot/python-runtime.js');

test('a run that never finishes comes back as timedOut and its worker is terminated', async () => {
    FakeWorker.next = 'hang';
    const result = JSON.parse(await runTests('while True: pass', 'f', '[]', 50));
    assert.equal(result.status, 'timedOut');
    assert.match(result.message, /more than 0.05 seconds/);
    assert.equal(workers.at(-1).terminated, true);
});

test('the next run after a timeout uses a fresh worker and works normally', async () => {
    const before = workers.length;
    FakeWorker.next = 'ok';
    const result = JSON.parse(await runTests('def f(): pass', 'f', '[]', 50));
    assert.equal(result.status, 'passed');
    assert.equal(workers.length, before + 1);
    assert.equal(workers.at(-1).terminated, false);
});

test('a healthy worker is reused between runs', async () => {
    const before = workers.length;
    await runTests('x', 'f', '[]', 50);
    assert.equal(workers.length, before);
});

test('a worker error rejects the run and the next run starts a new worker', async () => {
    FakeWorker.next = 'ok';
    const bad = workers.at(-1);
    const run = runTests('x', 'f', '[]', 5000);
    bad.onerror({ message: 'boom' });
    await assert.rejects(run, /boom/);
    const before = workers.length;
    assert.equal(JSON.parse(await runTests('x', 'f', '[]', 50)).status, 'passed');
    assert.equal(workers.length, before + 1);
});

test('a trace is sent to the worker as a trace request with the variables to watch', async () => {
    FakeWorker.next = 'ok';
    await trace('nums = [1]', '["nums"]', 50);
    assert.deepEqual(workers.at(-1).sent.at(-1), { id: workers.at(-1).sent.at(-1).id, kind: 'trace', code: 'nums = [1]', watchJson: '["nums"]' });
});

test('test runs are sent as tests requests', async () => {
    FakeWorker.next = 'ok';
    await runTests('def f(): pass', 'f', '[]', 50);
    const sent = workers.at(-1).sent.at(-1);
    assert.equal(sent.kind, 'tests');
    assert.equal(sent.entrypoint, 'f');
});

test('a trace that never finishes comes back as an error trace with no frames', async () => {
    workers.at(-1).script = 'hang';   // the healthy worker is reused, so make it the one that hangs
    const result = JSON.parse(await trace('10 ** 10 ** 10', '[]', 50));
    assert.equal(result.status, 'error');
    assert.deepEqual(result.frames, []);
    assert.match(result.message, /more than 0.05 seconds/);
    assert.equal(workers.at(-1).terminated, true);
});
