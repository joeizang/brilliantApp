import { test } from 'node:test';
import assert from 'node:assert/strict';
import { EditorState } from '@codemirror/state';
import { syntaxTree, ensureSyntaxTree } from '@codemirror/language';
import { languageSupport } from '../src/editor-languages.js';

function tokens(code, language) {
    const state = EditorState.create({ doc: code, extensions: [languageSupport(language)] });
    ensureSyntaxTree(state, code.length, 1000);
    const result = [];
    syntaxTree(state).iterate({ enter(node) {
        if (node.from < node.to) result.push([node.name, code.slice(node.from, node.to)]);
    } });
    return result;
}

test('C# mode recognizes C# declarations, verbatim strings, and comments', () => {
    const parsed = tokens('public class Sample { string path = @"c:\\work"; // comment\n}', 'csharp');
    assert.ok(parsed.some(([kind, text]) => kind === 'keyword' && text === 'class'));
    assert.ok(parsed.some(([kind, text]) => kind === 'string' && text.includes('c:\\work')));
    assert.ok(parsed.some(([kind, text]) => kind === 'comment' && text === '// comment'));
});

test('omitting the language retains Python function and comment parsing', () => {
    const parsed = tokens('def add(a, b):\n    # comment\n    return a + b\n');
    assert.ok(parsed.some(([kind, text]) => kind === 'def' && text === 'def'));
    assert.ok(parsed.some(([kind, text]) => kind === 'Comment' && text === '# comment'));
});
