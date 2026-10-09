// CodeMirror 6 behind the C# ICodeEditor interface (see CodeEditor.razor). Bundled by esbuild into
// ../wwwroot/code-editor.js so it works offline inside BlazorWebView on Mac Catalyst and Android.
import { EditorView, keymap, lineNumbers, highlightActiveLine, highlightActiveLineGutter, drawSelection, Decoration } from '@codemirror/view';
import { EditorState, StateEffect, StateField } from '@codemirror/state';
import { defaultKeymap, history, historyKeymap, indentWithTab } from '@codemirror/commands';
import { bracketMatching, indentOnInput, indentUnit, syntaxHighlighting } from '@codemirror/language';
import { closeBrackets, closeBracketsKeymap } from '@codemirror/autocomplete';
import { setDiagnostics, lintGutter } from '@codemirror/lint';
import { classHighlighter } from '@lezer/highlight';
import { python } from '@codemirror/lang-python';

const editors = new Map();
let nextId = 1;

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
    '.cm-highlighted-line': { backgroundColor: 'var(--warn-bg)' },
    '.cm-diagnostic': { fontFamily: 'inherit' },
});

export function create(element, code) {
    const view = new EditorView({
        parent: element,
        state: EditorState.create({
            doc: code ?? '',
            extensions: [
                lineNumbers(), highlightActiveLine(), highlightActiveLineGutter(), drawSelection(),
                history(), indentOnInput(), bracketMatching(), closeBrackets(), lintGutter(),
                indentUnit.of('    '), EditorState.tabSize.of(4),
                keymap.of([...closeBracketsKeymap, ...defaultKeymap, ...historyKeymap, indentWithTab]),
                python(), syntaxHighlighting(classHighlighter),
                EditorView.contentAttributes.of({ 'aria-label': 'Code editor', spellcheck: 'false', autocapitalize: 'off', autocorrect: 'off' }),
                highlightField, theme,
            ],
        }),
    });
    const id = nextId++;
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
    view.dispatch({ changes: { from: 0, to: view.state.doc.length, insert: code ?? '' }, effects: setHighlight.of(null) });
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

export const focus = id => get(id).focus();

export function dispose(id) {
    editors.get(id)?.destroy();
    editors.delete(id);
}
