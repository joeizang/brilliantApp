# Content distribution

Issue [#31](https://github.com/joeizang/brilliantApp/issues/31), PRD stories 66 and 68. New lessons reach the apps without a rebuild: you publish a Content Pack from your Mac to the household server, and each paired device downloads it over the home network and keeps it for offline use.

```
content/ ──validate──▶ brilliant publish ──▶ server (Postgres) ──▶ app: list, download, cache ──▶ used at next start
```

## Publish a pack

One-time, from the Mac that has the `content/` folder: pair the command line with the server, exactly like a phone (the code comes from [server.md](server.md#pair-a-device)).

```bash
docker compose exec -T server dotnet Brilliant.Server.dll pairing-code
dotnet run --project tools/Brilliant.Cli -- pair 192.168.1.20:8080 ABCD-2345 --name "Mac CLI"
```

The CLI keeps its credentials in `~/.brilliant/cli-credentials.json` (mode 600; override the path with `BRILLIANT_CLI_CREDENTIALS`). It shows up in the server's device list like any device and is revoked the same way. **Any paired device may publish**; the household is the trust boundary, as it is for progress.

Then, whenever the content changes and `version:` in `content/pack.yaml` has been raised:

```bash
dotnet run --project tools/Brilliant.Cli -- publish content
```

- **The content is validated first**, with the same checks as `validate` (including reference solutions). If anything fails, the errors are printed, `nothing was published.` is said, and nothing is sent.
- The server then checks the pack **again**, whoever sent it: the zip must load as a Content Pack of the format it understands, the pack ID must be 1 to 100 letters, digits, `.`, `-` or `_` (not starting with a dot), the version must be three numbers (`1.2.3`), it may unpack to at most 100 MB, and the upload is at most 20 MB.
- **Versions only go up** (compared as numbers, so `0.10.0` is newer than `0.9.0`):
  - Publishing the same version with the **same bytes** again is fine and changes nothing (packs are byte-reproducible, so an unchanged `content/` gives the same bytes).
  - The same version with **different contents** is refused (`409`): raise the version.
  - A version **lower than the newest** is refused (`409`). To go back, republish the older content under a higher version.
- **Dropped IDs are listed.** If the previous version had step, lesson or track IDs that the new one lacks, `publish` prints them as a warning. The publish still succeeds: the learner's progress on those IDs is kept in their log, it just stops showing (see below).

Every version is kept whole on the server, so older versions stay downloadable. Packs live in Postgres, so the [nightly backup](backup.md) covers them.

## How an app gets the update

- After each sync (at start, every 2 minutes, and **Sync now**), a paired app asks `GET /packs` for the newest version of each pack and looks at the one it is running (same pack ID, and a format version it understands; a pack in a newer format waits for a newer app).
- If that version is newer than the running one **and** newer than anything already downloaded, the app downloads it, checks the **size and SHA-256** against the listing, checks that it loads and really is the ID and version it claims, and stores it in `<app data>/packs/{packId}_{version}.zip` (the newest two are kept).
- **The new content is used at the next app start.** It is never swapped under a learner who is in the middle of a lesson. The Sync screen says `Content 0.2.0 downloaded. Restart the app to use it (you're on 0.1.0).`, or `Content 0.1.0 is up to date.`.
- At start, the app runs the cached pack if it is newer than the bundled one, otherwise the bundled one. A cached file that is damaged, or isn't what its name says, is deleted and the app starts from what it had. **Nothing about the cache can stop the app starting.**
- The check never throws at the learner: a server that is away, refuses, or sends a pack that doesn't match its listing just leaves the content as it was (the Sync screen says it couldn't check). A pack the device downloaded and couldn't use isn't fetched again every two minutes.
- Offline, the app uses what it has: the bundled pack plus whatever was downloaded earlier.

## Progress survives an update

Progress is an append-only event log keyed by **stable content IDs** ([sync.md](sync.md)), and state is projected from the log against whichever pack is running. So:

| The update | What the learner sees |
|---|---|
| Edits a step (same ID) | Their progress on it carries over. |
| Adds a step to a lesson | The lesson shows the new step; a lesson they had finished stays finished and shows "new steps" ([NewStepsTests](../tests/Brilliant.Lessons.UI.Tests/NewStepsTests.cs)). |
| Removes a step | Progress on it stays in the log and is ignored. If it was their last unfinished step, the lesson is completed and that is recorded once, so a later update can't undo it (`ProgressRecorder.RecordCompletionsFrom`). |
| Removes a lesson or track | Its progress stays in the log, and flags on it are kept and listed last. |
| **Changes an ID** | The old ID is a removal and the new one is a new item: **progress does not follow a rename.** Keep IDs stable when you reword. |

## API

| Request | Answer |
|---|---|
| `POST /packs` (body: the zip, `application/zip`) | `200 {pack, created, removedIds}`; `400 {error}` invalid pack; `409 {error}` same version with other contents, or older than the newest; `413 {error}` over 20 MB |
| `GET /packs` | `{packs:[{packId, version, formatVersion, sha256, size, publishedAt}]}`: the newest version of each pack |
| `GET /packs/{packId}/{version}` | the zip (`ETag` is the SHA-256); `404` if there is no such pack |

All need a device token. Publishes are serialised with a Postgres advisory lock, so two at once can't both claim "the newest".

## Limits

- Anything paired can publish, and the server trusts the content to be what the author meant; the checks above guard against a broken pack, not a malicious one in the household.
- The whole pack is downloaded each time (about 85 KB now); there are no deltas.
- A device only follows the pack ID it was built with (`pack.track-1`). A different pack ID is not picked up by existing apps.
- Content in a new **format version** needs a new app build; older apps keep what they have.
- Versions can't be unpublished. A mistake is fixed by publishing a higher version.

## Where the code is

| Path | Purpose |
|---|---|
| `tools/Brilliant.Cli/` (`CliApp`, `PackPublisher`) | `pair` and `publish` |
| `src/Brilliant.Server/PackEndpoints.cs`, `Data/Migrations/*ContentPacks*` | the `/packs` endpoints and the `Packs` table |
| `src/Brilliant.Core/Content/PackVersion.cs`, `PackCache.cs` | versions and pack IDs; the on-device cache and `PackStartup.Choose` |
| `src/Brilliant.Core/Sync/PackClient.cs`, `ContentUpdateService.cs` | the app's HTTP client; the check-and-download logic |
| `src/Brilliant.App/MauiProgram.cs`, `src/Brilliant.Lessons.UI/PairingView.razor` | start-up choice of pack; the status on the Sync screen |
| `tests/…/PackEndpointTests.cs`, `ContentDistributionTests.cs`, `PublishTests.cs`, `ContentStatusTests.cs` | server (real Postgres), Core, CLI and Sync-screen tests |
