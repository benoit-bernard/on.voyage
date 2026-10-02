#!/usr/bin/env bash
# Test de fumée de la plateforme lancée par scripts/dev-local.sh. Tout passe par le Gateway, comme un client :
#   session anonyme -> destination marseille -> lieux près du Vieux-Port -> histoire (texte + audio réellement téléchargé)
#   -> recommandations (Discovery) -> site public et PWA.
# Sortie : une ligne OK/ÉCHEC par étape et les vraies réponses ; code de sortie non nul au premier échec.
#
# Usage : scripts/smoke-local.sh [--wait-seconds N]   (N : attente du catalogue pendant l'import du snapshot, 600 par défaut)
set -euo pipefail

GATEWAY="${GATEWAY:-http://localhost:${P_GATEWAY:-5080}}"
WEB_PUBLIC="${WEB_PUBLIC:-http://localhost:${P_WEBPUBLIC:-5110}}"
PWA="${PWA:-http://localhost:${P_PWA:-5090}}"
DESTINATION="${DESTINATION:-marseille}"
MIN_POIS="${MIN_POIS:-30}"
LAT="${LAT:-43.2952}"   # Vieux-Port
LON="${LON:-5.3745}"
WAIT=600
[ "${1:-}" = "--wait-seconds" ] && WAIT="${2:?durée manquante}"

command -v curl >/dev/null && command -v jq >/dev/null || { echo "curl et jq sont requis." >&2; exit 2; }
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

step() { printf '\n\033[1;34m== %s\033[0m\n' "$*"; }
ok() { printf '\033[1;32mOK\033[0m     %s\n' "$*"; }
fail() { printf '\033[1;31mÉCHEC\033[0m  %s\n' "$*" >&2; exit 1; }

get() { curl -fsS --max-time 20 -H "Authorization: Bearer $TOKEN" "$@"; }

step "1. Le Gateway répond"
curl -fsS --max-time 10 "$GATEWAY/health" >/dev/null || fail "Gateway injoignable sur $GATEWAY (scripts/dev-local.sh up ?)"
ok "GET $GATEWAY/health"

step "2. Session anonyme (Platform)"
SESSION="$(curl -fsS --max-time 20 -X POST "$GATEWAY/api/platform/v1/auth/anonymous")" || fail "POST /auth/anonymous"
TOKEN="$(jq -r .accessToken <<<"$SESSION")"
[ -n "$TOKEN" ] && [ "$TOKEN" != null ] || fail "pas de jeton d'accès"
echo "$SESSION" | jq -c '{travelerId, isAnonymous, roles, accessTokenExpiresAt}'
ok "jeton obtenu (anonyme)"

step "3. Destination $DESTINATION (Catalog, attend l'import du snapshot jusqu'à ${WAIT}s)"
DEADLINE=$((SECONDS + WAIT)); COUNT=0
while :; do
  if DEST="$(get "$GATEWAY/api/catalog/v1/destinations/$DESTINATION" 2>/dev/null)"; then
    COUNT="$(jq -r .poiCount <<<"$DEST")"
    [ "$COUNT" -ge "$MIN_POIS" ] && break
  fi
  [ $SECONDS -lt $DEADLINE ] || fail "le catalogue n'a que ${COUNT} lieux après ${WAIT}s (attendus : $MIN_POIS) ; voir .local/logs/factory-worker.log"
  sleep 3
done
echo "$DEST" | jq -c .
ok "$COUNT lieux publiés"

step "4. Lieux autour du Vieux-Port (rayon 2 km)"
POIS="$(get "$GATEWAY/api/catalog/v1/destinations/$DESTINATION/pois?lat=$LAT&lon=$LON&radius=2000&limit=8")" || fail "GET pois"
jq -r '.[] | "  \(.distanceMeters // "?" )\tm\t\(.name)\t[\(.category)]\taudio=\(.audioSeconds // 0)s"' <<<"$POIS"
N="$(jq length <<<"$POIS")"
[ "$N" -ge 3 ] || fail "moins de 3 lieux près du Vieux-Port ($N)"
SLUG="$(jq -r '[.[] | select(.storyId != null)][0].slug' <<<"$POIS")"
[ "$SLUG" != null ] || fail "aucun lieu proche n'a d'histoire"
ok "$N lieux proches, premier avec histoire : $SLUG"

step "5. Histoire de $SLUG (texte + audio via le Gateway)"
DETAIL="$(get "$GATEWAY/api/catalog/v1/pois/$SLUG")" || fail "GET pois/$SLUG"
jq '{name, category, hiddenGem, crowdLevel, attributions, links: [.links[]? | {kind, url}], stories: [.stories[] | {title, durationSeconds, aiGenerated, audioUrl, text: (.text[0:160] + "…")}]}' <<<"$DETAIL"
AUDIO_URL="$(jq -r '.stories[0].audioUrl' <<<"$DETAIL")"
[ -n "$AUDIO_URL" ] && [ "$AUDIO_URL" != null ] || fail "l'histoire n'a pas d'URL audio (audio en attente ?)"
case "$AUDIO_URL" in "$GATEWAY"/media/*) ;; *) fail "l'URL audio ne passe pas par le Gateway : $AUDIO_URL";; esac
jq -e '.stories[0].aiGenerated == true' <<<"$DETAIL" >/dev/null || fail "l'histoire n'est pas marquée générée par IA"
CODE="$(curl -sS --max-time 30 -o "$TMP/audio.mp3" -w '%{http_code} %{content_type} %{size_download}' "$AUDIO_URL")"
echo "  GET $AUDIO_URL -> $CODE"
case "$CODE" in "200 audio/mpeg "*) ;; *) fail "audio non servi correctement ($CODE)";; esac
SIZE="${CODE##* }"; [ "$SIZE" -gt 20000 ] || fail "fichier audio trop petit ($SIZE octets)"
if command -v ffprobe >/dev/null; then
  ffprobe -hide_banner "$TMP/audio.mp3" 2>&1 | grep -E 'Duration|Stream|AI_GENERATED|TTS_PROVIDER|title' | sed 's/^/  /'
fi
RANGE="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' -H 'Range: bytes=0-99' "$AUDIO_URL")"
echo "  Range bytes=0-99 -> $RANGE"
[ "$RANGE" = 206 ] || fail "le serveur ne gère pas les requêtes Range ($RANGE) : la lecture et la recherche audio en dépendent"
ok "texte et audio servis par le Gateway (206 Range compris)"

step "6. Recommandations (Discovery) : pour moi, puis autour de moi"
FORME="$(get "$GATEWAY/api/discovery/v1/destinations/$DESTINATION/for-me")" || fail "GET for-me"
jq -r '.places[:5][] | "  \(.score)\t\(.name)\t(\(.why.template))"' <<<"$FORME"
[ "$(jq '.places | length' <<<"$FORME")" -ge 5 ] || fail "for-me renvoie moins de 5 lieux"
RECO="$(get "$GATEWAY/api/discovery/v1/recommendations?lat=$LAT&lng=$LON&radius=3000&limit=5")" || fail "GET recommendations"
jq -r '.items[] | "  \(.score)\t\(.name)"' <<<"$RECO"
[ "$(jq '.items | length' <<<"$RECO")" -ge 1 ] || fail "recommendations vide"
ok "recommandations reçues"

step "7. Site public (SEO) et PWA"
CODE="$(curl -sS --max-time 20 -o "$TMP/web.html" -w '%{http_code}' "$WEB_PUBLIC/fr/$DESTINATION")"
echo "  GET $WEB_PUBLIC/fr/$DESTINATION -> $CODE ($(wc -c <"$TMP/web.html") octets)"
[ "$CODE" = 200 ] || fail "site public : $CODE"
grep -qi "Vieux-Port\|Notre-Dame" "$TMP/web.html" || fail "la page publique ne liste pas les lieux du catalogue"
CODE="$(curl -sS --max-time 20 -o /dev/null -w '%{http_code}' "$PWA/")"
echo "  GET $PWA/ -> $CODE"
[ "$CODE" = 200 ] || fail "PWA : $CODE"
ok "web public et PWA"

printf '\n\033[1;32mTest de fumée réussi.\033[0m\n'
