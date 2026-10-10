// CodeMirror 6 behind the C# ICodeEditor interface (see CodeEditor.razor). Bundled by esbuild into
// ../wwwroot/code-editor.js so it works offline inside BlazorWebView on Mac Catalyst and Android.
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, rectangularSelection, crosshairCursor, Decoration } from '@codemirror/view';
import { Annotation, EditorState, StateEffect, StateField } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, indentOnInput, indentUnit, syntaxHighlighting } from '@codemirror/language';
import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { setDiagnostics, lintGutter } from '@codemirror/lint';
import { highlightSelectionMatches, selectNextOccurrence, selectSelectionMatches } from '@codemirror/search';
import { tagHighlighter, tags as t } from '@lezer/highlight';
import { python } from '@codemirror/lang-python';

const editors = new Map();
const pending = new Map();      // id -> { timer, notify } for a change the app hasn't been told about yet
let nextId = 1;

// VS Code-style keys. CodeMirror's default keymap already matches most of them: ⌥↑/↓ move a line, ⇧⌥↑/↓ copy it, ⌘/ comments, ⌘⇧K deletes a line,
// ⌘L selects it, ⌘[ / ⌘] indent, ⌥⌘↑/↓ add a cursor above/below. We add ⌘D (next occurrence) and ⌘⇧L (all occurrences), and drop ⌘↩, which the
// app uses to run the code (the page's key listener claims it before the editor sees it, this just keeps the editor from ever binding it).
const vscodeKeymap = [
    { key: 'Mod-d', run: selectNextOccurrence, preventDefault: true },
    { key: 'Mod-Shift-l', run: selectSelectionMatches, preventDefault: true },
];
const editorKeymap = defaultKeymap.filter(b => b.key !== 'Mod-Enter');

// Token classes the app's CSS colours (theme.css, the --syn-* tokens). Control flow (if/for/return) gets its own colour, as in VS Code.
const highlighter = tagHighlighter([
    { tag: t.controlKeyword, class: 'tok-control' },
    { tag: [t.keyword, t.self], class: 'tok-keyword' },
    { tag: [t.bool, t.null], class: 'tok-bool' },
    { tag: [t.string, t.special(t.string)], class: 'tok-string' },
    { tag: t.number, class: 'tok-number' },
    { tag: t.comment, class: 'tok-comment' },
    { tag: [t.function(t.variableName), t.function(t.definition(t.variableName)), t.definition(t.function(t.variableName)), t.meta], class: 'tok-function' },
    { tag: [t.className, t.typeName, t.definition(t.className), t.definition(t.typeName)], class: 'tok-class' },
    { tag: [t.variableName, t.propertyName, t.definition(t.variableName)], class: 'tok-variable' },
]);

const CHANGE_DELAY_MS = 500;    // how long typing must pause before the app is told (autosave)
const programmatic = Annotation.define();   // marks edits made by setCode, which the app already knows about

// One highlighted line at a time (e.g. the line a traceback points at).
const setHighlight = StateEffect.define();
const highlightField = StateField.define({
    create: () => Decoration.none,
    update(deco, tr) {
        deco = deco.map(tr.changes);
        for (const e of tr.effects) {
            if (!e.is(setHighlight)) continue;
            if (e.value == null || e.value < 1 || e.value > tr.state.doc.lines) return Decoration.none;
            return Decoration.set([Decoration.line({ class: 'cm-highlighted-line' }).range(tr.state.doc.line(e.value).from)]);
        }
        return deco;
    },
    provide: f => EditorView.decorations.from(f),
});

// Colours come from the app's CSS custom properties, so the editor follows the light/dark theme live.
const theme = EditorView.theme({
    '&': { backgroundColor: 'var(--code-bg)', color: 'var(--code-fg)', fontSize: '.95rem', borderRadius: '8px', border: '2px solid var(--border)' },
    '&.cm-focused': { outline: 'none', borderColor: 'var(--accent)' },
    '.cm-scroller': { fontFamily: 'ui-monospace, Menlo, monospace', lineHeight: '1.5', minHeight: '9rem', maxHeight: '28rem' },
    '.cm-content': { caretColor: 'var(--code-fg)' },
    '&.cm-focused .cm-cursor': { borderLeftColor: 'var(--code-fg)' },
    '.cm-gutters': { backgroundColor: 'transparent', color: 'var(--code-muted)', border: 'none' },
    '.cm-activeLine, .cm-activeLineGutter': { backgroundColor: 'var(--surface)' },
    '.cm-selectionBackground, &.cm-focused .cm-selectionBackground': { backgroundColor: 'var(--selection)' },
    '.cm-selectionMatch': { backgroundColor: 'var(--selection-match)' },
    '&.cm-focused .cm-matchingBracket': { backgroundColor: 'var(--bracket-match)', outline: '1px solid var(--code-muted)' },
    '&.cm-focused .cm-nonmatchingBracket': { backgroundColor: 'var(--warn-bg)' },
    '.cm-highlighted-line': { backgroundColor: 'var(--warn-bg)' },
    '.cm-diagnostic': { fontFamily: 'inherit' },
});

/** onChanged: optional DotNetObjectReference whose OnJsChanged(code) is called, debounced, after the learner edits. */
export function create(element, code, onChanged) {
    let id;
    const view = new EditorView({
        parent: element,
        state: EditorState.create({
            doc: code ?? '',
            extensions: [
                lineNumbers(), highlightActiveLine(), highlightActiveLineGutter(), drawSelection(),
                history(), indentOnInput(), bracketMatching(), closeBrackets(), lintGutter(), highlightSelectionMatches(),
                indentUnit.of('    '), EditorState.tabSize.of(4),
                // Multiple cursors: ⌥-click (or ⌘-click) adds one, ⇧⌥-drag selects a column, ⌘D / ⌘⇧L select occurrences.
                EditorState.allowMultipleSelections.of(true),
                EditorView.clickAddsSelectionRange.of(e => e.altKey || e.metaKey),
                rectangularSelection({ eventFilter: e => e.altKey && e.shiftKey && e.button === 0 }),
                crosshairCursor({ key: 'Alt' }),
                keymap.of([...closeBracketsKeymap, ...vscodeKeymap, ...editorKeymap, ...historyKeymap, indentWithTab]),
                python(), syntaxHighlighting(highlighter),
                EditorView.contentAttributes.of({ 'aria-label': 'Code editor', spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off' }),
                highlightField, theme,
                EditorView.updateListener.of(update => {
                    if (!onChanged || !update.docChanged || update.transactions.some(t => t.annotation(programmatic))) return;
                    const notify = () => { pending.delete(id); return onChanged.invokeMethodAsync('OnJsChanged', update.view.state.doc.toString()); };
                    clearTimeout(pending.get(id)?.timer);
                    pending.set(id, { timer: setTimeout(notify, CHANGE_DELAY_MS), notify });
                }),
            ],
        }),
    });
    id = nextId++;
    editors.set(id, view);
    return id;
}

const get = id => {
    const view = editors.get(id);
    if (!view) throw new Error('Code editor ' + id + ' does not exist (disposed?).');
    return view;
};

export const getCode = id => get(id).state.doc.toString();

export function setCode(id, code) {
    const view = get(id);
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code ?? '' }, effects: setHighlight.of(null), annotations: programmatic.of(true) });
    cancelPending(id);
}

/** markers: [{ line (1-based), message }] */
export function setErrorMarkers(id, markers) {
    const view = get(id);
    const doc = view.state.doc;
    const diagnostics = markers
        .filter(m => m.line >= 1 && m.line <= doc.lines)
        .map(m => { const line = doc.line(m.line); return { from: line.from, to: Math.max(line.to, line.from + 1 <= doc.length ? line.from + 1 : line.from), severity: 'error', message: m.message }; });
    view.dispatch(setDiagnostics(view.state, diagnostics));
}

export const clearErrorMarkers = id => setErrorMarkers(id, []);

/** line: 1-based, or null to clear. Also scrolls it into view. */
export function highlightLine(id, line) {
    const view = get(id);
    const effects = [setHighlight.of(line)];
    if (line >= 1 && line <= view.state.doc.lines)
        effects.push(EditorView.scrollIntoView(view.state.doc.line(line).from, { y: 'nearest' }));
    view.dispatch({ effects });
}

function cancelPending(id) {
    clearTimeout(pending.get(id)?.timer);
    pending.delete(id);
}

/** Tells the app about a not-yet-reported edit right now (used before the editor goes away). */
export async function flush(id) {
    const p = pending.get(id);
    if (!p) return;
    clearTimeout(p.timer);
    await p.notify();
}

export const focus = id => get(id).focus();

export function dispose(id) {
    cancelPending(id);
    editors.get(id)?.destroy();
    editors.delete(id);
}
