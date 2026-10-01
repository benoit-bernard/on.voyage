#!/bin/sh
# PostgreSQL archive_command: `sh wal-archive.sh %p %f`.
# Copies a finished WAL segment to the hand-over volume. The backup service encrypts it with age and moves it to /backups/wal.
# The copy is atomic (temp file, then rename) so the shipper never sees a partial segment, and it is idempotent: a segment already
# handed over is a success (PostgreSQL retries after a crash). A non-zero exit makes PostgreSQL keep the segment and retry.
set -eu

source_path=$1
name=$2
target_dir=${WAL_INCOMING_DIR:-/wal-incoming}

[ -f "$target_dir/$name" ] && exit 0
cp "$source_path" "$target_dir/.$name.tmp"
sync "$target_dir/.$name.tmp" 2>/dev/null || true
mv "$target_dir/.$name.tmp" "$target_dir/$name"
