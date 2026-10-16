#!/bin/bash
# End-to-end check of the backup service against a real Postgres, in a throwaway Compose project (own containers, own volume,
# a temp backup folder), so a running household stack is never touched. Needs Docker. Usage: scripts/backup/test-backup.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

export COMPOSE_PROJECT_NAME="brilliant-backup-test-$$"
export POSTGRES_PASSWORD="test-only"
export BACKUP_DIR; BACKUP_DIR="$(mktemp -d)"
export BACKUP_KEEP=2
dc() { docker compose "$@"; }
psql_() { dc exec -T postgres psql -h 127.0.0.1 -U brilliant -d brilliant -v ON_ERROR_STOP=1 -tA "$@"; }
fail() { echo "FAIL: $*" >&2; exit 1; }
cleanup() { dc down -v --remove-orphans >/dev/null 2>&1 || true; rm -rf "$BACKUP_DIR"; }
trap cleanup EXIT

echo "== start a database with some data"
dc up -d --wait postgres >/dev/null
# Image initialization briefly starts a Unix-socket-only server before the real TCP listener.
# The Compose health check can observe that temporary server; wait for the final listener.
ready=false
for attempt in {1..30}; do
    if psql_ -c 'select 1' >/dev/null 2>&1; then ready=true; break; fi
    sleep 1
done
[ "$ready" = true ] || fail "Postgres did not become ready on TCP"
psql_ -c "create table lesson_notes (id int primary key, note text); insert into lesson_notes values (1,'one'),(2,'two'),(3,'three');" >/dev/null
# Same row ID in two app scopes: the full-database dump must preserve both namespaces.
psql_ -c "create table app_scoped_notes (app_id text, id int, note text, primary key(app_id,id)); insert into app_scoped_notes values ('com.joeizang.brilliantapp',1,'dsa-progress'),('com.joeizang.brilliantapp.runtimecheck',1,'other-progress');" >/dev/null

echo "== three dumps with BACKUP_KEEP=2 keep the newest two, timestamp-named"
for _ in 1 2 3; do dc run --rm -T backup once >/dev/null; sleep 1.2; done
count=$(find "$BACKUP_DIR" -name 'brilliant-*.dump' | wc -l | tr -d ' ')
[ "$count" = 2 ] || fail "expected 2 dumps, found $count"
ls "$BACKUP_DIR" | grep -Eq '^brilliant-[0-9]{8}-[0-9]{6}\.dump$' || fail "dump name is not brilliant-YYYYMMDD-HHMMSS.dump"
[ -z "$(find "$BACKUP_DIR" -name '*.tmp')" ] || fail "a .tmp file was left behind"
echo "   kept: $(ls "$BACKUP_DIR" | tr '\n' ' ')"

echo "== a failing dump is reported, leaves no partial file and keeps the old dumps"
before=$(ls "$BACKUP_DIR")
if dc run --rm -T -e PGPASSWORD=wrong backup once >/dev/null 2>&1; then fail "a dump with the wrong password should fail"; fi
[ "$(ls -A "$BACKUP_DIR")" = "$before" ] || fail "the failed dump changed the backup folder"

echo "== restore brings back deleted data"
newest="$BACKUP_DIR/$(ls "$BACKUP_DIR" | sort | tail -n 1)"
psql_ -c "delete from lesson_notes; insert into lesson_notes values (9,'later');" >/dev/null
dc exec -T postgres pg_restore -U brilliant -d brilliant --clean --if-exists --no-owner < "$newest"
got=$(psql_ -c "select string_agg(note, ',' order by id) from lesson_notes")
[ "$got" = "one,two,three" ] || fail "after restore expected one,two,three, got '$got'"
got_apps=$(psql_ -c "select string_agg(app_id || ':' || note, ',' order by app_id) from app_scoped_notes;")
[ "$got_apps" = "com.joeizang.brilliantapp:dsa-progress,com.joeizang.brilliantapp.runtimecheck:other-progress" ] || fail "restore lost app-scoped data: '$got_apps'"

echo "== the service starts, sees a recent dump, and waits for tonight"
dc up -d backup >/dev/null
sleep 4
logs=$(dc logs backup)
echo "$logs" | grep -q "next dump in" || fail "service did not schedule the next dump: $logs"
echo "$logs" | grep -q "taking one now" && fail "service dumped at start although a recent dump exists"
[ "$(dc ps --format '{{.Service}}' | grep -c backup)" = 1 ] || fail "backup service is not running"

echo "== a bad setting is refused"
if dc run --rm -T -e BACKUP_KEEP=0 backup once >/dev/null 2>&1; then fail "BACKUP_KEEP=0 should be refused"; fi
if dc run --rm -T -e BACKUP_AT=25:00 backup once >/dev/null 2>&1; then fail "BACKUP_AT=25:00 should be refused"; fi

echo "== zero in any spelling is refused before anything is touched; leading zeros are not octal"
before=$(ls "$BACKUP_DIR")
for zero in 0 00 000; do
    if dc run --rm -T -e BACKUP_KEEP=$zero backup once >/dev/null 2>&1; then fail "BACKUP_KEEP=$zero should be refused"; fi
    [ "$(ls -A "$BACKUP_DIR")" = "$before" ] || fail "BACKUP_KEEP=$zero changed the backup folder"
done
dc run --rm -T -e BACKUP_KEEP=08 backup once >/dev/null 2>&1 || fail "BACKUP_KEEP=08 should be accepted as 8"
out=$(dc run --rm -T -e BACKUP_STALE_HOURS=08 -e BACKUP_AT=08:09 --entrypoint sh backup -c 'timeout 3 /backup.sh' 2>&1 || true)
echo "$out" | grep -q "backup: starting" || fail "08 as a number of hours or minutes should not break the service: $out"

echo "== if publishing the new dump fails, nothing old is pruned (the nightly loop's path)"
sleep 1.2
for n in 20260101-000001 20260101-000002 20260101-000003; do cp "$newest" "$BACKUP_DIR/brilliant-$n.dump"; done
find "$BACKUP_DIR" -name 'brilliant-*.dump' ! -name 'brilliant-20260101-*' -delete
dc run --rm -T -e BACKUP_KEEP=1 -e BACKUP_STALE_HOURS=0 --entrypoint sh backup -c \
    'mkdir /shim; printf "#!/bin/sh\nexit 1\n" > /shim/mv; chmod +x /shim/mv; PATH=/shim:$PATH exec timeout 5 /backup.sh' >/dev/null 2>&1 || true
[ "$(ls "$BACKUP_DIR" | grep -c '^brilliant-.*\.dump$')" = 3 ] || fail "old dumps were pruned although the new one was not published: $(ls -A "$BACKUP_DIR" | tr '\n' ' ')"
[ -z "$(find "$BACKUP_DIR" -name '*.tmp')" ] || fail "the unpublished dump's .tmp file was left behind"

echo "PASS"
