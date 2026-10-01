# syntax=docker/dockerfile:1.7
# PWA (Blazor WebAssembly): published once, served as static files by Caddy (no .NET at run time). Build context: the repository root.
# The PWA reads wwwroot/appsettings.json: Gateway:BaseAddress is empty (same origin; the reverse proxy routes /api and /media to the
# gateway). Map:TilesUrl comes from the MAP_TILES_URL build argument. It is written BEFORE publishing and not at container start, because
# the service worker manifest (service-worker-assets.js) holds a SHA-256 of appsettings.json: changing the file afterwards would make the
# offline cache installation fail its integrity check. One image per environment, then.

ARG DOTNET_VERSION=10.0

FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
ARG MAP_TILES_URL=""
WORKDIR /src
COPY . .
RUN printf '{ "Gateway": { "BaseAddress": "" }, "Map": { "TilesUrl": "%s" } }\n' "${MAP_TILES_URL}" > src/Web/OnVoyage.Web.Pwa/wwwroot/appsettings.json
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/Web/OnVoyage.Web.Pwa/OnVoyage.Web.Pwa.csproj -c Release -o /out --nologo

FROM caddy:2-alpine AS final
ENV XDG_DATA_HOME=/tmp/caddy-data XDG_CONFIG_HOME=/tmp/caddy-config
COPY deploy/docker/pwa.Caddyfile /etc/caddy/Caddyfile
COPY --from=build /out/wwwroot /srv
RUN chown -R 10001:10001 /srv

USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --start-period=10s --retries=5 \
    CMD wget -q --spider http://127.0.0.1:8080/ || exit 1
CMD ["caddy", "run", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"]
