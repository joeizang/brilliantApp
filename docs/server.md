# The household server

A small ASP.NET Core API and PostgreSQL, run with Docker Compose on one machine at home (the Ubuntu laptop). Devices pair with it once using a short code, and from then on carry a long-lived device token. It began as the tracer from [#28](https://github.com/joeizang/brilliantApp/issues/28) (pairing and device management) and now also holds the household's progress event log ([#29](https://github.com/joeizang/brilliantApp/issues/29); how that sync works is in [sync.md](sync.md)). Content distribution ([#31](https://github.com/joeizang/brilliantApp/issues/31)) builds on it next.

## Prerequisites

- Docker with the Compose plugin (`docker compose version`)
- Nothing else: the image builds the server from source

## Deploy

From the repo root:

```bash
cp .env.example .env
# edit .env: set POSTGRES_PASSWORD (and SERVER_PORT if 8080 is taken)
docker compose up -d --build
```

`.env` is git-ignored. Postgres data lives in the `pgdata` volume, so `docker compose down` keeps it and `docker compose down -v` deletes it.

Check it is up:

```bash
curl http://<server-address>:8080/health
# {"status":"ok"}   (503 if the database is unreachable)
```

The server applies its database migrations on every start.

## Pair a device

A pairing code is 8 characters (shown as `XXXX-XXXX`), works once, and expires after 15 minutes.

**First device.** When the server starts with no paired devices it logs a code:

```bash
docker compose logs server | grep "Pairing code"
```

**Any time.** Print a fresh one on the server:

```bash
docker compose exec server dotnet Brilliant.Server.dll pairing-code
```

**From an already paired device**, `POST /pairing-codes` with its token returns a new code.

Then, in the app: **Sync** in the sidebar, enter the server's address (for example `192.168.1.20:8080`; a bare host gets `http://` and port 8080), the code, and a name for the device, and press **Pair**. A wrong, used or expired code is refused, and after 10 wrong tries a minute from one address the server answers `429` until the minute is up.

The token is returned once and only a hash of it is stored on the server. On the device it is kept in the platform's secure storage (the keychain on a Mac, the keystore on Android). If that is unavailable, as in an unsigned Mac build (`MissingEntitlement`), it falls back to a file readable only by you (`0600`) inside the app's sandbox container. That file is authoritative whenever it holds anything, and it only changes once the replacement is committed: a pairing the keystore accepts empties it, one the keystore refuses is written into it, and a failed or cancelled save leaves it as it was (writes go to a temporary file that is moved into place). **Forget this server** always leaves a "forgotten" marker in it, because the platform can report a failed keychain removal as a plain `false` instead of an error, so an old token the keystore kept can never override a forget or a newer pairing. If the marker can't be written, the Sync screen says so instead of reporting success. Forgetting clears the credentials on the device; it does not revoke them (see below).

On narrow windows and phones, where the sidebar is hidden, Sync is reached from the link at the bottom of the Tracks screen.

## Manage devices

All of these need a device token (`Authorization: Bearer <token>`):

```bash
# list devices (id, name, when paired, last seen, revoked)
curl -H "Authorization: Bearer $TOKEN" http://<server>:8080/devices

# revoke one: its token stops working immediately
curl -X DELETE -H "Authorization: Bearer $TOKEN" http://<server>:8080/devices/<id>
```

A revoked device gets `401` on everything except `/health` and `/pair`. Revoking is permanent; to bring a device back, pair it again (it then appears as a new device). A device may revoke itself. If you lose every device, `docker compose exec server dotnet Brilliant.Server.dll pairing-code` still works and starts you over.

## Endpoints

| Method and path | Auth | What |
|---|---|---|
| `GET /health` | none | `200 {"status":"ok"}`, or `503` if Postgres is down |
| `POST /pair` | code | `{code, deviceName}` → `{deviceId, token}`; `401` bad/used/expired code, `400` bad request, `429` too many tries |
| `POST /pairing-codes` | device | `{code, expiresAt}` for a new one-time code |
| `GET /devices` | device | the household's devices |
| `DELETE /devices/{id}` | device | revoke; `204` (also if already revoked), `404` if unknown |
| `POST /events` | device | `{events:[…]}` (at most 500) → `{accepted, duplicates, rejected:[{id, reason}]}`. Idempotent by event ID. Judged event by event: valid ones are stored and an invalid one is named in `rejected` (the response is still `200`), so one bad event never blocks the rest. Only a malformed batch (no list, a null entry, over 500 events) is a `400` that stores nothing |
| `GET /events?after=N&limit=M` | device | events after cursor `N` (default 0), oldest first, at most `M` (default and maximum 1000) → `{events, cursor, hasMore}`. Pass `cursor` back as `after` |

## Plain HTTP on the home network

The server speaks plain HTTP, and the pairing code and token cross your LAN unencrypted. That is a deliberate tracer-stage choice for a trusted home network: do not expose the port to the internet. Local-CA HTTPS is tracked in [#35](https://github.com/joeizang/brilliantApp/issues/35).

The apps allow it narrowly:

- **Mac:** `NSAllowsLocalNetworking` in `Platforms/MacCatalyst/Info.plist` permits HTTP to IP addresses and `.local` names only.
- **Android** ([android.md](android.md)): `android:usesCleartextTraffic="true"` in `AndroidManifest.xml` permits cleartext to any host. Android cannot restrict it to private ranges, so it is lifted when #35 lands.

## Configuration

| Setting | Where | Default |
|---|---|---|
| `POSTGRES_PASSWORD` | `.env` | required |
| `SERVER_PORT` | `.env` | `8080` |
| `ConnectionStrings__Postgres` | set in `docker-compose.yml` | the compose Postgres |
| `Pairing__CodeLifetimeMinutes` | environment | `15` |
| `Pairing__AttemptsPerMinute` | environment | `10` |

## Tests

The server's integration tests run against a real Postgres started by Testcontainers (one database per test), so Docker must be running:

```bash
dotnet test tests/Brilliant.Server.Tests
```

`TwoDeviceConvergenceTests` there runs two real SQLite logs through the real server and checks that both end with identical learner state. The client side (address parsing, the pairing and sync clients, `SyncService`, credential stores) is covered in `tests/Brilliant.Core.Tests`, and the Sync screen and screen refresh in `tests/Brilliant.Lessons.UI.Tests`.

## Layout

| Path | Purpose |
|---|---|
| `src/Brilliant.Server` | The API: endpoints, device-token authentication, pairing codes, the event log, EF Core migrations |
| `src/Brilliant.Core/Sync` | Contracts shared with the apps, the pairing and sync clients, `SyncService`, credential stores |
| `docker-compose.yml`, `src/Brilliant.Server/Dockerfile` | Postgres and the server |
| `tests/Brilliant.Server.Tests` | Integration tests against real Postgres |
