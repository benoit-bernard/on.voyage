#!/usr/bin/env bash
# Restore test (NF-06, T-009): dump -> encrypt -> decrypt -> restore into a fresh database -> compare row counts of every table.
# Runs against a THROWAWAY PostgreSQL (CI service container, local instance). Never point it at staging or production data you cannot
# afford to read: with SOURCE_DB it only reads that database, but it creates and drops databases named ov_restore_*.
#
#   PGHOST=localhost PGPORT=5432 PGUSER=<superuser> PGPASSWORD=<password> deploy/staging/backup/test-restore.sh
#
# Without SOURCE_DB a representative database is generated (schemas of the services, PostGIS geometry, trigram index, JSONB, bytea).
# With SOURCE_DB=<name> an existing database is tested instead (read-only on it).
# Requirements: psql, pg_dump, pg_restore, age, age-keygen, PostGIS + pg_trgm + unaccent installed on the server.
# Exit code 0 only when every table has the same row count in both databases and a spatial query returns the same answer.
set -euo pipefail
here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
# shellcheck source=common.sh
source "$here/common.sh"

for tool in psql pg_dump pg_restore age age-keygen; do command -v "$tool" >/dev/null || die "$tool is required"; done

work=$(mktemp -d)
suffix=$$
source_db=${SOURCE_DB:-ov_restore_src_$suffix}
target_db=ov_restore_dst_$suffix
created_source=0
cleanup() {
  local status=$?
  dropdb --if-exists --force "$target_db" >/dev/null 2>&1 || true
  [[ $created_source -eq 1 ]] && dropdb --if-exists --force "$source_db" >/dev/null 2>&1 || true
  rm -rf "$work"
  [[ $status -eq 0 ]] && log "RESULT: restore test PASSED" || log "RESULT: restore test FAILED"
}
trap cleanup EXIT

# Throwaway key pair: the private key lives in the temp directory only, for the duration of the test.
age-keygen -o "$work/identity.key" 2>"$work/keygen.txt"
export AGE_IDENTITY_FILE="$work/identity.key"
BACKUP_AGE_RECIPIENT=$(age-keygen -y "$AGE_IDENTITY_FILE")
export BACKUP_AGE_RECIPIENT
export BACKUP_DIR="$work/backups"

if [[ -z "${SOURCE_DB:-}" ]]; then
  log "creating source database $source_db"
  createdb "$source_db"
  created_source=1
  psql --no-psqlrc -q -v ON_ERROR_STOP=1 -d "$source_db" <<'SQL'
create extension postgis;
create extension pg_trgm;
create extension unaccent;
create schema platform;
create schema catalog;
create schema discovery;
create schema factory;
create schema wolverine_queues;

create table platform.account (id uuid primary key, email text unique, created_at timestamptz not null default now(), roles text[] not null default '{}');
insert into platform.account select gen_random_uuid(), 'user' || n || '@example.invalid', now() - n * interval '1 hour', case when n % 10 = 0 then '{admin}'::text[] else '{}'::text[] end from generate_series(1, 250) n;

create table catalog.poi (id uuid primary key, slug text not null unique, name text not null, location geometry(Point, 4326) not null, tags jsonb not null default '{}');
insert into catalog.poi select gen_random_uuid(), 'poi-' || n, 'Lieu numéro ' || n || ' à Marseille', ST_SetSRID(ST_MakePoint(5.20 + (n % 100) / 300.0, 43.20 + (n / 100) / 300.0), 4326), jsonb_build_object('n', n, 'tourism', 'attraction') from generate_series(1, 1200) n;
create index poi_location_gist on catalog.poi using gist (location);
create index poi_name_trgm on catalog.poi using gin (name gin_trgm_ops);

create table discovery.interaction (id bigserial primary key, traveler uuid not null, poi_id uuid not null, kind text not null, at timestamptz not null default now());
insert into discovery.interaction (traveler, poi_id, kind) select gen_random_uuid(), id, (array['like','dislike','listened'])[1 + (random() * 2)::int] from catalog.poi limit 900;

create table factory.audio_asset (id uuid primary key, poi_id uuid not null, payload bytea not null);
insert into factory.audio_asset select gen_random_uuid(), id, decode(md5(id::text), 'hex') from catalog.poi limit 300;

create table wolverine_queues.envelope (id uuid primary key, body bytea);
SQL
fi

# Row counts of every user table. spatial_ref_sys belongs to the PostGIS extension (recreated by CREATE EXTENSION, not data).
count_rows() {
  local db=$1 table
  psql --no-psqlrc -Atq -d "$db" -c "select quote_ident(table_schema) || '.' || quote_ident(table_name) from information_schema.tables where table_type = 'BASE TABLE' and table_schema not in ('pg_catalog', 'information_schema') and table_name <> 'spatial_ref_sys' order by 1" |
    while IFS= read -r table; do
      printf '%s %s\n' "$table" "$(psql --no-psqlrc -Atq -d "$db" -c "select count(*) from $table")"
    done
}

# Spatial sanity check: the GiST index and the geometry data must have survived.
spatial_answer() {
  psql --no-psqlrc -Atq -d "$1" -c "select count(*) from catalog.poi where ST_DWithin(location::geography, ST_SetSRID(ST_MakePoint(5.37, 43.22), 4326)::geography, 5000)" 2>/dev/null || echo "n/a"
}

log "backing up $source_db (pg_dump | age)"
PGDATABASE=$source_db "$here/backup.sh" dump
backup=$(find "$BACKUP_DIR/dumps" -name '*.dump.age' | head -n 1)
[[ -n "$backup" ]] || die "no backup file produced"
log "backup: $(basename "$backup") ($(stat -c %s "$backup") bytes, encrypted; first bytes: $(head -c 22 "$backup" | tr -d '\n'))"
if pg_restore --list <(cat "$backup") >/dev/null 2>&1; then die "the backup is readable without the key: encryption is not applied"; fi

log "restoring into fresh database $target_db"
"$here/restore.sh" "$backup" "$target_db"

count_rows "$source_db" > "$work/source.counts"
count_rows "$target_db" > "$work/target.counts"
printf '%-40s %10s %10s\n' TABLE SOURCE RESTORED
join <(sort "$work/source.counts") <(sort "$work/target.counts") | awk '{ printf "%-40s %10s %10s %s\n", $1, $2, $3, ($2 == $3 ? "ok" : "MISMATCH") }'

[[ -s "$work/source.counts" ]] || die "the source database has no table to compare"
diff -u "$work/source.counts" "$work/target.counts" >/dev/null || die "row counts differ between the source and the restored database"

src_spatial=$(spatial_answer "$source_db")
dst_spatial=$(spatial_answer "$target_db")
log "spatial query (POIs within 5 km of 43.22N 5.37E): source=$src_spatial restored=$dst_spatial"
[[ "$src_spatial" == "$dst_spatial" ]] || die "the spatial query answers differently after restore"
