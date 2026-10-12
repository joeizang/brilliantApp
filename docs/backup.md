# Backup and restore

Issue [#32](https://github.com/joeizang/brilliantApp/issues/32), PRD story 58. The `backup` service in `docker-compose.yml` dumps the household database every night, so a dead disk or a bad upgrade doesn't lose the learning history. It starts with the rest of the stack (`docker compose up -d`).

## What it does

- Runs `pg_dump --format=custom` against the `postgres` service at **03:00** (UTC unless you set `BACKUP_TZ`) and writes `brilliant-YYYYMMDD-HHMMSS.dump` into `./backups/` on the host.
- Keeps the newest **14** dumps and deletes older ones. Pruning happens only after a new dump has been written and checked, so a failing night never costs you the backups you have.
- Each dump is written under a temporary name, checked with `pg_restore --list`, and only then renamed. A crash or a full disk never leaves a file that looks like a backup but isn't.
- When the service starts it takes a dump straight away if there is none from the last 20 hours (so the first night after a deploy isn't unprotected), and otherwise just waits for 03:00. Restarting it in a loop doesn't fill the folder.
- A failed dump is logged (`docker compose logs backup`) and the service stays up for the next night. `docker compose ps` shows the service **unhealthy** when there is no dump from the last 36 hours.
- Dump files are mode 600.

Only the database is backed up. The apps' own progress lives on each device and flows to the server through sync ([sync.md](sync.md)), so every device is also a copy of the learner's data.

## Settings

Put these in `.env` next to `docker-compose.yml` (all optional; see `.env.example`).

| Setting | Default | Meaning |
|---|---|---|
| `BACKUP_DIR` | `./backups` | folder on the host that receives the dumps; create it first if you point it elsewhere |
| `BACKUP_KEEP` | `14` | how many dumps to keep (a whole number, 1 or more) |
| `BACKUP_AT` | `03:00` | time of the nightly dump, `HH:MM` |
| `BACKUP_TZ` | `UTC` | time zone for `BACKUP_AT`, for example `Africa/Lagos` |
| `BACKUP_UID`, `BACKUP_GID` | root | run as this user so the files on the host are yours: use `id -u` and `id -g`. The folder must be writable by that user |

A bad `BACKUP_KEEP` or `BACKUP_AT` stops the service with a message rather than guessing.

**A backup on the same disk as the database doesn't survive that disk.** Point `BACKUP_DIR` at another drive, or copy the folder elsewhere now and then (a USB drive, another machine, `rsync`).

## Back up right now

```bash
docker compose run --rm backup once
```

Do this before upgrading the server or changing its data.

## Restore

This replaces the database's contents with the dump's. Do it with the server stopped, so nothing writes while it happens.

```bash
# 1. pick a dump (the newest is last)
ls backups/

# 2. stop the server (Postgres stays up)
docker compose stop server

# 3. restore it, replacing what is there
docker compose exec -T postgres pg_restore -U brilliant -d brilliant --clean --if-exists --no-owner < backups/brilliant-20261012-030000.dump

# 4. start the server again
docker compose start server
```

`--clean --if-exists` drops each object before recreating it, so the restore also works on a database that already has data (and on an empty one). Errors about objects that "do not exist" don't occur with `--if-exists`; anything else printed is worth reading. Check it with `curl http://localhost:8080/health` and `docker compose exec -T postgres psql -U brilliant -d brilliant -c 'select count(*) from "Events"'`.

On a **new machine** (after a lost disk), bring the stack up first so Postgres and the schema exist (`docker compose up -d`), then restore as above.

### Then re-pair the devices

If the dump is older than what the devices have done since, the devices are ahead of the server and would skip events the server hasn't seen. Fix this on **every device**: **Tracks → Sync → Forget this server**, then pair again with a new code (`docker compose exec -T server dotnet Brilliant.Server.dll pairing-code`). Pairing again makes the device send its whole log again (the server ignores what it already has), so progress made after the backup comes back from the devices, and draft code is sent again too. Devices paired after the dump was taken are unknown to the restored server and need this in any case.

## Tested

`scripts/backup/test-backup.sh` runs the whole thing against a real Postgres in a throwaway Compose project (separate containers, volume and backup folder, so a running stack is untouched). It checks the timestamped name, retention, that a failed dump (wrong password) leaves no partial file and keeps the old dumps, a restore after data was deleted, that the service waits for the night rather than dumping again at start, and that bad settings are refused. It needs Docker and takes about 30 seconds.

## Where the code is

| Path | Purpose |
|---|---|
| `scripts/backup/backup.sh` | the dump, check, rename and prune loop (mounted into the service) |
| `scripts/backup/test-backup.sh` | the end-to-end test |
| `docker-compose.yml` | the `backup` service |
