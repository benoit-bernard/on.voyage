#!/usr/bin/env bash
# Restores an encrypted logical backup (backup.sh dump) into a NEW database.
#
#   AGE_IDENTITY_FILE=/path/to/private.key PGHOST=... PGUSER=... PGPASSWORD=... \
#     restore.sh <backup.dump.age> <target-database> [--replace]
#
# The target must not exist (--replace drops it first: only for a database you are sure to discard). The connecting role must be able to
# create the database and the extensions (postgis, pg_trgm, unaccent): the superuser of the container, or a CI superuser.
# The private key is read from AGE_IDENTITY_FILE (a path, never a value) and is not copied anywhere.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=common.sh
source "$here/common.sh"

[[ $# -ge 2 ]] || die "usage: restore.sh <backup.dump.age> <target-database> [--replace]"
backup=$1
target=$2
replace=${3:-}
[[ -r "$backup" ]] || die "cannot read $backup"
require_identity

if [[ -r "$backup.sha256" ]]; then
  log "checking checksum"
  (cd "$(dirname "$backup")" && sha256sum --check --quiet "$(basename "$backup").sha256") || die "checksum mismatch: the backup is corrupted"
fi

exists=$(psql --no-psqlrc -Atd postgres -c "select 1 from pg_database where datname = '${target//\'/}'")
if [[ "$exists" == "1" ]]; then
  [[ "$replace" == "--replace" ]] || die "database '$target' already exists (use --replace to drop it)"
  log "dropping existing database $target"
  dropdb --force "$target"
fi

log "creating database $target"
createdb "$target"
log "restoring $backup -> $target"
age --decrypt --identity "$AGE_IDENTITY_FILE" "$backup" | pg_restore --dbname="$target" --no-owner --no-acl --exit-on-error
log "restore finished; run ANALYZE and compare row counts before switching traffic (docs/runbooks/restore.md)"
psql --no-psqlrc -q -d "$target" -c "ANALYZE"
