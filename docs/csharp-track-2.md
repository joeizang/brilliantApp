# C# Track 2 — Async in depth

Issues [#159](https://github.com/joeizang/brilliantApp/issues/159) and [#160](https://github.com/joeizang/brilliantApp/issues/160), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
Source: `content/csharp/tracks/03-async-in-depth/`.

The aim is to replace the vague rule “async means another thread” with a model a senior engineer can use to read failure reports and design APIs. Every lesson asks about a concrete runtime fact before explaining it. The async timeline is a pack-time recording: it has named thread lanes, global event sequence numbers, await states and explicit continuation destinations, so it plays on the phone as well as the Mac. It never runs learner code.

| Lesson | What it establishes | Signature step |
|---|---|---|
| State machines, Task, and ValueTask | An incomplete await returns to the caller; compiler-generated state keeps locals and the resume point; Task is the default and ValueTask is a measured fast-path optimisation. | Lowered `async` method |
| Contexts, ConfigureAwait, and deadlocks | SynchronizationContext determines the continuation destination; ConfigureAwait(false) avoids its capture, not ExecutionContext; blocking that destination creates a cycle. | Async timeline, including sync-over-async deadlock |
| Cancellation and async disposal | Cancellation is cooperative and must be propagated; await using awaits cleanup. | Cancelled Task.Delay prediction |
| Async streams and channels | IAsyncEnumerable yields over time, bounded channels exert backpressure, and await foreach disposes early exits. | Async iterator prediction |

Each lesson has three concepts and three review questions, so **I know this — test me** can select it. The current content intentionally uses recorded timeline events instead of asserting a scheduler's incidental thread behaviour. Real thread identities vary between runs; the authored recording names the meaningful lanes and explains the causal edge.

## Authoring an async timeline

See [async timeline format](csharp-async-timeline.md). A timeline event has a positive, unique sequence, a lane (`thread`), a kind and a learner-facing label. `await`, `continuation-queued`, and `blocking-wait` name their operation. A continuation also names its destination lane. When the same operation has a blocking wait and a continuation queued to that blocked lane, the player derives its sync-over-async deadlock warning from those facts.
