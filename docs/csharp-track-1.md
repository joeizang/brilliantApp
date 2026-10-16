# C# Track 1 — Types and the compiler

Issue [#158](https://github.com/joeizang/brilliantApp/issues/158), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
Source: `content/csharp/tracks/02-types-and-the-compiler/` (the starter lesson stays in `01-first-values`).

The reader is an experienced C# developer. Nobody needs `struct` versus `class` explained, so each lesson starts from what the
learner probably already believes and uses the mechanics views (the stack and heap, lowered code, allocations) to test it. Every lesson
declares 3–4 concepts, each with a review item that is a question (choice, predict-output, lowered or write-code), so **I know this — test me**
([lesson-test-out.md](lesson-test-out.md)) is offered on every one. A learner who knows a lesson tests out of it; one who doesn't plays it.
Code is current C# (.NET 10 / C# 14) throughout: primary constructors, collection expressions, `field`, extension members and
`required`/`init` are the norm, and an older spelling appears only to say what it became.

## Lessons, three pull requests

| PR | Lesson | What it shows |
|---|---|---|
| 1 | Where values live | The stack-and-heap view: a struct copy is a second set of fields, a reference is a second arrow, passing either by value copies the variable. |
| 1 | Records and value equality | What `record` and `record struct` generate; `with` copies; equality is by value, identity is not. |
| 1 | What the compiler writes | Lowered steps: `foreach`, a captured variable's closure class, a primary constructor's parameter capture, and what a capturing lambda allocates. |
| 2 | `in`, `ref` and `out` | Aliasing a variable: the memory view with a `ref` local, defensive copies of an `in` struct, `ref readonly`. |
| 2 | Boxing | Where a box comes from (`object`, interfaces, non-generic collections), allocations questions, and generics as the way out. |
| 2 | Nullability flow analysis | The compiler's null state follows the code; `?`, `!`, `[NotNull]`-style attributes, and what it cannot know. |
| 3 | Generic constraints and variance | `where T : …`, `in`/`out` on interfaces and delegates, why `List<T>` is invariant and `IEnumerable<T>` is not. |
| 3 | Static abstract members | `INumber<T>`, generic math, operators in interfaces. |
| 3 | Pattern matching, lowered | What `is`, `switch` expressions and list patterns turn into. |

Only the last pull request says "Closes #158".

## Authoring rules this track follows

- Mechanics steps are recorded at pack time (`trace` with `memory`, `lowered`, `allocations`); no lowered code, IL or numbers are typed by hand.
- Every `write-code` step has `largeScreenPreferred: true` and a `phoneAlternative` ([csharp-write-code.md](csharp-write-code.md)).
- A prediction is asked before the mechanism is shown wherever the learner is likely to be wrong, because a wrong guess is what makes the
  view stick; the explanation follows the answer.
- IDs are `lesson.csharp.<slug>`, `step.csharp.<slug>.<what>`, `concept.csharp.<slug>`, `review.csharp.<slug>`.
