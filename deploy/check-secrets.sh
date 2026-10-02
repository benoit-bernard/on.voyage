#!/usr/bin/env bash
# Checks that every secret and variable the deployment expects is present, WITHOUT ever printing a value (H-003).
#
#   deploy/check-secrets.sh                              names read from the environment (STAGING_JWT_SECRET=... deploy/check-secrets.sh)
#   deploy/check-secrets.sh --env-file ~/secrets/onvoyage-staging.env     ...and from a KEY=VALUE file kept OUTSIDE the repository
#   deploy/check-secrets.sh --github [--repo owner/name]   ...and from GitHub (needs the gh CLI, logged in): environment "staging" + repository
#   deploy/check-secrets.sh --mode server --env-file deploy/staging/.env   the .env of the VPS (docker compose names, no STAGING_ prefix)
#   Options: --with-observability  --with-mobile  --provider-disabled  --help
#
# The list is the one of docs/runbooks/h003-comptes-et-secrets.md, extracted from .github/workflows/deploy-staging.yml,
# deploy/staging/docker-compose.yml and AppHost.cs. Exit code: 0 = nothing missing or invalid, 1 = something to fix, 2 = usage.
# Only the NAME and a verdict are printed. Formats are checked locally (length, prefix, forbidden characters) on the value held in memory.
set -euo pipefail

mode=github-names
env_file=""
use_github=0
repo=""
with_obs=0
with_mobile=0
provider_disabled=0

usage() { sed -n '2,12p' "$0" | sed 's/^# \{0,1\}//'; }

while (($#)); do
  case "$1" in
    --mode) mode=${2:?--mode needs github-names or server}; [[ $mode == server ]] || mode=github-names; shift 2 ;;
    --env-file) env_file=${2:?--env-file needs a path}; shift 2 ;;
    --github) use_github=1; shift ;;
    --repo) repo=${2:?--repo needs owner/name}; shift 2 ;;
    --with-observability) with_obs=1; shift ;;
    --with-mobile) with_mobile=1; shift ;;
    --provider-disabled) provider_disabled=1; shift ;;
    -h | --help) usage; exit 0 ;;
    *) echo "Unknown option: $1" >&2; usage >&2; exit 2 ;;
  esac
done

declare -A VALUE=()     # name -> value read from the environment or the file (never printed)
declare -A REMOTE=()    # name -> 1 when GitHub lists it (names only: GitHub never returns a secret)

# ---------------------------------------------------------------------------------------------------- sources
load_file() {
  local file=$1 line key val
  [[ -r $file ]] || { echo "Cannot read $file" >&2; exit 2; }
  while IFS= read -r line || [[ -n $line ]]; do
    [[ $line =~ ^[[:space:]]*# || -z ${line//[[:space:]]/} ]] && continue
    [[ $line == *=* ]] || continue
    key=${line%%=*}; val=${line#*=}
    key=${key//[[:space:]]/}; key=${key#export}
    [[ $key =~ ^[A-Za-z_][A-Za-z0-9_]*$ ]] || continue
    if [[ $val == \'*\' ]]; then val=${val:1:${#val}-2}; elif [[ $val == \"*\" ]]; then val=${val:1:${#val}-2}; fi
    VALUE[$key]=$val
  done < "$file"
}

load_github() {
  command -v gh > /dev/null || { echo "gh CLI not found: install it or drop --github" >&2; exit 2; }
  local flags=() scope kind
  [[ -z $repo ]] || flags=(--repo "$repo")
  for kind in secret variable; do
    # Repository level, then the environment "staging" (the deploy job runs in it). Only names are requested.
    for scope in "" "--env staging"; do
      # shellcheck disable=SC2086
      while IFS= read -r name; do [[ -z $name ]] || REMOTE[$name]=1; done < <(gh "$kind" list "${flags[@]}" $scope --json name --jq '.[].name' 2> /dev/null || true)
    done
  done
  ((${#REMOTE[@]} > 0)) || echo "Warning: gh returned no secret or variable (not logged in, wrong repository, or none created yet)." >&2
}

[[ -z $env_file ]] || load_file "$env_file"
((use_github == 0)) || load_github

get() {  # value of NAME: environment first, then the file
  local name=$1
  if [[ -n ${!name-} ]]; then printf '%s' "${!name}"; elif [[ -n ${VALUE[$name]-} ]]; then printf '%s' "${VALUE[$name]}"; fi
}
present() { [[ -n $(get "$1") || -n ${REMOTE[$1]-} ]]; }
local_value() { [[ -n $(get "$1") ]]; }

# ---------------------------------------------------------------------------------------------------- formats
# Each checker receives the value on stdin-free argument and returns 0 when valid; the reason is set in $why.
why=""
fmt_none() { return 0; }
fmt_secret_strong() { [[ ${#1} -ge 32 ]] || { why="moins de 32 caractères (openssl rand -hex 32)"; return 1; }; fmt_safe "$1"; }
fmt_safe() { if [[ $1 == *"'"* || $1 == *" "* || $1 == *";"* || $1 == *'$'* ]]; then why="contient ' espace ; ou \$ (interdits dans .env)"; return 1; fi; return 0; }
fmt_password() { [[ ${#1} -ge 16 ]] || { why="moins de 16 caractères"; return 1; }; fmt_safe "$1"; }
fmt_email() { [[ $1 =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] || { why="n'est pas une adresse e-mail"; return 1; }; }
fmt_email_from() { [[ $1 =~ \<[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+\>$ || $1 =~ ^[^@[:space:]]+@[^@[:space:]]+\.[^@[:space:]]+$ ]] || { why="attendu : Nom <adresse> ou adresse"; return 1; }; }
fmt_host() { [[ $1 =~ ^[A-Za-z0-9]([A-Za-z0-9.-]*[A-Za-z0-9])?$ ]] || { why="un nom DNS ou une IP, sans schéma ni chemin"; return 1; }; }
fmt_domain() { fmt_host "$1" && [[ $1 == *.* ]] || { why="un nom de domaine (sans https://, sans /), ex. staging.on.voyage"; return 1; }; }
fmt_port() { [[ $1 =~ ^[0-9]{1,5}$ ]] || { why="un numéro de port"; return 1; }; }
fmt_age() { [[ $1 == age1* && $1 != *AGE-SECRET-KEY* ]] || { why="clé PUBLIQUE age (age1…), jamais la clé privée"; return 1; }; }
fmt_ssh_key() { [[ $1 == *"PRIVATE KEY"* ]] || { why="n'est pas une clé privée SSH (en-tête BEGIN … PRIVATE KEY manquant)"; return 1; }; }
fmt_known_hosts() { [[ $1 == *ssh-* || $1 == *ecdsa-* ]] || { why="sortie de ssh-keyscan attendue (ssh-ed25519 …)"; return 1; }; }
fmt_openai() { [[ $1 == sk-* ]] || { why="une clé OpenAI commence par sk-"; return 1; }; }
fmt_resend() { [[ $1 == re_* ]] || { why="une clé Resend commence par re_"; return 1; }; }
fmt_provider() { [[ $1 == openai || $1 == disabled ]] || { why="openai ou disabled"; return 1; }; }
fmt_bool() { [[ $1 == true || $1 == false ]] || { why="true ou false"; return 1; }; }
fmt_url() { [[ $1 == https://* ]] || { why="une URL https://"; return 1; }; }
fmt_otlp() { [[ $1 == http://* || $1 == https://* ]] || { why="une URL http(s)://"; return 1; }; }
fmt_path() { [[ $1 == /* ]] || { why="un chemin absolu"; return 1; }; }

# ---------------------------------------------------------------------------------------------------- the list
# kind|github name|compose name|requirement|format|where it is read
#   requirement: always | llm (unless the provider is disabled) | obs (with --with-observability) | optional | mobile (with --with-mobile)
ROWS=(
  "secret|STAGING_SSH_HOST|-|always|fmt_host|deploy-staging.yml : Prepare SSH"
  "secret|STAGING_SSH_USER|-|always|fmt_none|deploy-staging.yml : Prepare SSH"
  "secret|STAGING_SSH_PRIVATE_KEY|-|always|fmt_ssh_key|deploy-staging.yml : Prepare SSH"
  "secret|STAGING_SSH_KNOWN_HOSTS|-|always|fmt_known_hosts|deploy-staging.yml : Prepare SSH"
  "secret|STAGING_POSTGRES_PASSWORD|POSTGRES_PASSWORD|always|fmt_password|compose : ConnectionStrings__onvoyage, postgres, backup"
  "secret|STAGING_JWT_SECRET|JWT_SECRET|always|fmt_secret_strong|compose : Auth__JwtSecret (tous les services)"
  "secret|STAGING_BOOTSTRAP_ADMIN_EMAIL|BOOTSTRAP_ADMIN_EMAIL|always|fmt_email|compose : Auth__BootstrapAdminEmails__0"
  "secret|STAGING_RESEND_API_KEY|RESEND_API_KEY|always|fmt_resend|compose : Email__Resend__ApiKey"
  "secret|STAGING_OPENAI_API_KEY|OPENAI_API_KEY|llm|fmt_openai|compose : OpenAI__ApiKey (factory-worker et factory-api)"
  "secret|STAGING_GRAFANA_ADMIN_PASSWORD|GRAFANA_ADMIN_PASSWORD|obs|fmt_password|observability/docker-compose.yml : Grafana"
  "var|STAGING_DOMAIN|STAGING_DOMAIN|always|fmt_domain|compose, Caddyfile, environment.url du job deploy"
  "var|STAGING_ACME_EMAIL|ACME_EMAIL|always|fmt_email|compose : caddy"
  "var|STAGING_BACKUP_AGE_RECIPIENT|BACKUP_AGE_RECIPIENT|always|fmt_age|compose : backup"
  "var|STAGING_LLM_EXTRACTOR_MODEL|FACTORY_LLM_EXTRACTOR_MODEL|llm|fmt_none|compose : Factory__Llm__ExtractorModel"
  "var|STAGING_LLM_WRITER_MODEL|FACTORY_LLM_WRITER_MODEL|llm|fmt_none|compose : Factory__Llm__WriterModel"
  "var|STAGING_LLM_VERIFIER_MODEL|FACTORY_LLM_VERIFIER_MODEL|llm|fmt_none|compose : Factory__Llm__VerifierModel"
  "var|STAGING_LLM_CLASSIFIER_MODEL|FACTORY_LLM_CLASSIFIER_MODEL|llm|fmt_none|compose : Factory__Llm__ClassifierModel"
  "var|STAGING_LLM_PROVIDER|FACTORY_LLM_PROVIDER|optional|fmt_provider|compose : Factory__Llm__Provider (défaut openai)"
  "var|STAGING_EMAIL_FROM|EMAIL_FROM|optional|fmt_email_from|compose : Email__From"
  "var|STAGING_MAP_TILES_URL|PWA_MAP_TILES_URL|optional|fmt_url|images (job sans environnement : variable de DÉPÔT) -> web-pwa"
  "var|STAGING_DEPLOY_PATH|-|optional|fmt_path|deploy-staging.yml (défaut /opt/onvoyage)"
  "var|STAGING_SSH_PORT|-|optional|fmt_port|deploy-staging.yml (défaut 22)"
  "var|STAGING_OBSERVABILITY|-|optional|fmt_bool|deploy-staging.yml : pile d'observabilité"
  "var|STAGING_OTLP_ENDPOINT|OTEL_EXPORTER_OTLP_ENDPOINT|optional|fmt_otlp|compose : tous les services"
)
MOBILE_ROWS=(
  "secret|ANDROID_KEYSTORE_BASE64|-|mobile|fmt_none|PROPOSÉ, pas encore lu par mobile.yml : keystore .jks en base64"
  "secret|ANDROID_KEYSTORE_PASSWORD|-|mobile|fmt_none|PROPOSÉ : mot de passe du keystore"
  "secret|ANDROID_KEY_ALIAS|-|mobile|fmt_none|PROPOSÉ : alias de la clé d'upload"
  "secret|ANDROID_KEY_PASSWORD|-|mobile|fmt_none|PROPOSÉ : mot de passe de la clé"
  "secret|IOS_CERTIFICATE_P12_BASE64|-|mobile|fmt_none|PROPOSÉ : certificat de distribution .p12 en base64"
  "secret|IOS_CERTIFICATE_PASSWORD|-|mobile|fmt_none|PROPOSÉ : mot de passe du .p12"
  "secret|IOS_PROVISIONING_PROFILE_BASE64|-|mobile|fmt_none|PROPOSÉ : profil de provisionnement en base64"
  "secret|APP_STORE_CONNECT_KEY_ID|-|mobile|fmt_none|PROPOSÉ : clé API App Store Connect (TestFlight)"
  "secret|APP_STORE_CONNECT_ISSUER_ID|-|mobile|fmt_none|PROPOSÉ : émetteur de la clé API"
  "secret|APP_STORE_CONNECT_PRIVATE_KEY|-|mobile|fmt_ssh_key|PROPOSÉ : clé privée .p8"
  "secret|GOOGLE_PLAY_SERVICE_ACCOUNT_JSON|-|mobile|fmt_none|PROPOSÉ : compte de service Google Play (piste interne)"
)

# ---------------------------------------------------------------------------------------------------- evaluation
provider=$(get STAGING_LLM_PROVIDER)
[[ -n $provider ]] || provider=$(get FACTORY_LLM_PROVIDER)
((provider_disabled == 1)) || [[ $provider != disabled ]] || provider_disabled=1
obs_flag=$(get STAGING_OBSERVABILITY)
[[ $obs_flag != true ]] || with_obs=1

missing=0; invalid=0; ok=0; optional_absent=0
report() { printf '  %-9s %-36s %s\n' "$1" "$2" "$3"; }

evaluate() {
  local row=$1 kind gh_name compose_name req fmt where name needed
  IFS='|' read -r kind gh_name compose_name req fmt where <<< "$row"
  name=$gh_name
  if [[ $mode == server ]]; then
    [[ $compose_name != "-" ]] || return 0   # SSH and path settings only exist on the GitHub side
    name=$compose_name
  fi
  case $req in
    always) needed=1 ;;
    llm) needed=$((provider_disabled == 1 ? 0 : 1)) ;;
    obs) needed=$with_obs ;;
    mobile) needed=$with_mobile ;;
    *) needed=0 ;;
  esac
  if ! present "$name"; then
    if ((needed)); then report "MANQUANT" "$name" "[$kind] $where"; missing=$((missing + 1)); else report "absent" "$name" "[$kind] facultatif ($req) - $where"; optional_absent=$((optional_absent + 1)); fi
    return 0
  fi
  if local_value "$name"; then
    why=""
    if "$fmt" "$(get "$name")"; then report "OK" "$name" "[$kind] présent, format plausible"; ok=$((ok + 1)); else report "INVALIDE" "$name" "[$kind] $why"; invalid=$((invalid + 1)); fi
  else
    report "OK" "$name" "[$kind] présent sur GitHub (valeur non lisible, format non vérifié)"; ok=$((ok + 1))
  fi
}

echo "Contrôle des secrets et variables - mode: $mode$( ((use_github)) && echo ' + GitHub' )$( [[ -n $env_file ]] && echo ' + fichier' )"
echo "(aucune valeur n'est affichée)"
echo
for row in "${ROWS[@]}"; do evaluate "$row"; done
if ((with_mobile)) && [[ $mode != server ]]; then
  echo
  echo "Signature des applications (noms PROPOSÉS : aucun workflow ne les lit encore, à confirmer quand mobile.yml signera)"
  for row in "${MOBILE_ROWS[@]}"; do evaluate "$row"; done
fi
if [[ $mode != server && use_github -eq 1 ]]; then
  echo
  echo "Rappel : STAGING_MAP_TILES_URL est lue par le job d'images (sans environnement) : la créer comme variable de DÉPÔT."
fi

echo
echo "Résumé : $ok présent(s), $missing manquant(s), $invalid invalide(s), $optional_absent facultatif(s) absent(s)."
if ((missing + invalid > 0)); then
  echo "Voir docs/runbooks/h003-comptes-et-secrets.md pour créer ce qui manque."
  exit 1
fi
echo "Rien à corriger."
