#!/bin/sh
# Nightly pg_dump of the household database, run by the `backup` service in docker-compose.yml (see docs/backup.md).
#
#   backup.sh          run for ever: dump every night at BACKUP_AT, and once at start if the newest dump is stale
#   backup.sh once     dump now and exit (use it by hand: docker compose run --rm backup /backup.sh once)
#
# Settings (environment): BACKUP_AT (HH:MM, default 03:00, in the container's TZ), BACKUP_KEEP (dumps to keep, default 14),
# BACKUP_DIR (default /backups), BACKUP_STALE_HOURS (default 20), and the usual PGHOST, PGUSER, PGDATABASE, PGPASSWORD.
set -eu

DIR="${BACKUP_DIR:-/backups}"
KEEP="${BACKUP_KEEP:-14}"
AT="${BACKUP_AT:-03:00}"
STALE_HOURS="${BACKUP_STALE_HOURS:-20}"
umask 077

log() { echo "$(date '+%Y-%m-%d %H:%M:%S') backup: $*"; }

case "$KEEP" in ''|*[!0-9]*|0) log "BACKUP_KEEP must be a whole number of at least 1 (got '$KEEP')"; exit 2 ;; esac
case "$AT" in [0-2][0-9]:[0-5][0-9]) ;; *) log "BACKUP_AT must look like 03:00 (got '$AT')"; exit 2 ;; esac
AT_H="${AT%%:*}"; AT_M="${AT##*:}"
if [ "$((${AT_H#0} + 0))" -gt 23 ]; then log "BACKUP_AT hour must be 00 to 23 (got '$AT')"; exit 2; fi

# One dump. Written under a temporary name and checked before it takes its real one, so a crash or a full disk never leaves
# something that looks like a backup but isn't. Old dumps are pruned only after a good one exists.
dump() {
    name="brilliant-$(date '+%Y%m%d-%H%M%S').dump"
    tmp="$DIR/.$name.tmp"
    if pg_dump --format=custom --no-owner --file="$tmp" && pg_restore --list "$tmp" >/dev/null; then
        mv "$tmp" "$DIR/$name"
        log "wrote $name ($(wc -c < "$DIR/$name") bytes)"
        prune
    else
        rm -f "$tmp"
        log "FAILED to dump; earlier backups are untouched"
        return 1
    fi
}

# Keeps the newest $KEEP dumps (the names sort by time).
prune() {
    ls -1 "$DIR" | grep '^brilliant-.*\.dump$' | sort -r | tail -n "+$((KEEP + 1))" | while read -r old; do
        rm -f "$DIR/$old"
        log "removed $old (keeping the last $KEEP)"
    done
}

# Seconds from now until the next BACKUP_AT.
seconds_until_next() {
    h="$(date +%H)"; m="$(date +%M)"; s="$(date +%S)"
    now=$(( (${h#0} + 0) * 3600 + (${m#0} + 0) * 60 + (${s#0} + 0) ))
    target=$(( (${AT_H#0} + 0) * 3600 + (${AT_M#0} + 0) * 60 ))
    wait=$((target - now))
    [ "$wait" -le 0 ] && wait=$((wait + 86400))
    echo "$wait"
}

mkdir -p "$DIR"
rm -f "$DIR"/.brilliant-*.dump.tmp   # left by a dump that was interrupted

if [ "${1:-}" = "once" ]; then dump; exit $?; fi

log "starting: nightly at $AT ($(date +%Z)), keeping the last $KEEP, into $DIR"

# The first night after a deploy shouldn't be unprotected, but a restart loop mustn't fill the folder: dump at start only
# when there is no dump from the last $STALE_HOURS hours.
if [ -z "$(find "$DIR" -name 'brilliant-*.dump' -mmin "-$((STALE_HOURS * 60))" 2>/dev/null)" ]; then
    log "no recent dump; taking one now"
    dump || true
fi

while true; do
    wait="$(seconds_until_next)"
    log "next dump in $((wait / 3600))h $((wait % 3600 / 60))m"
    sleep "$wait"
    dump || true   # a failed night is logged and shows as unhealthy; the next night tries again
done
