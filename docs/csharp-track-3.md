# C# Track 3 — Memory and the GC

Issue [#161](https://github.com/joeizang/brilliantApp/issues/161), part of the C# app ([#137](https://github.com/joeizang/brilliantApp/issues/137)).
Source: `content/csharp/tracks/04-memory-and-gc/`.

The track treats memory as a lifetime and ownership problem, not as a contest to avoid every allocation. Each lesson ties a concrete decision to the runtime evidence available in the pack: the stack-and-heap trace shows reachable references at recorded probes, allocation exercises set a per-call budget, and BenchmarkDotNet records allocation rankings with its machine and runtime.

| Lesson | What it establishes | Signature step |
|---|---|---|
| Generations and survival | Roots determine liveness; survivors are promoted through generations; Gen 1 buffers young and long-lived objects. | Recorded memory view and measured allocation comparison |
| Large and pinned heaps | The LOH threshold, its Gen 2 collection behavior, and when POH allocations or pinning are justified. | LOH/POH decision questions |
| Spans, pools, and hidden allocations | `Span<T>`/`stackalloc` lifetimes, `Memory<T>` across async boundaries, `ArrayPool<T>` ownership, and avoiding temporary LINQ collections. | Zero-allocation rewrite with a phone alternative |
| Disposal and finalization | Deterministic `IDisposable` cleanup, compiler-generated `try/finally`, finalizers as fallback, and `SafeHandle` for native handles. | Lowered `using` declaration |

The memory view is a snapshot, not a GC simulator: it shows frames, roots and reachable objects but does not claim to show an object's generation or trigger a collection. Allocation counts and benchmark rankings are recorded on the build machine; the content asks learners to reason from the recorded environment and does not turn a one-machine timing into a universal promise.

The runtime facts follow the Microsoft documentation for [.NET GC fundamentals](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/fundamentals), the [large object heap](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/large-object-heap), [`GC.AllocateArray`](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocatearray), [`ref struct` types](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/builtin-types/ref-struct), [`stackalloc`](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc), the [Dispose pattern](https://learn.microsoft.com/en-us/dotnet/standard/design-guidelines/dispose-pattern), and [finalizers](https://learn.microsoft.com/en-us/dotnet/csharp/programming-guide/classes-and-structs/finalizers).
