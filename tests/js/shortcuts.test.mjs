// Checks which key presses become step commands.
// Run:  node --test tests/js/shortcuts.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const { classify } = await import('../../src/Brilliant.Lessons.UI/wwwroot/shortcuts.js');
const key = (k, mods = {}) => ({ key: k, metaKey: false, ctrlKey: false, altKey: false, shiftKey: false, repeat: false, isComposing: false, ...mods });

test('command-return runs, even inside the code editor or a text box', () => {
    for (const target of ['other', 'button', 'text'])
        assert.deepEqual(classify(key('Enter', { metaKey: true }), target), { command: 'run', number: 0 });
});

test('command-return does not repeat while held, and shift/option/control variants are not ours', () => {
    assert.equal(classify(key('Enter', { metaKey: true, repeat: true }), 'other'), null);
    for (const m of ['shiftKey', 'altKey', 'ctrlKey'])
        assert.equal(classify(key('Enter', { metaKey: true, [m]: true }), 'other'), null);
});

test('return advances only where it does nothing else', () => {
    assert.deepEqual(classify(key('Enter'), 'other'), { command: 'advance', number: 0 });
    assert.equal(classify(key('Enter'), 'text'), null);     // newline in a textarea, submit in an input
    assert.equal(classify(key('Enter'), 'button'), null);   // the button clicks itself
    assert.equal(classify(key('Enter', { repeat: true }), 'other'), null);
    assert.equal(classify(key('Enter', { shiftKey: true }), 'other'), null);
});

test('digits 1 to 4 pick answers away from text, and only those digits', () => {
    for (const d of [1, 2, 3, 4]) {
        assert.deepEqual(classify(key(String(d)), 'other'), { command: 'choose', number: d });
        assert.deepEqual(classify(key(String(d)), 'button'), { command: 'choose', number: d });
        assert.equal(classify(key(String(d)), 'text'), null);
    }
    for (const k of ['0', '5', '9', '10', 'a', 'F1', 'Enter2']) assert.equal(classify(key(k), 'other'), null, k);
});

test('digits with a modifier are left to the browser and the OS', () => {
    for (const m of ['metaKey', 'ctrlKey', 'altKey', 'shiftKey']) assert.equal(classify(key('1', { [m]: true }), 'other'), null, m);
});

test('keys during IME composition are ignored', () => {
    assert.equal(classify(key('Enter', { isComposing: true }), 'other'), null);
    assert.equal(classify(key('Enter', { metaKey: true, isComposing: true }), 'other'), null);
});
