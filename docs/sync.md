# Syncing progress between devices

Issue [#29](https://github.com/joeizang/brilliantApp/issues/29), PRD stories 52–55. Every device keeps learning offline; when it can reach the household server ([server.md](server.md)) it swaps events with it, and all devices end up showing the same progress.

## The idea

Progress is not stored as state. It is an append-only log of events (`ProgressEvent`: ID, device, time, type, lesson, step, data), and everything the learner sees (streaks, mastery, due reviews, the daily goal, flagged steps) is *projected* from that log by `LearnerStateProjector`. So syncing means one thing: **make every device hold the same set of events.** There is no merge logic, because the projection depends only on the set:

- events are de-duplicated by ID;
- they are ordered by `(OccurredAt, Id ordinal)`, a total order that is the same on every device, whichever order they arrived in.

`EventOrderTests` shuffles a two-device history 200 times and checks the learner state never changes. Finding that test's first run exposed a real bug (`FlaggedSteps.From` depended on arrival order), now fixed.

## What happens in a sync

`SyncService.SyncAsync` does, in order:

1. **Push.** Read this device's local log after `PushedThrough`, keep the events this device *authored* (events pulled from elsewhere are not sent back), and `POST /events` them in batches (at most 500 events and 2,000,000 characters of data each). Events the server could never accept are [held back](#events-the-server-cannot-take).
2. **Pull.** `GET /events?after=PulledThrough`, page by page, appending each event to the local log (idempotent by ID). The cursor is saved as each page lands, so an interrupted sync resumes where it stopped.
3. **Record** `LastSyncedAt`. If anything was pulled, raise `EventsPulled`.

Both directions are idempotent, so a repeated, overlapping or half-finished sync is harmless. Two cursors per pairing are kept in `sync-state.json` (`SyncState`): `PushedThrough` is a position in the local SQLite log (`Seq`), `PulledThrough` is a position in the server's log. A new pairing starts both at zero.

A push that is sent but whose response is lost is simply sent again next time; the server answers `duplicates` for it.

## On the server

`events` table, one row per event, with a server-assigned sequence number that is the pull cursor. Push runs in a transaction that takes a Postgres advisory lock (`Endpoints.PushLock`), so sequence numbers become visible in commit order. Without it a pull could read past a slower concurrent push and its cursor would skip that push's events for good. A test holds the lock and shows a concurrent push waits for it.

`OccurredAt` is stored as ticks (a `long`) so a timestamp round-trips exactly. If it were rounded, the same event would sort differently on two devices.

Limits (`EventLimits`): 500 events per push, 1000 per pull page, IDs up to 100 characters, `Data` up to 1,000,000 characters per event (a long pasted solution is well inside this), and 2,000,000 per push. The server judges each event on its own: valid ones are stored and an invalid one is reported by ID in `rejected`.

### Events the server cannot take

One event the server will not accept must not stop the rest, so the learner's other progress and the pulls always go on. The device does not discard it either:

- Before sending, the client checks each event against the same `EventLimits.Problem` the server uses, and leaves out one that could never be accepted. If the server still names an event in `rejected`, that is recorded too.
- The ID goes in `SyncState.Rejected` for that pairing, the push cursor moves past it, and it is not offered again. The event **stays in the local log**, so it still counts on this device.
- The Sync screen says so: *N items from this device are too large for the server and stay on this device only, so other devices won't see them.* Other devices lack that one event, so their projection can differ in what it contributes (a code submission, for instance).
- Pairing again starts the record over, and the events are checked again.

Because a lifted limit would not retroactively release held events, the limit is generous on purpose; the editor itself has no size cap.

## Code drafts

The learner's in-progress code for a write-code step ([mac-local-dev.md](mac-local-dev.md#code-drafts-issue-12)) travels with the same sync, but it is **not** an event log. A draft is mutable: there is one per step, and **the newest copy wins** (issue [#30](https://github.com/joeizang/brilliantApp/issues/30)).

- **Which wins.** The draft with the later `UpdatedAt` replaces the other, on the server and on each device. On an exact tie the larger code wins (a reset counts as the smallest), so every party settles on the same draft whatever order it sees them in. `CodeDraft.Supersedes` is that rule, used by the store and the server alike.
- **Resets travel.** Reset to starter leaves a tombstone (`Code` null, new `UpdatedAt`). A tombstone is a draft like any other: it beats an older edit on the other device, and loses to an edit made after it.
- **Edits after a pull.** A local save is stamped no earlier than one tick after what the device already holds for that step, so typing on a phone whose clock is a little behind still beats the draft it has just received.
- **Push.** After events, the device sends the drafts it changed since the server last took them (`Unsynced` in `drafts.db`; a file from before sync existed is upgraded and everything in it counts as unsynced). `POST /drafts` keeps a draft only if it supersedes the server's copy. Once sent, a draft is marked synced only if it has not been edited again since it was read, so a keystroke during a push goes up next time. Drafts the server could never take (over 1,000,000 characters) are not sent; they stay on the device.
- **Pull.** `GET /drafts?after=DraftsPulledThrough`, page by page. The server gives a draft a new position in that order each time it is replaced, so a changed draft comes down again. `Merge` applies each one only if it supersedes the local draft; an older one is ignored and the local copy goes up instead. The cursor (`DraftsPulledThrough` in `sync-state.json`) is saved as each page lands.
- **When you see it.** A draft is read when the step opens. A draft that arrives while its step is open is not swapped under the learner's cursor; reopen the step to see it.

Because the winner is chosen by timestamp, two devices editing the *same step* between syncs lose the earlier edit entirely, not a merge of the two. That is the PRD's choice for drafts; submitted solutions are never lost, because each submission is its own event.

## When it runs

- **At app start**, then **every 2 minutes**, by `SyncScheduler` (a `PeriodicTimer`). Errors are swallowed; a sync can never take the app down. In a Debug build started with `BRILLIANT_DEV_START_AT`, the scheduler stays off so scripted runs aren't disturbed.
- **On demand**: **Sync now** on the Sync screen.

One sync runs at a time (a semaphore); pressing **Sync now** during the periodic one waits for it and then checks again.

## What the learner sees

The Sync screen shows *Last synced <time>* (kept across restarts), or why the last attempt failed in plain words:

| `SyncFailure` | Meaning |
|---|---|
| `NotPaired` | no server paired yet |
| `Unreachable` | the server didn't answer (off, wrong network, asleep) |
| `Unauthorized` | the device was revoked on the server |
| `ServerError` | the server answered with an error |
| `LocalError` | something on this device failed (for example the local log) |

**The app works normally with the server away.** Nothing waits on the network, nothing throws at the learner, and the local log is untouched by a failed sync. Everything done meanwhile is pushed at the next successful sync.

Screens showing progress (Today, Review, and so on) redraw when events arrive from another device. `EventsPulled` is raised on a background thread, and the UI marshals it with `InvokeAsync`. `CourseShell` deliberately does **not** refresh in the middle of a lesson or practice session (it would pull the step out from under the learner); it picks the new progress up at the next navigation.

**Review** reads its queue once when it opens, so the shell passes it a `SyncRevision` that changes when progress arrives:

- On "Nothing to review yet", "That's all for today" or "You're all caught up", the queue is read again and the learner sees what the other device just unlocked.
- Mid-queue, the question in front of the learner (and any answer they have chosen) is left alone. At the next question, questions that became due since are added to the end, so the progress bar only ever grows.

## Known limitations

- **Clock skew.** Order is by the device's own clock. Where the *latest* event wins (the daily goal, for instance), a device whose clock is wrong can win or lose unfairly. Counting and de-duplication are unaffected.
- **A server restored from an older backup.** Device cursors would then sit ahead of the server's log and they'd skip new events. Pairing again resets a device's cursors (`SyncState.For`); a gentler recovery belongs with backups ([#32](https://github.com/joeizang/brilliantApp/issues/32)).
- **One learner per household.** Events are not tagged with a learner, so everyone sharing a server shares progress.
- **Plain HTTP** on the LAN, as described in [server.md](server.md#plain-http-on-the-home-network).
- **Draft clock skew.** The same applies, more sharply, to drafts: a device whose clock runs ahead keeps winning until the others catch up past its time (their own next edit does, by the one-tick rule above). A far-future clock can therefore hold a step's draft until it is edited again on another device.
- **Not synced here:** content packs ([#31](https://github.com/joeizang/brilliantApp/issues/31)).

## Where the code is

| Path | Purpose |
|---|---|
| `src/Brilliant.Core/Sync/SyncService.cs` | `SyncService`, `SyncScheduler`, `SyncResult` |
| `src/Brilliant.Core/Sync/SyncClient.cs` | HTTP client for `/events`, and the failure mapping |
| `src/Brilliant.Core/Sync/SyncState.cs` | the cursors and last-synced time |
| `src/Brilliant.Core/Sync/ServerContracts.cs` | request and response types, `EventLimits` |
| `src/Brilliant.Server/Endpoints.cs` | `POST` and `GET /events`, `POST` and `GET /drafts` |
| `src/Brilliant.Core/Drafts/CodeDrafts.cs` | `CodeDraft.Supersedes`, `IDraftSyncSource` |
| `src/Brilliant.Data/SqliteCodeDraftStore.cs` | unsynced tracking and `Merge` on the device |
| `src/Brilliant.Lessons.UI/PairingView.razor` | the status and **Sync now** |
| `tests/Brilliant.Core.Tests/{SyncServiceTests,EventOrderTests}.cs` | the sync loop and order-independence |
| `tests/Brilliant.Server.Tests/{EventSyncTests,DraftSyncTests,TwoDeviceConvergenceTests}.cs` | endpoints and the two-device convergence (events and drafts) against real Postgres |
| `tests/Brilliant.Data.Tests/SqliteDraftSyncTests.cs`, `tests/Brilliant.Core.Tests/DraftSupersedesTests.cs` | the device side and the winning rule |
