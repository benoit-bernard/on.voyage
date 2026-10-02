#!/usr/bin/env bash
# Tests of deploy/check-secrets.sh (no network, no gh): bash deploy/check-secrets.test.sh
set -uo pipefail

here=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
script="$here/check-secrets.sh"
tmp=$(mktemp -d)
trap 'rm -rf "$tmp"' EXIT
failures=0

run() {  # run <args...> : output in $out, exit code in $code, with an empty environment
  out=$(env -i PATH="$PATH" HOME="$tmp" bash "$script" "$@" 2>&1)
  code=$?
}
expect() {  # expect <name> <condition>
  if ! eval "$2"; then echo "FAIL: $1"; echo "$out" | sed 's/^/    /'; failures=$((failures + 1)); else echo "ok:   $1"; fi
}

hex64=0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef
good="$tmp/good.env"
cat > "$good" <<EOF
# a complete staging configuration with obviously fake values
STAGING_SSH_HOST=203.0.113.10
STAGING_SSH_USER=deploy
STAGING_SSH_PRIVATE_KEY='-----BEGIN OPENSSH PRIVATE KEY----- FAKEKEYBODY -----END OPENSSH PRIVATE KEY-----'
STAGING_SSH_KNOWN_HOSTS="203.0.113.10 ssh-ed25519 AAAAFAKE"
STAGING_POSTGRES_PASSWORD=$hex64
STAGING_JWT_SECRET=$hex64
STAGING_BOOTSTRAP_ADMIN_EMAIL=admin@example.org
STAGING_RESEND_API_KEY=re_FAKEFAKEFAKE
STAGING_OPENAI_API_KEY=sk-FAKEFAKEFAKE
STAGING_DOMAIN=staging.example.org
STAGING_ACME_EMAIL=ops@example.org
STAGING_BACKUP_AGE_RECIPIENT=age1fakefakefakefakefakefakefakefakefakefakefakefakefakefakefake
STAGING_LLM_EXTRACTOR_MODEL=model-a
STAGING_LLM_WRITER_MODEL=model-b
STAGING_LLM_VERIFIER_MODEL=model-c
STAGING_LLM_CLASSIFIER_MODEL=model-d
EOF

run --env-file "$good"
expect "a complete file passes" '[[ $code -eq 0 && $out == *"0 manquant(s), 0 invalide(s)"* ]]'
expect "no value is ever printed" '[[ $out != *FAKEKEYBODY* && $out != *"$hex64"* && $out != *sk-FAKE* && $out != *admin@example.org* ]]'
expect "optional names are listed as absent, not as errors" '[[ $out == *"absent    STAGING_EMAIL_FROM"* ]]'

grep -v '^STAGING_JWT_SECRET' "$good" > "$tmp/nojwt.env"
run --env-file "$tmp/nojwt.env"
expect "a missing required secret fails and is named" '[[ $code -eq 1 && $out == *"MANQUANT  STAGING_JWT_SECRET"* ]]'

sed 's/^STAGING_JWT_SECRET=.*/STAGING_JWT_SECRET=SENTINELshort/' "$good" > "$tmp/short.env"
run --env-file "$tmp/short.env"
expect "a short JWT secret is invalid and its value is not echoed" '[[ $code -eq 1 && $out == *"INVALIDE  STAGING_JWT_SECRET"* && $out != *SENTINELshort* ]]'

sed 's/^STAGING_POSTGRES_PASSWORD=.*/STAGING_POSTGRES_PASSWORD=abcdefghijklmnop$qrst/' "$good" > "$tmp/dollar.env"
run --env-file "$tmp/dollar.env"
expect "a password containing a dollar sign is refused" '[[ $code -eq 1 && $out == *"INVALIDE  STAGING_POSTGRES_PASSWORD"* ]]'

sed 's/^STAGING_BACKUP_AGE_RECIPIENT=.*/STAGING_BACKUP_AGE_RECIPIENT=AGE-SECRET-KEY-1FAKE/' "$good" > "$tmp/agepriv.env"
run --env-file "$tmp/agepriv.env"
expect "the private age key is refused" '[[ $code -eq 1 && $out == *"INVALIDE  STAGING_BACKUP_AGE_RECIPIENT"* && $out != *AGE-SECRET-KEY-1FAKE* ]]'

sed 's#^STAGING_DOMAIN=.*#STAGING_DOMAIN=https://staging.example.org/#' "$good" > "$tmp/url.env"
run --env-file "$tmp/url.env"
expect "a domain with a scheme is refused" '[[ $code -eq 1 && $out == *"INVALIDE  STAGING_DOMAIN"* ]]'

grep -v 'OPENAI\|LLM_' "$good" > "$tmp/nollm.env"
run --env-file "$tmp/nollm.env"
expect "without OpenAI settings it fails" '[[ $code -eq 1 && $out == *"MANQUANT  STAGING_OPENAI_API_KEY"* ]]'
run --env-file "$tmp/nollm.env" --provider-disabled
expect "with the provider disabled the OpenAI settings are not required" '[[ $code -eq 0 ]]'
echo "STAGING_LLM_PROVIDER=disabled" >> "$tmp/nollm.env"
run --env-file "$tmp/nollm.env"
expect "STAGING_LLM_PROVIDER=disabled has the same effect" '[[ $code -eq 0 ]]'

run --env-file "$good" --with-observability
expect "observability requires the Grafana password" '[[ $code -eq 1 && $out == *"MANQUANT  STAGING_GRAFANA_ADMIN_PASSWORD"* ]]'

run --env-file "$good" --with-mobile
expect "mobile signing names are required only with --with-mobile and are flagged as proposed" '[[ $code -eq 1 && $out == *"MANQUANT  ANDROID_KEYSTORE_BASE64"* && $out == *"PROPOSÉS"* ]]'

sed 's/^STAGING_JWT_SECRET=.*//' "$good" > "$tmp/envfirst.env"
out=$(env -i PATH="$PATH" HOME="$tmp" STAGING_JWT_SECRET="$hex64" bash "$script" --env-file "$tmp/envfirst.env" 2>&1); code=$?
: "$code"  # read by the eval in expect()
expect "the process environment is read too" '[[ $code -eq 0 ]]'

server="$tmp/server.env"
cat > "$server" <<EOF
STAGING_DOMAIN=staging.example.org
ACME_EMAIL=ops@example.org
POSTGRES_PASSWORD=$hex64
JWT_SECRET=$hex64
BOOTSTRAP_ADMIN_EMAIL=admin@example.org
RESEND_API_KEY=re_FAKE
BACKUP_AGE_RECIPIENT=age1fakefakefakefakefakefakefakefakefakefakefakefakefakefakefake
FACTORY_LLM_PROVIDER=disabled
EOF
run --mode server --env-file "$server"
expect "the server .env is checked with compose names" '[[ $code -eq 0 ]]'
sed -i '/^JWT_SECRET/d' "$server"
run --mode server --env-file "$server"
expect "a server .env without JWT_SECRET fails" '[[ $code -eq 1 && $out == *"MANQUANT  JWT_SECRET"* ]]'

run --nonsense
expect "an unknown option is a usage error" '[[ $code -eq 2 ]]'
run --env-file "$tmp/does-not-exist"
expect "an unreadable file is a usage error" '[[ $code -eq 2 ]]'

echo
if ((failures)); then echo "$failures test(s) failed"; exit 1; fi
echo "all tests passed"
