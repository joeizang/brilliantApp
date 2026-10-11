// Checks where a dragged Parsons line lands and when dragging scrolls the page.
// Run:  node --test tests/js/parsons-touch.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const { dropIndex, restingRects, scrollDelta } = await import('../../src/Brilliant.Lessons.UI/wwwroot/parsons-touch.js');
const rects = [0, 1, 2, 3].map(i => ({ top: i * 50, bottom: i * 50 + 44 }));

test('a pointer over a row, or in the gap below it, lands on that row', () => {
    assert.equal(dropIndex(rects, 10), 0);
    assert.equal(dropIndex(rects, 44), 1);   // the gap after row 0 belongs to the next row
    assert.equal(dropIndex(rects, 120), 2);
});

test('above the first row lands first, below the last lands last', () => {
    assert.equal(dropIndex(rects, -30), 0);
    assert.equal(dropIndex(rects, 900), 3);
});

test('dragging scrolls only near the window edges', () => {
    assert.ok(scrollDelta(10, 800) < 0);
    assert.ok(scrollDelta(790, 800) > 0);
    assert.equal(scrollDelta(400, 800), 0);
});

test('the dragged row is measured where it started, so dragging it down does not leave it as its own target', () => {
    // Row 0 has followed the finger 130px down, so its box now sits over row 2; the finger is at y=120.
    const moved = rects.map((r, i) => i === 0 ? { top: r.top + 130, bottom: r.bottom + 130 } : r);
    assert.equal(dropIndex(moved, 120), 0);   // what went wrong on a phone: the moved row always matched first
    assert.equal(dropIndex(restingRects(moved, 0, 130), 120), 2);
});

// ---- attach/detach, driven through a minimal fake DOM (no browser needed) ----

/** Four rows resting at tops 0/50/100/150, 44 tall, whose boxes follow their translateY transform like a real layout would. */
function harness() {
    const frames = [];
    const scrolls = [];
    globalThis.window = { scrollY: 0, innerHeight: 800, scrollBy: (_x, y) => { scrolls.push(y); window.scrollY += y; } };
    globalThis.requestAnimationFrame = fn => frames.push(fn);   // frames.length is the number of loops waiting
    globalThis.cancelAnimationFrame = () => { frames.length = 0; };

    const handlers = {};
    const moves = [];
    const rows = [0, 1, 2, 3].map(i => {
        const row = { dataset: { row: String(i) }, style: { transform: '' } };
        const shift = () => Number(/translateY\((-?[\d.]+)px\)/.exec(row.style.transform)?.[1] ?? 0);
        row.getBoundingClientRect = () => ({ top: i * 50 + shift(), bottom: i * 50 + 44 + shift() });
        row.grip = { closest: sel => sel === '.grip' ? row.grip : row, setPointerCapture() {}, releasePointerCapture() {} };
        return row;
    });
    const list = {
        dataset: {},
        querySelectorAll: () => rows,
        addEventListener: (type, fn) => (handlers[type] ??= []).push(fn),
        removeEventListener: (type, fn) => { handlers[type] = (handlers[type] ?? []).filter(h => h !== fn); },
    };
    const fire = (type, y, row = 0) => (handlers[type] ?? []).slice().forEach(h => h({
        type, pointerId: 1, pointerType: 'touch', clientY: y, target: rows[row].grip, preventDefault() {}, stopPropagation() {},
    }));
    const ref = { invokeMethodAsync: (name, from, to) => { moves.push([name, from, to]); return Promise.resolve(); } };
    return { list, rows, fire, ref, moves, frames, scrolls, handlers };
}

test('a line lands where the finger lifted, even if the last move was somewhere else', async () => {
    const { attach } = await import('../../src/Brilliant.Lessons.UI/wwwroot/parsons-touch.js');
    const h = harness();
    attach(h.list, h.ref);

    h.fire('pointerdown', 10);
    h.fire('pointermove', 120);   // row 0 now sits 110px down, over row 2
    h.fire('pointerup', 60);      // but the finger lifted over row 1

    assert.deepEqual(h.moves, [['MoveTo', 0, 1]]);
    assert.equal(h.rows[0].style.transform, '');
});

test('lifting where the line started moves nothing', async () => {
    const { attach } = await import('../../src/Brilliant.Lessons.UI/wwwroot/parsons-touch.js');
    const h = harness();
    attach(h.list, h.ref);

    h.fire('pointerdown', 10);
    h.fire('pointermove', 120);
    h.fire('pointerup', 20);

    assert.deepEqual(h.moves, []);
});

test('detaching in the middle of an edge drag stops the auto-scroll and puts the row back', async () => {
    const { attach, detach } = await import('../../src/Brilliant.Lessons.UI/wwwroot/parsons-touch.js');
    const h = harness();
    attach(h.list, h.ref);

    h.fire('pointerdown', 700);
    h.fire('pointermove', 790);   // within the bottom edge: scrolls and queues the next frame
    assert.equal(h.frames.length, 1);

    const queued = h.frames.slice();
    detach(h.list);
    queued.forEach(f => f());      // a frame that was already due must not scroll again

    assert.equal(h.frames.length, 0);
    assert.deepEqual(h.scrolls, [14]);
    assert.equal(h.rows[0].style.transform, '');
    assert.equal(h.rows[0].dataset.touchDrag, undefined);
    assert.deepEqual(h.moves, []);
    assert.equal((h.handlers.pointerup ?? []).length, 0);
});
