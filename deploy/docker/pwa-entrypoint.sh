#!/bin/sh
# Writes the per-environment settings into the published appsettings.json (the PWA fetches it at start), then runs the command.
set -eu

settings=/srv/appsettings.json
tiles=${PWA_MAP_TILES_URL:-}
printf '{ "Gateway": { "BaseAddress": "" }, "Map": { "TilesUrl": "%s" } }\n' "$tiles" > "$settings"
# The precompressed variants would otherwise shadow the file just written.
rm -f "$settings.br" "$settings.gz"

exec "$@"
