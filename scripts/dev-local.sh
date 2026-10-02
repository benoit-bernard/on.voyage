#!/usr/bin/env bash
# Lance toute la plateforme ON.VOYAGE en local, SANS Docker ni Aspire :
#   PostgreSQL/PostGIS local + chaque service en processus `dotnet run`.
#
# Usage :
#   scripts/dev-local.sh up        [--build] [--no-snapshot]   démarre tout (défaut)
#   scripts/dev-local.sh down                                  arrête les services (pas PostgreSQL)
#   scripts/dev-local.sh status                                état et adresses
#   scripts/dev-local.sh restart <service>...                  relance des services (après une recompilation)
#   scripts/dev-local.sh logs <service>                        suit le journal d'un service
#   scripts/dev-local.sh reset-db                              supprime la base `onvoyage` et le dossier média
#
# Réglages (variables d'environnement) : voir la section « Réglages » ci-dessous et docs/runbooks/dev-local.md.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

# ---------------------------------------------------------------- Réglages
RUN_DIR="${ONVOYAGE_RUN_DIR:-$ROOT/.local}"
PG_HOST="${PGHOST:-localhost}"
PG_PORT="${PGPORT:-5432}"
PG_SUPERUSER="${ONVOYAGE_PG_SUPERUSER:-postgres}"
PG_SUPERPASSWORD="${ONVOYAGE_PG_SUPERPASSWORD:-pgtest}"
DB_NAME="${ONVOYAGE_DB:-onvoyage}"
DB_USER="${ONVOYAGE_DB_USER:-ov}"
DB_PASSWORD="${ONVOYAGE_DB_PASSWORD:-ov}"
export ConnectionStrings__onvoyage="Host=${PG_HOST};Port=${PG_PORT};Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}"

# Clé HS256 de développement (>= 32 octets). Ne sert JAMAIS hors de ce poste.
export Auth__JwtSecret="${Auth__JwtSecret:-local-dev-only-jwt-secret-0123456789abcdef0123456789}"
export Exports__Directory="${Exports__Directory:-$RUN_DIR/exports}"
MEDIA_DIR="${ONVOYAGE_MEDIA_DIR:-$RUN_DIR/media}"
export Media__RootPath="$MEDIA_DIR"
export Factory__MediaDirectory="$MEDIA_DIR"
export Factory__DataDirectory="${Factory__DataDirectory:-$RUN_DIR/factory}"
export Email__Provider="${Email__Provider:-log}"
export ASPNETCORE_ENVIRONMENT="${ASPNETCORE_ENVIRONMENT:-Development}"
export DOTNET_ENVIRONMENT="$ASPNETCORE_ENVIRONMENT"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

# Ports (alignés sur les launchSettings : gateway 5080, PWA 5090).
P_PLATFORM="${P_PLATFORM:-5101}"; P_CATALOG="${P_CATALOG:-5102}"; P_DISCOVERY="${P_DISCOVERY:-5103}"
P_INSIGHTS="${P_INSIGHTS:-5104}"; P_CREATORS="${P_CREATORS:-5105}"; P_FACTORY="${P_FACTORY:-5106}"
P_WORKER="${P_WORKER:-5107}"; P_GATEWAY="${P_GATEWAY:-5080}"; P_WEBPUBLIC="${P_WEBPUBLIC:-5110}"; P_PWA="${P_PWA:-5090}"

# Fournisseurs de contenu. `offline` = adaptateurs déterministes + voix espeak-ng (aucune clé, aucun réseau).
# Le catalogue vient du snapshot (Factory -> Catalog), pas du jeu de démonstration interne de Catalog.
export Catalog__SeedDemoData="${Catalog__SeedDemoData:-false}"
export Factory__Llm__Provider="${Factory__Llm__Provider:-offline}"
export Factory__Tts__Provider="${Factory__Tts__Provider:-auto}"
# Importe le snapshot data-pipeline/marseille/ au démarrage du worker Factory (idempotent). --no-snapshot le désactive.
SNAPSHOT_IMPORT="${ONVOYAGE_IMPORT_SNAPSHOT:-true}"
export Factory__Snapshot__Directory="${Factory__Snapshot__Directory:-$ROOT/data-pipeline}"

# Découverte de services (même mécanisme que Aspire : services__<nom>__http__0). Les noms contiennent un tiret :
# impossibles à `export` en bash, on les passe donc par `env` au lancement de chaque processus.
SERVICE_ENV=(
  "services__platform-api__http__0=http://localhost:${P_PLATFORM}"
  "services__catalog-api__http__0=http://localhost:${P_CATALOG}"
  "services__discovery-api__http__0=http://localhost:${P_DISCOVERY}"
  "services__insights-api__http__0=http://localhost:${P_INSIGHTS}"
  "services__creators-api__http__0=http://localhost:${P_CREATORS}"
  "services__factory-api__http__0=http://localhost:${P_FACTORY}"
  "services__gateway__http__0=http://localhost:${P_GATEWAY}"
)
export Gateway__PlatformBaseAddress="http://localhost:${P_PLATFORM}"
export Cors__AllowedOrigins__0="http://localhost:${P_PWA}"
export Cors__AllowedOrigins__1="http://127.0.0.1:${P_PWA}"
# Les applications (PWA, mobile) lisent l'audio à l'adresse publique du Gateway.
export Media__PublicBaseUrl="http://localhost:${P_GATEWAY}/media"
export Catalog__BaseAddress="http://localhost:${P_CATALOG}"

SERVICES=(platform catalog discovery insights creators factory-api factory-worker gateway web-public pwa)

project_of() {
  case "$1" in
    platform) echo src/Services/Platform/OnVoyage.Platform.Api ;;
    catalog) echo src/Services/Catalog/OnVoyage.Catalog.Api ;;
    discovery) echo src/Services/Discovery/OnVoyage.Discovery.Api ;;
    insights) echo src/Services/Insights/OnVoyage.Insights.Api ;;
    creators) echo src/Services/Creators/OnVoyage.Creators.Api ;;
    factory-api) echo src/Services/Factory/OnVoyage.Factory.Api ;;
    factory-worker) echo src/Services/Factory/OnVoyage.Factory.Worker ;;
    gateway) echo src/Gateway/OnVoyage.Gateway ;;
    web-public) echo src/Web/OnVoyage.Web.Public ;;
    pwa) echo src/Web/OnVoyage.Web.Pwa ;;
    *) echo "service inconnu : $1" >&2; return 1 ;;
  esac
}

port_of() {
  case "$1" in
    platform) echo "$P_PLATFORM" ;; catalog) echo "$P_CATALOG" ;; discovery) echo "$P_DISCOVERY" ;;
    insights) echo "$P_INSIGHTS" ;; creators) echo "$P_CREATORS" ;; factory-api) echo "$P_FACTORY" ;;
    factory-worker) echo "$P_WORKER" ;; gateway) echo "$P_GATEWAY" ;; web-public) echo "$P_WEBPUBLIC" ;; pwa) echo "$P_PWA" ;;
  esac
}

log() { printf '\033[1;34m[dev-local]\033[0m %s\n' "$*"; }
die() { printf '\033[1;31m[dev-local] %s\033[0m\n' "$*" >&2; exit 1; }

# ---------------------------------------------------------------- PostgreSQL
psql_super() { PGPASSWORD="$PG_SUPERPASSWORD" psql -h "$PG_HOST" -p "$PG_PORT" -U "$PG_SUPERUSER" -v ON_ERROR_STOP=1 -qAt "$@"; }

ensure_postgres() {
  if ! PGPASSWORD="$PG_SUPERPASSWORD" pg_isready -h "$PG_HOST" -p "$PG_PORT" -q 2>/dev/null; then
    log "PostgreSQL ne répond pas : tentative de démarrage (service postgresql start)."
    (service postgresql start || sudo -n service postgresql start) >/dev/null 2>&1 || true
    for _ in $(seq 1 30); do PGPASSWORD="$PG_SUPERPASSWORD" pg_isready -h "$PG_HOST" -p "$PG_PORT" -q 2>/dev/null && break; sleep 1; done
  fi
  PGPASSWORD="$PG_SUPERPASSWORD" pg_isready -h "$PG_HOST" -p "$PG_PORT" -q || die "PostgreSQL injoignable sur ${PG_HOST}:${PG_PORT}."
  psql_super -d postgres -c "select 1" >/dev/null || die "Connexion super-utilisateur refusée (ONVOYAGE_PG_SUPERUSER / ONVOYAGE_PG_SUPERPASSWORD)."

  if [ -z "$(psql_super -d postgres -c "select 1 from pg_roles where rolname='${DB_USER}'")" ]; then
    log "Création du rôle ${DB_USER}."
    psql_super -d postgres -c "create role ${DB_USER} login password '${DB_PASSWORD}' superuser"
  fi
  if [ -z "$(psql_super -d postgres -c "select 1 from pg_database where datname='${DB_NAME}'")" ]; then
    log "Création de la base ${DB_NAME}."
    psql_super -d postgres -c "create database ${DB_NAME} owner ${DB_USER}"
  fi
  # PostGIS, trigrammes et unaccent : les migrations les demandent, mais l'extension doit être installable par le rôle.
  psql_super -d "$DB_NAME" -c "create extension if not exists postgis; create extension if not exists pg_trgm; create extension if not exists unaccent;" >/dev/null
  log "PostgreSQL prêt (${DB_NAME}, PostGIS $(psql_super -d "$DB_NAME" -c 'select postgis_lib_version()'))."
}

# ---------------------------------------------------------------- Processus
pid_file() { echo "$RUN_DIR/pids/$1.pid"; }
log_file() { echo "$RUN_DIR/logs/$1.log"; }

is_running() { local f; f="$(pid_file "$1")"; [ -f "$f" ] && kill -0 "$(cat "$f")" 2>/dev/null; }

wait_http() { # nom url délai
  local name="$1" url="$2" max="${3:-120}"
  for _ in $(seq 1 "$max"); do
    if curl -fsS -o /dev/null --max-time 2 "$url" 2>/dev/null; then return 0; fi
    is_running "$name" || { tail -n 30 "$(log_file "$name")" >&2; die "$name s'est arrêté pendant le démarrage (voir $(log_file "$name"))."; }
    sleep 1
  done
  tail -n 30 "$(log_file "$name")" >&2
  die "$name ne répond pas sur $url après ${max}s."
}

start_service() {
  local name="$1" project port; project="$(project_of "$name")"; port="$(port_of "$name")"
  if is_running "$name"; then log "$name déjà démarré (pid $(cat "$(pid_file "$name")"))."; return; fi
  mkdir -p "$RUN_DIR/pids" "$RUN_DIR/logs"
  # La PWA lit l'adresse du Gateway dans wwwroot/appsettings.Development.json (déjà http://localhost:5080/).
  log "Démarrage de $name sur le port $port."
  nohup env "${SERVICE_ENV[@]}" "Factory__Snapshot__ImportOnStart=$SNAPSHOT_IMPORT" ASPNETCORE_URLS="http://localhost:${port}" \
    dotnet run --no-build --no-launch-profile --project "$project" >"$(log_file "$name")" 2>&1 &
  echo $! >"$(pid_file "$name")"
  wait_http "$name" "http://localhost:${port}/$([ "$name" = pwa ] && echo '' || echo alive)" "${START_TIMEOUT:-150}"
}

stop_service() {
  local name="$1" f; f="$(pid_file "$name")"
  [ -f "$f" ] || return 0
  local pid; pid="$(cat "$f")"
  if kill -0 "$pid" 2>/dev/null; then
    # `dotnet run` lance un processus enfant : on arrête tout le groupe de descendants.
    pkill -TERM -P "$pid" 2>/dev/null || true
    kill -TERM "$pid" 2>/dev/null || true
    for _ in $(seq 1 15); do kill -0 "$pid" 2>/dev/null || break; sleep 1; done
    pkill -KILL -P "$pid" 2>/dev/null || true
    kill -KILL "$pid" 2>/dev/null || true
  fi
  rm -f "$f"
  log "$name arrêté."
}

cmd_up() {
  local do_build=false
  for arg in "$@"; do
    case "$arg" in
      --build) do_build=true ;;
      --no-snapshot) SNAPSHOT_IMPORT=false ;;
      *) die "option inconnue : $arg" ;;
    esac
  done
  mkdir -p "$RUN_DIR" "$MEDIA_DIR" "$Exports__Directory" "$Factory__DataDirectory"
  command -v dotnet >/dev/null || die "dotnet introuvable."
  ensure_postgres
  if $do_build || [ ! -d "$ROOT/src/Gateway/OnVoyage.Gateway/bin" ]; then
    log "Compilation (dotnet build OnVoyage.slnx -m:1)."
    dotnet build OnVoyage.slnx -m:1 -nologo -v q
  fi
  # Ordre : Platform d'abord (il publie la configuration), puis les services de données, puis la porte d'entrée.
  for s in platform catalog discovery insights creators factory-api factory-worker gateway web-public pwa; do start_service "$s"; done
  log "Tout est démarré."
  cmd_status
}

cmd_down() {
  for ((i=${#SERVICES[@]}-1; i>=0; i--)); do stop_service "${SERVICES[$i]}"; done
  dotnet build-server shutdown >/dev/null 2>&1 || true
}

cmd_status() {
  printf '%-16s %-8s %s\n' SERVICE ÉTAT ADRESSE
  for s in "${SERVICES[@]}"; do
    local state=arrêté; is_running "$s" && state=démarré
    printf '%-16s %-8s http://localhost:%s\n' "$s" "$state" "$(port_of "$s")"
  done
  echo "Gateway : http://localhost:${P_GATEWAY}   PWA : http://localhost:${P_PWA}   Média : $MEDIA_DIR   Journaux : $RUN_DIR/logs"
}

cmd_reset_db() {
  cmd_down
  psql_super -d postgres -c "drop database if exists ${DB_NAME} with (force)"
  rm -rf "$MEDIA_DIR" "$Factory__DataDirectory" "$Exports__Directory"
  log "Base et média supprimés."
}

case "${1:-up}" in
  up) shift || true; cmd_up "$@" ;;
  down) cmd_down ;;
  status) cmd_status ;;
  restart) shift; for s in "$@"; do stop_service "$s"; start_service "$s"; done ;;
  logs) [ -n "${2:-}" ] || die "usage : logs <service>"; tail -n 100 -f "$(log_file "$2")" ;;
  reset-db) cmd_reset_db ;;
  *) die "commande inconnue : $1 (up|down|status|logs|reset-db)" ;;
esac
