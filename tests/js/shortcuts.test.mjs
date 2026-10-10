// Checks which key presses become step commands.
// Run:  node --test tests/js/shortcuts.test.mjs
import test from 'node:test';
import assert from 'node:assert/strict';

const { classify, pageScope, targetKind } = await import('../../src/Brilliant.Lessons.UI/wwwroot/shortcuts.js');
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

// ---- the trace player: arrows step, space plays and pauses

const trace = { trace: true };

test('the arrow keys step through a trace, and only while a trace player is on the page', () => {
    assert.deepEqual(classify(key('ArrowRight'), 'other', trace), { command: 'next', number: 0 });
    assert.deepEqual(classify(key('ArrowLeft'), 'other', trace), { command: 'previous', number: 0 });
    assert.equal(classify(key('ArrowRight'), 'other'), null);
    assert.equal(classify(key('ArrowLeft'), 'other', { trace: false }), null);
});

test('the arrows also work when a button has focus, since they do nothing to a button', () => {
    assert.deepEqual(classify(key('ArrowRight'), 'button', trace), { command: 'next', number: 0 });
    assert.deepEqual(classify(key('ArrowLeft'), 'button', trace), { command: 'previous', number: 0 });
});

test('the arrows move the cursor in text boxes and the code editor, so they are left alone there', () => {
    for (const k of ['ArrowLeft', 'ArrowRight']) assert.equal(classify(key(k), 'text', trace), null, k);
});

test('holding an arrow keeps stepping', () => {
    assert.deepEqual(classify(key('ArrowRight', { repeat: true }), 'other', trace), { command: 'next', number: 0 });
});

test('arrows with a modifier belong to the browser and the OS (history, line start and end, the Back menu item)', () => {
    for (const m of ['metaKey', 'ctrlKey', 'altKey', 'shiftKey'])
        for (const k of ['ArrowLeft', 'ArrowRight']) assert.equal(classify(key(k, { [m]: true }), 'other', trace), null, `${m} ${k}`);
});

test('up and down are not ours: they scroll the page', () => {
    for (const k of ['ArrowUp', 'ArrowDown']) assert.equal(classify(key(k), 'other', trace), null, k);
});

test('space plays and pauses a trace, but only away from controls that space already activates', () => {
    assert.deepEqual(classify(key(' '), 'other', trace), { command: 'playpause', number: 0 });
    assert.equal(classify(key(' '), 'button', trace), null);    // a focused button clicks itself on space
    assert.equal(classify(key(' '), 'text', trace), null);      // a space in a text box is a space
    assert.equal(classify(key(' '), 'other'), null);            // elsewhere space scrolls the page
});

test('space does not repeat while held, and with a modifier it is not ours', () => {
    assert.equal(classify(key(' ', { repeat: true }), 'other', trace), null);
    for (const m of ['metaKey', 'ctrlKey', 'altKey', 'shiftKey']) assert.equal(classify(key(' ', { [m]: true }), 'other', trace), null, m);
});

test('a trace player on the page changes nothing about return, command-return or the digits', () => {
    assert.deepEqual(classify(key('Enter'), 'other', trace), { command: 'advance', number: 0 });
    assert.deepEqual(classify(key('Enter', { metaKey: true }), 'text', trace), { command: 'run', number: 0 });
    assert.deepEqual(classify(key('2'), 'other', trace), { command: 'choose', number: 2 });
});

test('a key press during IME composition is never ours', () => {
    assert.equal(classify(key('ArrowRight', { isComposing: true }), 'other', trace), null);
    assert.equal(classify(key(' ', { isComposing: true }), 'other', trace), null);
});

// ---- what counts as a text box

globalThis.document = { body: {}, documentElement: {} };

function element(overrides) {
    return { tagName: 'DIV', isContentEditable: false, closest: () => null, ...overrides };
}

test('the code editor is a text box, so arrows move its cursor', () => {
    const editor = element({ closest: sel => (sel === '.cm-editor' ? {} : null) });
    assert.equal(targetKind(editor), 'text');
});

test("the trace player's read-only code is not: clicking it must not stop the arrows stepping", () => {
    const readOnly = element({ closest: sel => (sel === '.cm-editor' || sel === '.trace-player' ? {} : null) });
    assert.equal(targetKind(readOnly), 'other');
});

test('the arrows are only live when a trace player is on the page', () => {
    const page = present => ({ querySelector: selector => (present && selector === '.trace-player' ? {} : null) });

    assert.deepEqual(pageScope(page(true)), { trace: true });
    assert.deepEqual(pageScope(page(false)), { trace: false });
});
