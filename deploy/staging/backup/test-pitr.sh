#!/usr/bin/env bash
# Point-in-time recovery drill (NF-06): proves the WAL archiving chain used on staging end to end, on a throwaway cluster.
#   archive_command (postgres/wal-archive.sh) -> backup.sh ship-wal (age) -> backup.sh basebackup (age)
#   -> restore: decrypt base backup, restore_command decrypts WAL, recovery stops at a named restore point.
# Rows written after the restore point must be absent from the recovered cluster.
#
# Run as a NON-root user (PostgreSQL refuses to run as root). PG_BIN defaults to the Debian/Ubuntu location of PostgreSQL 16.
# Requirements: initdb, pg_ctl, pg_basebackup, psql, age, age-keygen, tar, gzip.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=common.sh
source "$here/common.sh"

[[ $(id -u) -ne 0 ]] || die "run as a non-root user (e.g. su postgres -c $0)"
PG_BIN=${PG_BIN:-$(dirname "$(command -v pg_ctl || echo /usr/lib/postgresql/16/bin/pg_ctl)")}
export PATH="$PG_BIN:$PATH"
port_source=${PITR_SOURCE_PORT:-54391}
port_restored=${PITR_RESTORED_PORT:-54392}

work=$(mktemp -d)
pg_ctl_stop() { pg_ctl -D "$1" -m immediate stop >/dev/null 2>&1 || true; }
trap 'pg_ctl_stop "$work/data"; pg_ctl_stop "$work/restored"; rm -rf "$work"' EXIT

age-keygen -o "$work/identity.key" 2>/dev/null
export AGE_IDENTITY_FILE="$work/identity.key"
BACKUP_AGE_RECIPIENT=$(age-keygen -y "$AGE_IDENTITY_FILE")
export BACKUP_AGE_RECIPIENT
export BACKUP_DIR="$work/backups" WAL_INCOMING_DIR="$work/incoming"
mkdir -p "$WAL_INCOMING_DIR" "$BACKUP_DIR" "$work/sock"

log "initialising a source cluster with WAL archiving"
initdb -D "$work/data" -U drill --auth=trust >/dev/null
cat >> "$work/data/postgresql.conf" <<EOF
port = $port_source
listen_addresses = ''
unix_socket_directories = '$work/sock'
wal_level = replica
archive_mode = on
archive_timeout = 60
max_wal_senders = 4
archive_command = 'WAL_INCOMING_DIR=$WAL_INCOMING_DIR /bin/sh $here/../postgres/wal-archive.sh %p %f'
EOF
pg_ctl -D "$work/data" -l "$work/source.log" -w start >/dev/null

export PGHOST="$work/sock" PGPORT=$port_source PGUSER=drill PGDATABASE=postgres
psql -q -c "create table drill (id int primary key, phase text)"
psql -q -c "insert into drill select n, 'before-backup' from generate_series(1, 500) n"

"$here/backup.sh" basebackup

psql -q -c "insert into drill select n, 'after-backup-before-restore-point' from generate_series(501, 1500) n"
psql -q -c "select pg_create_restore_point('drill')" >/dev/null
psql -q -c "insert into drill select n, 'after-restore-point' from generate_series(1501, 2000) n"
last_segment=$(psql -Atc "select pg_walfile_name(pg_current_wal_lsn())")
psql -q -c "select pg_switch_wal()" >/dev/null

# archive_command runs asynchronously: wait for the segment holding the restore point to be handed over.
for _ in $(seq 1 60); do [[ -e "$WAL_INCOMING_DIR/$last_segment" ]] && break; sleep 1; done
[[ -e "$WAL_INCOMING_DIR/$last_segment" ]] || die "WAL segment $last_segment was not archived within 60 s"
"$here/backup.sh" ship-wal
log "encrypted WAL segments: $(ls "$BACKUP_DIR/wal" | wc -l); plain segments left in the hand-over directory: $(ls "$WAL_INCOMING_DIR" | wc -l)"
[[ $(ls "$BACKUP_DIR/wal" | wc -l) -ge 1 ]] || die "no WAL segment was archived"
pg_ctl_stop "$work/data"

log "restoring: base backup + WAL replay up to the restore point"
mkdir -m 700 "$work/restored"
base=$(ls "$BACKUP_DIR"/basebackups/*.tar.gz.age | head -n 1)
age --decrypt --identity "$AGE_IDENTITY_FILE" "$base" | gunzip | tar -x -C "$work/restored"
touch "$work/restored/recovery.signal"
cat >> "$work/restored/postgresql.conf" <<EOF
port = $port_restored
archive_mode = off
restore_command = 'age --decrypt --identity $AGE_IDENTITY_FILE --output "%p" $BACKUP_DIR/wal/%f.age'
recovery_target_name = 'drill'
recovery_target_action = 'promote'
EOF
pg_ctl -D "$work/restored" -l "$work/restored.log" -w -t 60 start >/dev/null || { tail -n 25 "$work/restored.log" >&2; die "the restored cluster did not start"; }

export PGPORT=$port_restored
total=$(psql -Atc "select count(*) from drill")
phases=$(psql -Atc "select string_agg(distinct phase, ',' order by phase) from drill")
log "recovered rows: $total (phases: $phases)"
[[ "$total" == "1500" ]] || die "expected 1500 rows at the restore point, got $total"
[[ "$phases" != *after-restore-point* ]] || die "rows written after the restore point are present"
log "RESULT: PITR drill PASSED"
