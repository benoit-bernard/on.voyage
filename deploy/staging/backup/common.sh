#!/usr/bin/env bash
# Shared helpers of the backup scripts. Sourced, never executed.
# Connection settings are the standard libpq variables (PGHOST, PGPORT, PGUSER, PGPASSWORD, PGDATABASE). Keys are referenced by
# variable NAME only: BACKUP_AGE_RECIPIENT (public key, encryption) and AGE_IDENTITY_FILE (path of the private key, restore only).

log() { printf '%s %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*" >&2; }
die() { log "ERROR: $*"; exit 1; }

BACKUP_DIR=${BACKUP_DIR:-/backups}
WAL_INCOMING_DIR=${WAL_INCOMING_DIR:-/wal-incoming}
BACKUP_RETENTION_DAYS=${BACKUP_RETENTION_DAYS:-30}

require_recipient() {
  [[ -n "${BACKUP_AGE_RECIPIENT:-}" ]] || die "BACKUP_AGE_RECIPIENT is not set (public age key, age1...)."
  [[ "$BACKUP_AGE_RECIPIENT" == age1* ]] || die "BACKUP_AGE_RECIPIENT must be an age public key (age1...). The private key must never be given to this service."
}

require_identity() {
  [[ -n "${AGE_IDENTITY_FILE:-}" && -r "${AGE_IDENTITY_FILE}" ]] || die "AGE_IDENTITY_FILE must name a readable age private key file."
}

timestamp() { date -u +%Y%m%dT%H%M%SZ; }
