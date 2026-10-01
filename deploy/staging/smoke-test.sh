#!/usr/bin/env bash
# Smoke test of a deployed environment, through the public entry point (Caddy -> Gateway).
#
#   deploy/staging/smoke-test.sh https://staging.example.org
#
# 1. GET /health            the Gateway answers (retried: containers may still be warming up)
# 2. GET /api/platform/v1/config   anonymous route Gateway -> Platform (proves service discovery and the Platform database)
# 3. POST /api/platform/v1/auth/anonymous then GET /api/catalog/v1/destinations/marseille with the token
#                           token issued by Platform, validated by the Gateway and Catalog (200, or 404 when the demo seed is off;
#                           401/403/5xx fail). It creates one anonymous session, which holds no personal data.
# 4. GET / (PWA) and GET admin.<domain>/health are checked when ADMIN_URL is set.
# Requires curl and jq. No credential is read or printed.
set -euo pipefail

base=${1:?usage: smoke-test.sh <base-url>}
base=${base%/}
attempts=${SMOKE_ATTEMPTS:-30}
pause=${SMOKE_PAUSE_SECONDS:-5}

fail() { echo "SMOKE FAILED: $*" >&2; exit 1; }
status() { curl -sS -o /dev/null -w '%{http_code}' --max-time 15 "$@" || echo 000; }

echo "1. $base/health"
code=000
for ((i = 1; i <= attempts; i++)); do
  code=$(status "$base/health")
  [[ "$code" == 200 ]] && break
  echo "   attempt $i/$attempts: HTTP $code"
  sleep "$pause"
done
[[ "$code" == 200 ]] || fail "/health answered HTTP $code"
echo "   OK (200)"

echo "2. $base/api/platform/v1/config"
body=$(curl -sS --fail --max-time 15 "$base/api/platform/v1/config") || fail "config route did not answer 2xx"
[[ -n "$body" ]] || fail "config route returned an empty body"
echo "   OK ($(printf '%s' "$body" | wc -c) bytes)"

echo "3. anonymous session then $base/api/catalog/v1/destinations/marseille"
token=$(curl -sS --fail --max-time 15 -X POST "$base/api/platform/v1/auth/anonymous" | jq -er '.accessToken') || fail "could not open an anonymous session"
code=$(status -H "Authorization: Bearer $token" "$base/api/catalog/v1/destinations/marseille")
case "$code" in
  200) echo "   OK (200)" ;;
  404) echo "   OK (404: the service answered and accepted the token; no demo destination loaded)" ;;
  *) fail "catalog answered HTTP $code" ;;
esac
code=$(status "$base/api/catalog/v1/destinations/marseille")
[[ "$code" == 401 ]] || fail "an unauthenticated call to Catalog answered HTTP $code instead of 401"
echo "   OK (unauthenticated call is refused with 401)"

echo "4. PWA $base/"
code=$(status "$base/")
[[ "$code" == 200 ]] || fail "the PWA answered HTTP $code"
echo "   OK (200)"

if [[ -n "${ADMIN_URL:-}" ]]; then
  echo "5. ${ADMIN_URL%/}/health"
  code=$(status "${ADMIN_URL%/}/health")
  [[ "$code" == 200 ]] || fail "the back-office answered HTTP $code"
  echo "   OK (200)"
fi

echo "SMOKE PASSED"
