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
