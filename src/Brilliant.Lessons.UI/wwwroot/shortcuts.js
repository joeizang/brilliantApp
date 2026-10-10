// Turns key presses into step commands (see StepShortcuts.cs). `classify` is pure so it can be tested under node;
// `attach` wires it to the page and tells the app through a DotNetObjectReference.

/**
 * What a key press means, or null if it isn't ours.
 * The digits only count away from text boxes, where they type, and Return only where it does nothing else (a focused button
 * already clicks itself); ⌘↩ works everywhere, including the code editor.
 *
 * e: { key, metaKey, ctrlKey, altKey, shiftKey, repeat, isComposing }, target: 'text' | 'button' | 'other' (see targetKind)
 * Returns { command: 'run' | 'advance' | 'choose', number } or null.
 */
export function classify(e, target) {
    if (e.isComposing) return null;
    if (e.key === 'Enter' && e.metaKey && !e.ctrlKey && !e.altKey && !e.shiftKey)
        return e.repeat ? null : { command: 'run', number: 0 };
    if (e.metaKey || e.ctrlKey || e.altKey || target === 'text') return null;
    if (e.key === 'Enter' && !e.shiftKey && target === 'other')
        return e.repeat ? null : { command: 'advance', number: 0 };
    if (!e.shiftKey && /^[1-4]$/.test(e.key)) return { command: 'choose', number: Number(e.key) };
    return null;
}

/** 'text' where typing should win (inputs, textareas, selects, anything editable, the code editor); 'button' for things Return already activates; else 'other'. */
export function targetKind(el) {
    if (!el || el === document.body || el === document.documentElement) return 'other';
    if (el.isContentEditable || el.closest?.('.cm-editor')) return 'text';
    const tag = el.tagName;
    if (tag === 'TEXTAREA' || tag === 'SELECT') return 'text';
    if (tag === 'INPUT') return ['radio', 'checkbox', 'button', 'submit', 'reset'].includes(el.type) ? 'other' : 'text';
    if (tag === 'BUTTON' || tag === 'A' || tag === 'SUMMARY') return 'button';
    return 'other';
}

let listener = null;

/** Listens on the whole page. onShortcut: DotNetObjectReference with OnShortcut(command, number). */
export function attach(onShortcut) {
    detach();
    listener = ev => {
        const hit = classify(ev, targetKind(ev.target));
        if (!hit) return;
        ev.preventDefault();
        ev.stopPropagation();   // ⌘↩ would otherwise reach the editor's own keymap
        onShortcut.invokeMethodAsync('OnShortcut', hit.command, hit.number);
    };
    window.addEventListener('keydown', listener, true);
}

export function detach() {
    if (listener) window.removeEventListener('keydown', listener, true);
    listener = null;
}
