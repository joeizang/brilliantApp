# The `lowered` step and the lowering service

Issue [#154](https://github.com/joeizang/brilliantApp/issues/154), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).

C# hides a lot behind small keywords: `foreach` is an enumerator and a `try`/`finally`, `await` is a state machine, a lambda that
captures a variable is a generated class. A `lowered` step shows the learner a snippet next to what the compiler made of it, and asks
a question about the lowered form.

This work ships in two pull requests. **This one** is the model, the lowering service, the loader and validator, the pack attach and
the golden tests. The next adds the view (phone and Mac), the toggle between lowered C# and IL, and turns the step on in
`apps/csharp/manifest.yaml`.

## Authoring

```yaml
- id: step.csharp.foreach.lowered
  type: lowered
  title: What foreach becomes
  prompt: Which call ends the loop?
  code: |
    static class Demo { public static int Total(List<int> items) { var sum = 0; foreach (var item in items) sum += item; return sum; } }
  options:               # or: accepted: [MoveNext] (+ mistakes), exactly like predict-output
    - { text: MoveNext, correct: true, feedback: The loop runs while MoveNext returns true. }
    - { text: Dispose }
```

- The answer side is the same as `predict-output`: typed (`accepted`, optional `mistakes`) or multiple choice (`options`, exactly one
  correct). Hints are allowed. A walkthrough is not (there is no solution to walk through).
- `language` may be left out or must be `csharp`.
- **Authors never write the lowered C# or the IL.** `LoweredStep.Lowered` is filled in when the pack is built.
- The snippet is compiled with the same implicit usings as the rest of the C# app (`System`, `System.Collections.Generic`,
  `System.Linq`); add any other `using` yourself. A snippet with top-level statements becomes a console app, otherwise a library.
- `pack.yaml` must pin `targetFramework` and `langVersion` like any other C# content, because the compiler version decides the output.

## The lowering service

`tools/Brilliant.Cli/CSharpLowering.cs`, run by `ContentSource.Load` once the content is structurally valid:

1. Roslyn compiles the snippet in **Release** mode (`deterministic: true`, nullable annotations off so no `[Nullable]` noise) against the
   pinned reference assemblies. Release matters: it makes the async state machine a struct, as production code is.
2. `ICSharpCode.Decompiler` reads the in-memory assembly back with the `CSharp1` language level and `foreach`, `using`, `lock` and
   async-enumerator reconstruction switched off, so the compiler's enumerators, `try`/`finally` blocks and state machines stay visible.
3. The same decompiler's `ReflectionDisassembler` prints the IL for the same types.
4. Compiler plumbing (`<Module>`, `<PrivateImplementationDetails>`, embedded attributes) is filtered out, and output is tidied (LF line
   endings, spaces, no RVAs or header sizes) so it is identical on every machine.

Nothing is run, so there is no worker process. A snippet that doesn't compile blocks the pack, naming the line:
`the code does not compile (line 3: CS0029 …)`. Each form is capped at 1000 lines and the snippet at 16 KB.

## Tests

- `tests/Brilliant.Cli.Tests/Lowering/*.snippet` are the fixtures (async, foreach, yield, closure, using, top-level statements), each with
  a `.lowered.txt` and `.il` golden. A change in Roslyn or the decompiler shows up as a diff; rewrite with `UPDATE_GOLDENS=1` after
  reading it. A second theory proves lowering twice gives the same text.
- `LoweredStepTests` cover the pack round trip, byte-identical repacks, compile errors, missing fields, typed and choice answers, and the
  allow-list: the DSA manifest does not enable `lowered`, and a manifest that omits it gets
  `step type 'lowered' is not enabled by app …`.

## Not in this PR

The view and the IL/C# toggle, enabling the step in the C# manifest, and how `lowered` steps feed test-out difficulty, review and rating
inference (they use the defaults until then).
