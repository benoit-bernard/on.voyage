#!/usr/bin/env bash
# Backups of the ON.VOYAGE PostgreSQL database (NF-06). Every artefact is encrypted with age before it touches the backup volume.
#
#   backup.sh dump        logical backup (pg_dump custom format) -> $BACKUP_DIR/dumps/<db>_<UTC>.dump.age        (daily)
#   backup.sh basebackup  physical base backup (pg_basebackup)   -> $BACKUP_DIR/basebackups/<UTC>.tar.gz.age   (weekly, base of WAL replay)
#   backup.sh ship-wal    encrypt WAL segments handed over by PostgreSQL -> $BACKUP_DIR/wal/<segment>.age
#   backup.sh prune       delete artefacts older than $BACKUP_RETENTION_DAYS (default 30)
#
# Environment: PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE, BACKUP_AGE_RECIPIENT (public key), optional BACKUP_DIR, WAL_INCOMING_DIR.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=common.sh
source "$here/common.sh"

dump() {
  require_recipient
  : "${PGDATABASE:?PGDATABASE is required}"
  mkdir -p "$BACKUP_DIR/dumps"
  local out
  out="$BACKUP_DIR/dumps/${PGDATABASE}_$(timestamp).dump.age"
  log "pg_dump $PGDATABASE -> $out"
  # --no-owner/--no-acl: the dump restores under any role. The plain dump never exists on disk (pipe straight into age).
  pg_dump --format=custom --no-owner --no-acl --dbname="$PGDATABASE" | age --recipient "$BACKUP_AGE_RECIPIENT" --output "$out.partial"
  mv "$out.partial" "$out"
  (cd "$(dirname "$out")" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256")
  log "dump done ($(du -h "$out" | cut -f1))"
}

basebackup() {
  require_recipient
  mkdir -p "$BACKUP_DIR/basebackups"
  local out
  out="$BACKUP_DIR/basebackups/$(timestamp).tar.gz.age"
  log "pg_basebackup -> $out"
  # -X fetch: the WAL needed to make the copy consistent is inside the archive. Output is a tar of the data directory (no tablespaces).
  pg_basebackup --format=tar --checkpoint=fast --wal-method=fetch --pgdata=- | gzip -6 | age --recipient "$BACKUP_AGE_RECIPIENT" --output "$out.partial"
  mv "$out.partial" "$out"
  (cd "$(dirname "$out")" && sha256sum "$(basename "$out")" > "$(basename "$out").sha256")
  log "base backup done ($(du -h "$out" | cut -f1))"
}

ship_wal() {
  require_recipient
  mkdir -p "$BACKUP_DIR/wal"
  local shipped=0 file name
  shopt -s nullglob
  for file in "$WAL_INCOMING_DIR"/*; do
    name=$(basename "$file")
    [[ "$name" == .* || "$name" == *.tmp ]] && continue
    age --recipient "$BACKUP_AGE_RECIPIENT" --output "$BACKUP_DIR/wal/$name.age.partial" "$file"
    mv "$BACKUP_DIR/wal/$name.age.partial" "$BACKUP_DIR/wal/$name.age"
    rm -f "$file" # PostgreSQL got a success from archive_command long ago; the encrypted copy is on disk now
    shipped=$((shipped + 1))
  done
  [[ $shipped -eq 0 ]] || log "shipped $shipped WAL segment(s)"
}

prune() {
  log "pruning artefacts older than $BACKUP_RETENTION_DAYS days"
  for sub in dumps basebackups wal; do
    [[ -d "$BACKUP_DIR/$sub" ]] || continue
    find "$BACKUP_DIR/$sub" -type f -mtime "+$BACKUP_RETENTION_DAYS" -print -delete | sed 's/^/  deleted /' >&2
  done
}

case "${1:-}" in
  dump) dump ;;
  basebackup) basebackup ;;
  ship-wal) ship_wal ;;
  prune) prune ;;
  *) die "usage: backup.sh dump|basebackup|ship-wal|prune" ;;
esac
