# C# Track 4 — Concurrency and the memory model

Issue [#163](https://github.com/joeizang/brilliantApp/issues/163), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
Source: `content/csharp/tracks/05-concurrency-and-the-memory-model/`. It is built on the [`interleave` step](csharp-interleave.md) (#162).

The track replaces "it works on my machine" with a question a principal engineer can answer: *what orders does this code allow?* Race
conditions and deadlocks are properties of interleavings, so the learner builds the bad order themselves in the interleaving player, then
learns the tool that removes it. Every interleave puzzle is checked at pack time: all one-thread-after-another orders hold the invariant,
and the authored breaking order breaks it.

| Lesson | What it establishes | Interleave puzzle |
|---|---|---|
| The thread pool and lost updates | Work items run on a few reused workers; blocking them starves the pool; `counter++` is a load and a store. | Two threads lose an update |
| Locks, Lock, and SemaphoreSlim | A lock excludes only code that takes it; `lock` lowers to `Monitor` or `Lock.EnterScope`; a semaphore counts permits and can be awaited. | One thread forgot the lock |
| Interlocked and atomic updates | `Interlocked` makes one read-modify-write indivisible; `CompareExchange` returns the previous value and builds retry loops; atomic calls do not compose. | A deposit races a withdrawal |
| Volatile and reordering | Compiler, JIT and CPU reorder; volatile is acquire/release, not atomicity; `Interlocked` is a full fence while `Monitor.Enter`/`Exit` are acquire/release. | Three threads, volatile counter |
| Concurrent collections, Parallel and PLINQ | Per-call safety is not sequence safety; `GetOrAdd` may run its factory twice; parallel bodies race on shared state; `AsOrdered`. | none (prediction and decision steps) |
| Finding races and deadlocks | Read-modify-write and check-then-act shapes; the lock must cover the read; lock ordering; tests are weak evidence. | Lock covers only the write; two transfers deadlock |

Each lesson has three or four concepts and a review item for every concept, so **I know this — test me** and Review can select it.

## Why the memory model is taught with questions, not a simulator

The interleave machine executes sequentially consistent loads, stores and locks. It cannot show a CPU reordering a store past a load, and
the track does not pretend otherwise. The volatile lesson therefore uses the interleaver for what it *can* show (volatile gives no
atomicity: even when every load sees the latest store, which the player assumes and the language does not promise, the total is still wrong) and uses decision questions for reordering, hoisting and
publication, stating the guarantees from the .NET memory model.

Predict-output answers were run on .NET 10 (a lone `Parallel.For` with `Interlocked.Add`, `AsOrdered` PLINQ, `GetOrAdd` called twice on one key, `SemaphoreSlim` counts, `CompareExchange` return values) and the lowered `lock` output was read from the built pack.

Runtime facts follow the Microsoft documentation for [the managed thread pool](https://learn.microsoft.com/en-us/dotnet/standard/threading/the-managed-thread-pool), [`lock`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock), [`System.Threading.Lock`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock), [`SemaphoreSlim`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.semaphoreslim), [`Interlocked`](https://learn.microsoft.com/en-us/dotnet/api/system.threading.interlocked), [`volatile`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/keywords/volatile), [`ConcurrentDictionary.GetOrAdd`](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd) and [PLINQ ordering](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/order-preservation-in-plinq).
