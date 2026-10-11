// Checks which screen class the window's width becomes, and that changes are reported until detached.
// Run:  node --test tests/js/screen.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

class FakeQuery {
    constructor(matches) { this.matches = matches; this.listeners = new Set(); }
    addEventListener(_, l) { this.listeners.add(l); }
    removeEventListener(_, l) { this.listeners.delete(l); }
    set(matches) { this.matches = matches; this.listeners.forEach(l => l()); }
}
const phone = new FakeQuery(false), desktop = new FakeQuery(true);
globalThis.window = { matchMedia: q => (q.includes('max-width') ? phone : desktop) };

const { classify, attach, detach } = await import('../../src/Brilliant.Lessons.UI/wwwroot/screen.js');

test('phone wins over desktop, desktop over tablet', () => {
    assert.equal(classify(true, false), 'phone');
    assert.equal(classify(false, false), 'tablet');
    assert.equal(classify(false, true), 'desktop');
});

test('attach returns the class now and reports each change', () => {
    const seen = [];
    const ref = { invokeMethodAsync: (name, value) => seen.push([name, value]) };
    assert.equal(attach(ref), 'desktop');
    desktop.set(false);
    phone.set(true);
    assert.deepEqual(seen, [['OnScreenChanged', 'tablet'], ['OnScreenChanged', 'phone']]);
});

test('detach stops the reports', () => {
    const seen = [];
    attach({ invokeMethodAsync: (_, v) => seen.push(v) });
    detach();
    phone.set(false);
    assert.deepEqual(seen, []);
    assert.equal(phone.listeners.size + desktop.listeners.size, 0);
});
