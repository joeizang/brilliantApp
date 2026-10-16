import { StreamLanguage } from '@codemirror/language';
import { python } from '@codemirror/lang-python';
import { csharp } from '@codemirror/legacy-modes/mode/clike';

// Keep the existing Python grammar unless the exercise explicitly selects C#.
export function languageSupport(language = 'python') {
    return language === 'csharp' ? StreamLanguage.define(csharp) : python();
}
