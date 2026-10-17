# The household server

A small ASP.NET Core API and PostgreSQL, run with Docker Compose on one machine at home (the Ubuntu laptop). Devices pair with it once using a short code, and from then on carry a long-lived device token. It began as the tracer from [#28](https://github.com/joeizang/brilliantApp/issues/28) (pairing and device management) and now also holds the household's progress event log ([#29](https://github.com/joeizang/brilliantApp/issues/29)) and the current code draft for each step ([#30](https://github.com/joeizang/brilliantApp/issues/30)); how that sync works is in [sync.md](sync.md). It also stores the Content Packs the authoring tool publishes and serves them to the devices ([#31](https://github.com/joeizang/brilliantApp/issues/31); [content-distribution.md](content-distribution.md)).

## Prerequisites

- Docker with the Compose plugin (`docker compose version`)
- Nothing else: the image builds the server from source

## Deploy

From the repo root:

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD (and SERVER_PORT if 8443 is taken)
scripts/local-ca.sh 192.168.1.20      # the HTTPS certificate: see https.md; the server won't start without it
docker compose up -d --build
```

`.env` is git-ignored. Postgres data lives in the `pgdata` volume, so `docker compose down` keeps it and `docker compose down -v` deletes it.

Check it is up:

```bash
curl --cacert tls/brilliant-ca.crt https://<server-address>:8443/health
# {"status":"ok"}   (503 if the database is unreachable)
```

The server applies its database migrations on every start.

For app scoping (#145), the additive migration assigns every existing device,
pairing code, event, draft and pack to the DSA ID `com.joeizang.brilliantapp`.
Tokens, progress, draft code, pack bytes and sync cursor values are retained.
Back up before upgrading as usual. Downgrading this migration is refused while
any non-DSA rows exist, so app identities cannot silently be removed.

## App identity

The published server contains `apps/*/manifest.yaml` and loads app IDs through the
shared `AppManifest` parser. Unknown IDs are refused. New app manifests are picked
up by rebuilding/publishing the server; `Apps:Directory` (environment variable
`Apps__Directory`) can point at a directory of manifests instead. Duplicate IDs,
invalid manifests or an empty catalog fail startup.

Each app pairs separately, even on the same physical device. Pairing requests
include `appId`, and authenticated requests carry `X-Brilliant-App-Id` alongside
the bearer token. Authentication checks that header against the app saved on the
device record; endpoints use that authenticated identity for every read/write.
A missing header or missing pairing `appId` means DSA for existing installations.
A token for another app, an unknown ID or a missing header on a non-DSA token gets
`401`; an unknown pairing app gets `400` without consuming its code.

All device lists/revocations, codes, packs, event streams and draft updates are
confined to that app. The same event ID, step ID or pack ID/version can exist in
two apps independently. Sequence numbers stay global and monotonic; filtered
per-app pulls can have gaps, and cursors remain safe.


## Pair a device

A pairing code is 8 characters (shown as `XXXX-XXXX`), works once, and expires after 15 minutes.

**First DSA device.** When DSA is configured and has no paired devices, startup logs a DSA code. Other apps get their first code from the server command below:

```bash
docker compose logs server | grep "Pairing code"
```

**Any time.** Print a fresh one on the server:

```bash
docker compose exec server dotnet Brilliant.Server.dll pairing-code

# A separate app's first device (use its manifest appId, not its folder name):
docker compose exec server dotnet Brilliant.Server.dll pairing-code --app com.joeizang.brilliantapp.runtimecheck
```

**From an already paired device**, `POST /pairing-codes` with its token and app header returns a new code for the same app only.

Then, in the app: **Sync** in the sidebar, enter the server's address (for example `192.168.1.20:8443`; a bare host gets `https://` and port 8443; the device must trust the household CA first, see [https.md](https.md)), the code, and a name for the device, and press **Pair**. A wrong, used or expired code is refused, and after 10 wrong tries a minute from one address the server answers `429` until the minute is up.

The token is returned once and only a hash of it is stored on the server. On the device it is kept in the platform's secure storage (the keychain on a Mac, the keystore on Android). If that is unavailable, as in an unsigned Mac build (`MissingEntitlement`), it falls back to a file readable only by you (`0600`) inside the app's sandbox container. That file is authoritative whenever it holds anything, and it only changes once the replacement is committed: a pairing the keystore accepts empties it, one the keystore refuses is written into it, and a failed or cancelled save leaves it as it was (writes go to a temporary file that is moved into place). **Forget this server** always leaves a "forgotten" marker in it, because the platform can report a failed keychain removal as a plain `false` instead of an error, so an old token the keystore kept can never override a forget or a newer pairing. If the marker can't be written, the Sync screen says so instead of reporting success. Forgetting clears the credentials on the device; it does not revoke them (see below).

On narrow windows and phones, where the sidebar is hidden, Sync is reached from the link at the bottom of the Tracks screen.

## Manage devices

All of these need a device token (`Authorization: Bearer <token>`). For non-DSA apps also add `-H "X-Brilliant-App-Id: <appId>"` to every request:

```bash
# list devices (id, name, when paired, last seen, revoked)
curl -H "Authorization: Bearer $TOKEN" https://<server>:8443/devices

# revoke one: its token stops working immediately
curl -X DELETE -H "Authorization: Bearer $TOKEN" https://<server>:8443/devices/<id>
```

A revoked device gets `401` on everything except `/health` and `/pair`. Revoking is permanent; to bring a device back, pair it again (it then appears as a new device). A device may revoke itself. If you lose every device, `docker compose exec server dotnet Brilliant.Server.dll pairing-code` still works and starts you over.

## Endpoints

| Method and path | Auth | What |
|---|---|---|
| `GET /health` | none | `200 {"status":"ok"}`, or `503` if Postgres is down |
| `POST /pair` | code | `{code, deviceName, appId}` → `{deviceId, token, appId}` (missing appId defaults to DSA); `401` bad/used/expired code, `400` bad request, `429` too many tries |
| `POST /pairing-codes` | device | `{code, expiresAt}` for a new one-time code |
| `GET /devices` | device | the app's devices |
| `DELETE /devices/{id}` | device | revoke; `204` (also if already revoked), `404` if unknown |
| `POST /events` | device | `{events:[…]}` (at most 500) → `{accepted, duplicates, rejected:[{id, reason}]}`. Idempotent by event ID. Judged event by event: valid ones are stored and an invalid one is named in `rejected` (the response is still `200`), so one bad event never blocks the rest. Only a malformed batch (no list, a null entry, over 500 events) is a `400` that stores nothing |
| `POST /drafts` | device | `{drafts:[{stepId, code, updatedAt}]}` (at most 200) → `{accepted, ignored, rejected:[{id, reason}]}`. A draft replaces the server's copy only if it is newer (a tie goes to the larger code; `code: null` is a reset and is kept). Over-limit drafts are named in `rejected`; only a malformed batch is a `400` |
| `GET /drafts?after=N&limit=M` | device | drafts changed after cursor `N`, oldest change first, at most `M` (default and maximum 500) → `{drafts, cursor, hasMore}`. A draft that is replaced moves to the end of this order. See [sync.md](sync.md#code-drafts) |
| `GET /events?after=N&limit=M` | device | events after cursor `N` (default 0), oldest first, at most `M` (default and maximum 1000) → `{events, cursor, hasMore}`. Pass `cursor` back as `after` |
| `POST /packs` | device | a Content Pack (the zip as the body, at most 20 MB) → `{pack, created, removedIds}`. Validated by loading it; idempotent for identical bytes; `409` for the same version with other contents or a lower version than the newest; `400` with a reason for an invalid pack. See [content-distribution.md](content-distribution.md) |
| `GET /packs` | device | `{packs:[{packId, version, formatVersion, sha256, size, publishedAt}]}`, the newest version of each pack |
| `GET /packs/{packId}/{version}` | device | the pack's zip (`ETag` is its SHA-256); `404` if unknown |
| `POST /feedback/grade` | device | a `GradeRequest` (`{title, option, answer, modelAnswer, rubric, redFlags}`, each text at most 8000 characters) → advisory `AnswerFeedback` from the Ollama the server is configured with, using the `graderModel` in the calling app's manifest. `404` for an app with no grader, `400` for a bad request, `503 {failure, error}` when Ollama is unset, unreachable, too slow or lacks the model (the error never names its address), `502` when its reply is unusable. See [csharp-answer-feedback.md](csharp-answer-feedback.md#phone-feedback-through-the-server-opt-in) |

## HTTPS on the home network

The server speaks **HTTPS only**, with a certificate signed by the household's own certificate authority ([https.md](https.md), [#35](https://github.com/joeizang/brilliantApp/issues/35)), so the pairing code and device tokens never cross the Wi-Fi in the clear. Each device must trust that authority once (you install `tls/brilliant-ca.crt` on it). The apps allow no cleartext exceptions: Android's `network_security_config.xml` forbids cleartext, and the Mac app has no App Transport Security exceptions. It is still a home server: do not forward its port to the internet.

Devices paired before #35 remember `http://…:8080` and must pair again ([https.md](https.md#pair-over-https)).

## Configuration

| Setting | Where | Default |
|---|---|---|
| `POSTGRES_PASSWORD` | `.env` | required |
| `SERVER_PORT` | `.env` | `8443` |
| `TLS_DIR` | `.env` | `./tls` (holds `server.pem`, `server.key`; see [https.md](https.md)) |
| `SERVER_UID`, `SERVER_GID` | `.env` (set by `scripts/local-ca.sh`) | `1000`; the user the server runs as, which must own `tls/server.key` |
| `Tls__CertificatePath`, `Tls__KeyPath`, `Tls__Port` | set in `docker-compose.yml` | `/tls/server.pem`, `/tls/server.key`, `8443` |
| `ConnectionStrings__Postgres` | set in `docker-compose.yml` | the compose Postgres |
| `BACKUP_DIR`, `BACKUP_KEEP`, `BACKUP_AT`, `BACKUP_TZ`, `BACKUP_UID`, `BACKUP_GID` | `.env` | `./backups`, `14`, `03:00`, `UTC`, root; see [backup.md](backup.md) |
| `Pairing__CodeLifetimeMinutes` | environment | `15` |
| `Pairing__AttemptsPerMinute` | environment | `10` |
| `OLLAMA_URL` (sets `Feedback__OllamaUrl`) | `.env` | empty: the feedback proxy answers "unavailable". The Mac's Ollama, for example `http://192.168.1.50:11434`; see [csharp-answer-feedback.md](csharp-answer-feedback.md#the-one-time-setting-on-the-mac) |
| `Feedback__ConnectTimeoutSeconds`, `Feedback__TimeoutSeconds` | environment | `3`, `120`: how long to wait to reach Ollama, and for its whole answer |

## Tests

The server's integration tests run against a real Postgres started by Testcontainers (one database per test), so Docker must be running:

```bash
dotnet test tests/Brilliant.Server.Tests
```

`TwoDeviceConvergenceTests` there runs two real SQLite logs through the real server and checks that both end with identical learner state. The client side (address parsing, the pairing and sync clients, `SyncService`, credential stores) is covered in `tests/Brilliant.Core.Tests`, and the Sync screen and screen refresh in `tests/Brilliant.Lessons.UI.Tests`.

## Layout

| Path | Purpose |
|---|---|
| `src/Brilliant.Server` | The API: endpoints, device-token authentication, pairing codes, the event log, Content Packs, EF Core migrations |
| `src/Brilliant.Core/Sync` | Contracts shared with the apps, the pairing and sync clients, `SyncService`, credential stores |
| `docker-compose.yml`, `src/Brilliant.Server/Dockerfile` | Postgres, the server and the nightly backup ([backup.md](backup.md)) |
| `tests/Brilliant.Server.Tests` | Integration tests against real Postgres |
