# Recorded C# async timelines

An `async-timeline` step is a pack-time recording. It does not run learner code on the device. The player groups events into thread lanes and keeps their sequence numbers visible so thread hops remain understandable on a phone as well as a Mac.

```yaml
- id: step.async.deadlock
  type: async-timeline
  title: Why does Result deadlock here?
  body: The UI thread blocks for an operation whose continuation needs that same thread.
  events:
    - sequence: 1
      thread: UI
      kind: enter
      label: LoadAsync starts
      state: Start
    - sequence: 2
      thread: UI
      kind: await
      label: The operation is incomplete, so the method yields
      operation: load
      state: AwaitingLoad
    - sequence: 3
      thread: UI
      kind: blocking-wait
      label: Result synchronously waits for the task
      operation: load
    - sequence: 4
      thread: Worker
      kind: continuation-queued
      label: The async method's continuation is ready
      operation: load
      continuationThread: UI
      state: AwaitingLoad
    - sequence: 5
      thread: UI
      kind: resume
      label: The continuation would run after the UI thread is released
      state: AfterAwait
```

`thread` is where the recorded event occurs. A `continuation-queued` event names its destination in `continuationThread`. `state` is the authored state-machine state at that point. Sequence values must be positive and unique; the packer orders events by sequence. Await, continuation-queued and blocking-wait events identify an `operation`, which lets the player mark the sync-over-async cycle when a blocked thread is also the destination for that operation's continuation. The deadlock note is derived from these recorded facts rather than authored as a separate claim.

Supported event kinds are `enter`, `await`, `continuation-queued`, `resume`, `complete` and `blocking-wait`. The C# app manifest enables this step type; the DSA manifest leaves it disabled.
