#!/usr/bin/env bash
# Entrypoint of the backup service: a one-minute loop.
#  - every tick: ship WAL segments, refresh the heartbeat read by the container health check;
#  - once a day at BACKUP_HOUR_UTC (default 02): logical dump, then prune;
#  - once a week (BACKUP_BASEBACKUP_WEEKDAY, 1 = Monday ... 7 = Sunday, default 7): physical base backup.
# State lives in $BACKUP_DIR/state so that a restart neither skips nor repeats a day.
set -uo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=common.sh
source "$here/common.sh"

require_recipient
hour=${BACKUP_HOUR_UTC:-2}
weekday=${BACKUP_BASEBACKUP_WEEKDAY:-7}
state="$BACKUP_DIR/state"
mkdir -p "$state"

run() { "$here/backup.sh" "$1" || log "WARNING: backup.sh $1 failed (will be retried at the next opportunity)"; }

log "backup service started (daily dump at ${hour}:00 UTC, base backup on weekday $weekday, retention ${BACKUP_RETENTION_DAYS} days)"
until pg_isready -q; do sleep 5; done

while true; do
  today=$(date -u +%F)
  if (( 10#$(date -u +%H) >= 10#$hour )) && [[ "$(cat "$state/last-dump" 2>/dev/null)" != "$today" ]]; then
    if "$here/backup.sh" dump; then echo "$today" > "$state/last-dump"; run prune; else log "WARNING: dump failed, retrying next minute"; fi
  fi
  if [[ "$(date -u +%u)" == "$weekday" && "$(cat "$state/last-basebackup" 2>/dev/null)" != "$today" ]] && (( 10#$(date -u +%H) >= 10#$hour )); then
    if "$here/backup.sh" basebackup; then echo "$today" > "$state/last-basebackup"; else log "WARNING: base backup failed, retrying next minute"; fi
  fi
  run ship-wal
  touch "$state/heartbeat"
  sleep 60
done
